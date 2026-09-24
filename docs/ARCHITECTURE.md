# Architecture

Onion architecture, four rings, dependencies point inward. No MediatR, no generic repository, no unit-of-work
abstraction - the stored procedures already are the units of work.

```
            +-------------------------------------------------------------+
            |  Jaftim.Api (HTTP)                Jaftim.Jobs (Hangfire)     |   hosts
            |  +-------------------------------------------------------+  |
            |  |  Jaftim.Infrastructure                                 |  |   Dapper, JWT, Blob, Email, Hangfire client
            |  |  +-------------------------------------------------+  |  |
            |  |  |  Jaftim.Application                              |  |  |   services, validators, repository interfaces
            |  |  |  +-------------------------------------------+  |  |  |
            |  |  |  |  Jaftim.Domain                             |  |  |  |   entities, enums, Permissions, exceptions
            |  |  |  +-------------------------------------------+  |  |  |
            |  |  +-------------------------------------------------+  |  |
            |  +-------------------------------------------------------+  |
            +-------------------------------------------------------------+
```

## Tenancy

One database per tenant plus a shared catalog. Two executors, same class:

```
ICatalogDbExecutor  -> CatalogConnectionFactory  -> ConnectionStrings:Catalog        (Account, Tenant, sessions, auth audit)
IDbExecutor         -> TenantConnectionFactory   -> ITenantContext.TenantId -> ITenantConnectionResolver (catalog Tenant row, 5 min cache)
```
`ITenantContext` is scoped and settable (`ITenantContextSetter`): the API binds it in `TokenVersionValidator` from
the `tid` claim; Jobs bind it per run (`TenantScopeRunner.BindAsync`); background writers (audit, request audit)
carry the tenant id in their channel record and bind a fresh scope. A tenant-bound repository resolved without a
bound tenant throws - never silently reads the wrong database.

## Request flow

```
HTTP -> Serilog request log -> ExceptionHandler -> CORS -> RateLimiter -> JwtBearer (signature, expiry,
     -> TokenVersionValidator: bind tenant from tid; catalog Account.TokenVersion + IsActive + tenant UserProfile.StatusId, cached 60 s)
     -> IpAllowlistMiddleware (UAT/Prod)
     -> Authorization: [HasPermission(ActionId)] -> PermissionHandler -> CachedPermissionService (uid -> effective roles 60 s -> RoleActionMapping per role 5 min, union)
     -> Controller (binds DTO, runs FluentValidation, calls IXService)
        -> XService (orchestration: validation, branching, notifications, job enqueue, IAuditWriter after writes)
           -> IXRepository (Infrastructure): SpCall.Procedure("Name").With(...) -> IDbExecutor
              -> @CreatedBy/@CreatedAt/@CompanyId injected -> Dapper -> SQL Server stored procedure
     <- ApiResponse<T> (200) | ProblemDetails (400/401/403/404/409/422/500)
     -> RequestAuditMiddleware (channel) -> RequestAuditWriter -> SaveActionURL
```

## Layer rules

### Domain
- Table-shaped POCOs (`UserProfile`, `StockListItem`, ...). Property names = column/alias names so Dapper maps by
  convention. Use classes with setters, not positional records, for anything Dapper materialises.
- Enums/constants only for values that are **live-verified** and referenced by code (`RoleIds`, `StockCheck`,
  `StockJourneyStatus`, ...). `Permissions` is generated from `RoleAction`; never hand-edit its values.
- `AppException` hierarchy is the only way to signal a non-500 outcome from inner layers.
- Zero package references.

### Application
- One folder per module: `XContracts.cs` (DTOs + validators), `IXRepository`, `IXService` + `XService`.
- Services orchestrate; they do not know SQL. A service may call several repositories and `INotificationDispatcher`
  / `IJobScheduler`, and may use `IDbExecutor.InTransactionAsync` **only through a repository method** that takes a
  transaction scope (keep SQL knowledge in Infrastructure).
- Validators: FluentValidation, one per request DTO, invoked in the controller or service via
  `ValidateAndThrowAppAsync` -> `ValidationException` -> HTTP 400 with field errors.
- `ICurrentUser` is the actor. Never pass user ids from the client for audit purposes.

### Infrastructure
- `IDbExecutor` is the only SQL entry point. Repositories are `sealed`, take `IDbExecutor` (and `IDateTimeProvider`
  when they need timestamps), and have **one method per stored procedure**.
- Inline SQL (`SpCall.Text`) is allowed only for tables with no procedure (`AspNetUsers`,
  `SYS_DropDownsWithAuth` reads). Parameterise everything; never concatenate.
- `.WithoutAudit()` only for procedures that do not declare the audit trio. If a procedure declares them but the
  actor is not the HTTP user (background writer, anonymous login), pass them explicitly and call `.WithoutAudit()`.
- Multi-result-set procedures use `QueryMultipleAsync(call, grid => ...)`.

### Api
- Every controller derives from `ApiControllerBase` (`[ApiController] [Authorize] [Route("api/[controller]")]`).
- Every business endpoint has `[HasPermission(Permissions.X)]`. `[AllowAnonymous]` only on login/refresh/health and
  the public website endpoints (which carry their own key check).
- Success = `ApiResponse<T> { success, data, message }`. Failure = RFC 7807 ProblemDetails. Paged data =
  `PagedResult<T> { items, page, pageSize, totalRecords, totalPages, hasNext, hasPrevious }`.
- No business logic in controllers beyond binding, validation and calling one service method.

### Jobs
- One class per job under `Jobs/`, `sealed`, constructor-injected, `public Task RunAsync(...)` with `[Queue]`,
  `[DisableConcurrentExecution]`, `[AutomaticRetry]` attributes as appropriate.
- Recurring registration only in `JobsRegistry.RegisterForTenant` (per tenant; `TenantJobsRegistrarJob` reconciles). Every job takes `tenantCode` and calls `TenantScopeRunner.BindAsync` first.
- The actor is `SystemUser` (`SystemUser:UserProfileId` config). Choose a real, active staff profile.

## Cross-cutting

| Concern | Where | Notes |
|---|---|---|
| Logging | Serilog, both hosts | structured; request logging in the API; failures in the exception handler |
| Caching | `IMemoryCache` | role -> actions (5 min), user -> effective roles (60 s), auth state per account+tenant (60 s), tenant connection strings (5 min), IP allowlist (5 min), lookup whitelist (10 min); all keys include the tenant. Single-instance cache: on scale-out add a distributed cache or shorten TTLs |
| Realtime | SignalR `/hubs/notifications` | JWT via `access_token` query param; group `t{TenantId}-user-{UserProfileId}`; Azure SignalR Service for scale-out |
| Config/secrets | `appsettings.json` has shapes only | user-secrets locally, App Service settings / Key Vault in UAT/Prod |
| Audit | `IAuditWriter` -> bounded channel -> `AuditFlushService` -> tenant `AuditLog_Write`; auth events -> catalog `AuthAuditLog` synchronously | best-effort, never fails the business action |
| Health | `/health` (API: catalog SQL check) | wire to App Service health probe |
| OpenAPI | Swashbuckle: `/swagger/v1/swagger.json`, Swagger UI `/swagger`, Scalar `/scalar` (`OpenApi:Enabled`, default Development only) | XML comments from all layers; Bearer "Authorize"; each operation lists its required permission and the shared ProblemDetails responses (`src/Jaftim.Api/OpenApi/SwaggerSetup.cs`) |

## Scaling notes

- API is stateless: scale out freely; add Azure SignalR + distributed cache when you do.
- Jobs: recurring jobs are registered per tenant (`{job}:{tenantCode}`) and reconciled with the catalog every 5 min; one server is enough for current volume; more servers just compete for the same queues safely.
- SQL: unchanged from today; the rewrite adds no load beyond what the legacy app produced, and removes the Azure
  Queue round trip from the stock-status pipeline.

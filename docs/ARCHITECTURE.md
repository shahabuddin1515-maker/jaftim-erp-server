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

A service never delivers a notification or an e-mail inside the request: it queues one (`INotificationDispatcher`,
`IEmailDispatcher`) and the pipeline below delivers it in the background.

## Messaging and job pipelines

Decided 2026-10-05 (owner): notifications, e-mails and background jobs all run as **pipelines**. One mechanism,
`Pipeline<TContext>` (`Application/Abstractions/Pipeline.cs`), runs every registered `IPipelineStep<TContext>` in
**DI registration order** (`Application/DependencyInjection.cs` is where the order is defined); a step calls `next` or
returns without it to short-circuit.

```
Notifications                          Email (separate pipeline)              Background jobs
-------------                          -------------------------              ---------------
INotificationDispatcher.NotifyAsync    IEmailDispatcher.EnqueueAsync          Hangfire invokes Job.RunAsync
  -> NotificationOutbox row              -> validate, EmailOutbox row           -> IJobRunner.RunAsync(JobContext, body)
     (actor = @CreatedBy, injected)      -> signal                                 1 JobLoggingStep   (scope, timing, failure)
  -> signal (immediate Hangfire job)                                               2 JobTenantBindingStep
queue "notifications" -> API host      queue "email" -> Jobs host                  -> body
  NotificationOutboxProcessor            EmailOutboxProcessor
    claim (lease) -> steps -> settle       claim (lease) -> steps -> settle
    1 PersistNotificationStep              1 EmailGuardStep  Suppress|Redirect|Send
    2 PushNotificationStep (SignalR)       2 SendEmailStep   (SMTP)
```

**The outbox** (`database/v2/007`, `Application/Messaging/Outbox.cs`) is the queue and the record. A row is claimed
under a lease, run through the steps, then marked succeeded, failed-with-back-off (`OutboxRetryPolicy`: 30 s, 2 m,
10 m, 30 m, 1 h, 3 h) or dead-lettered (a permanent failure per `IDeliveryFailureClassifier`, or the attempt budget
spent: 5 for notifications, 8 for e-mail). Two ways in, both lease-guarded so a row is processed once:

- **immediate** - the producer's `IOutboxSignal` enqueues a per-row Hangfire job right after the insert;
- **sweep** - a per-tenant recurring job every minute leases due rows older than 30 s (missed signals, retries,
  crashed runs whose lease expired) and enqueues the same job; it also purges succeeded rows older than 14 days.
  Dead letters are kept.

Hangfire's own retries are off for these jobs (`AutomaticRetry(Attempts = 0)`): retrying is the outbox's job, so a
message's history lives in one place.

**Delivery guarantees.**
- Notifications are never duplicated. `Notification_Create` and `NotificationOutbox_MarkPersisted` run in one
  transaction, so a failure anywhere after the insert (even reading its results) rolls the notification back, and a
  retry of a persisted row skips step 1. The push is best-effort, as in legacy: a failure is logged, never retried,
  and the notification is already in the inbox.
- E-mail is at-least-once: SMTP accepting a message and the row being marked are not atomic.
- Enqueueing happens right after the business write, not inside its transaction (the existing call sites write
  through procedures that commit on their own). A crash between the two loses the message, as it did in legacy.

**Where things run.** Realtime push needs the SignalR hub, so **the API host consumes the `notifications` queue**
with a *lightweight* Hangfire server (`IsLightweightServer`: workers only - a full server would also run the
recurring/delayed schedulers and fail every `Jaftim.Jobs` job it cannot load). Everything else, the `email` queue and
both sweeps included, runs in the Jobs host. While no API instance is up, notifications wait in the outbox. Switch:
`Messaging:Notifications:ProcessInApi`. Moving the queue to the Jobs host needs a SignalR backplane both hosts can
publish to (Azure SignalR Service).

**E-mail safety.** `EmailDelivery:Mode` defaults to `Suppress` in both hosts: local and UAT databases are copies
holding real customer addresses. `Redirect` sends everything to `EmailDelivery:RedirectTo`; only Live sets `Send`.

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
- The body runs through the job pipeline: `jobs.RunAsync(new JobContext(Id, tenantCode), ct => ..., ct)`
  (`IJobRunner`). Never bind the tenant, open log scopes or time the run in the job itself - those are steps.
- Recurring registration only in `JobsRegistry.RegisterForTenant` (per tenant; `TenantJobsRegistrarJob` reconciles).
- Jobs either host can execute (the messaging jobs) live in `Infrastructure/Messaging`; Jobs-host-only jobs in
  `Jaftim.Jobs/Jobs`.
- The actor is `SystemUser` (`SystemUser:UserProfileId` config). Choose a real, active staff profile.

## Cross-cutting

| Concern | Where | Notes |
|---|---|---|
| Logging | Serilog, both hosts | structured; request logging in the API; failures in the exception handler |
| Caching | `IMemoryCache` | role -> actions (5 min), user -> effective roles (60 s), auth state per account+tenant (60 s), tenant connection strings (5 min), IP allowlist (5 min), lookup whitelist (10 min); all keys include the tenant. Single-instance cache: on scale-out add a distributed cache or shorten TTLs |
| Realtime | SignalR `/hubs/notifications` | JWT via `access_token` query param; group `t{TenantId}-user-{UserProfileId}`, taken from the connection's claims (hub methods run in a fresh DI scope where the scoped tenant context is unbound); Azure SignalR Service for scale-out |
| Notifications / e-mail | outbox pipelines (above) | `INotificationDispatcher` / `IEmailDispatcher` only; never SignalR or SMTP from a service |
| Config/secrets | `appsettings.json` has shapes only | user-secrets locally, App Service settings / Key Vault in UAT/Prod |
| Audit | `IAuditWriter` -> bounded channel -> `AuditFlushService` -> tenant `AuditLog_Write`; auth events -> catalog `AuthAuditLog` synchronously | best-effort, never fails the business action |
| Health | `/health` (API: catalog SQL check) | wire to App Service health probe |
| OpenAPI | Swashbuckle: `/swagger/v1/swagger.json`, Swagger UI `/swagger`, Scalar `/scalar` (`OpenApi:Enabled`, default Development only) | XML comments from all layers; Bearer "Authorize"; each operation lists its required permission and the shared ProblemDetails responses (`src/Jaftim.Api/OpenApi/SwaggerSetup.cs`) |

## Scaling notes

- API is stateless: scale out freely; add Azure SignalR + distributed cache when you do.
- Jobs: recurring jobs are registered per tenant (`{job}:{tenantCode}`) and reconciled with the catalog every 5 min; one server is enough for current volume; more servers just compete for the same queues safely.
- Each API instance runs a 2-worker lightweight Hangfire server for `notifications`; instances share the queue and
  the outbox lease stops double delivery. Scale-out needs Azure SignalR so a push reaches clients on other instances.
- SQL: unchanged from today; the rewrite adds no load beyond what the legacy app produced, and removes the Azure
  Queue round trip from the stock-status pipeline.

# Conventions: how to add a module / endpoint / job

Follow the reference module (`Stock` read path) exactly. Copy its shape; do not invent a new one.

## Adding an endpoint that wraps an existing stored procedure

1. **Find the legacy call.** In `C:\jaftimv2\Jaftim`, locate the controller action, then the `Jaftim_Core` repository
   method it reaches. Copy the `param` dictionary **verbatim** - parameter names, order and which ones are passed.
   Confirm the procedure name is the one actually called (watch for `_New` / `_Optimized` / `_bk` siblings).
2. **Check the procedure's real definition** (`sqlcmd`, `sys.parameters`, `OBJECT_DEFINITION`): does it declare
   `@CreatedBy/@CreatedAt/@CompanyId`? How many result sets? Any `RAISERROR`/`THROW` messages the UI relied on?
   Read it from the local copy; read UAT too when precision matters (a checked-in `.sql` can be stale or, as with
   `CustomerSave`, differ from what is deployed). **Reads only on UAT** - see below.
3. **Domain**: add/extend the row POCO under `Domain/Entities/<Module>/` with the columns the procedure emits.
4. **Application** `Modules/<Module>/`:
   - request DTO (`sealed record`) + `AbstractValidator<T>` reproducing the controller-level checks;
   - method on `I<Module>Repository`;
   - method on `I<Module>Service` + implementation (validation, orchestration, notification, job enqueue).
5. **Infrastructure** `Repositories/<Module>Repository.cs`: one method,
   `SpCall.Procedure("Name").With("@Param", value, DbType?, size?)`, `.WithoutAudit()` only when the SP has no audit
   params, `.WithTimeout(n)` for known-slow procedures.
6. **Api** controller action: `[HttpX] [HasPermission(Permissions.<Action>)]`, bind, validate, call service, return
   `Ok(result)`. Give the action a `/// <summary>` and the request record `/// <param>` lines - that text IS the
   Swagger documentation (permission and error responses are added automatically). Use the ActionId of the `_CSS_###` class the legacy view used for that button/screen.
7. **Register** nothing extra if the repository/service already exist; otherwise add them in
   `Infrastructure/DependencyInjection.cs` and `Application/DependencyInjection.cs`.
8. **Tests**: a service test with fakes for any branching logic; an API smoke test if the endpoint has special
   binding. Run `dotnet test`.
9. **Inventory**: tick the legacy action off in `docs/MIGRATION_INVENTORY.md`.

## Catalog vs tenant

- Anything about identity, tenants or sessions -> catalog: `ICatalogDbExecutor`, `Catalog_*` procedure in
  `database/catalog/`, `.WithoutAudit()` (catalog procedures never declare the legacy trio).
- Everything else -> tenant: `IDbExecutor`; the tenant is whatever the scope is bound to. Never accept a tenant id
  from a request body; it comes from the token. Jobs call `TenantScopeRunner.BindAsync(tenantCode)` first.
- After a business write, call `IAuditWriter.RecordAsync("Entity.Verb", "EntityType", id, before, after)` in the
  service. Use plain objects/records for before/after; they are serialised to JSON.

## Adding a procedure (v2-only need)

- `database/v2/NNN_<Name>.sql`, `CREATE OR ALTER`, `SET QUOTED_IDENTIFIER ON` as its own batch first, declare
  `@CreatedBy BIGINT, @CreatedAt DATETIME, @CompanyId BIGINT = NULL` as trailing params so the executor's default
  injection works.
- Never `ALTER` a procedure the legacy app calls to change its behaviour; create `Name_V2`. (`v2/006` is the one
  documented exception; `docs/DATABASE.md` rule 1 states the bar for another - clear it with the owner first.)
- Apply it to the **local** databases only, and verify with `SELECT OBJECT_DEFINITION(OBJECT_ID('Name'))`.
  **Never run a script against UAT or Live** - they are read-only during development (`docs/DATABASE.md` rule 6);
  promotion is a separate, owner-authorised release step. Add the script to the deployment package for that step.
- Apply it to **every** local tenant (`jaftim-local-db` *and* `jaftim-local-db2`), or the second tenant silently
  drifts and only fails once someone switches to it.

## Adding a background job

1. `src/Jaftim.Jobs/Jobs/<Name>Job.cs`, `sealed`, `RunAsync(... CancellationToken ct)`.
2. Attributes: `[Queue("...")]` from `JobQueues`, `[DisableConcurrentExecution(seconds)]` for anything that must not
   overlap, `[AutomaticRetry(Attempts = n, DelaysInSeconds = [...])]` (0 for minute-ticks).
3. Recurring: one `AddOrUpdate` line in `JobsRegistry`. Fire-and-forget: enqueue from a service with
   `IJobScheduler.Enqueue<TJob>(j => j.RunAsync(args, CancellationToken.None), JobQueues.X)`.
4. Jobs use the same repositories as the API. The actor is `SystemUser`.

## Naming

- Namespaces: `Jaftim.<Layer>.<Area>`; modules: `Jaftim.Application.Modules.<Module>`.
- Endpoints: plural nouns, kebab-case for multi-word actions (`/api/stocks/{id}/reservation-bids`,
  `/api/auth/change-password`). Verbs only for non-CRUD commands (`/approve`, `/reject`, `/mark-read`).
- DTOs: `<Thing>Request` / `<Thing>Response`; row POCOs named after what the SP returns (`StockListItem`).
- Permissions: use the generated constant; never a raw integer.

## Style

- File-scoped namespaces, primary constructors, `sealed` by default, nullable enabled, warnings are errors.
- Comments explain *why* and cite the legacy source (`README section 8.2`, `StockController.ConfirmApproveBid`).
- No `catch (Exception) { return null; }`. Let `AppException`s and `SqlException`s reach the handler.
- No `DateTime.Now`. `IDateTimeProvider.UtcNow` only.

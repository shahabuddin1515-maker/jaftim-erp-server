# Jaftim Backend Rewrite Plan (v2): .NET 10 API + Hangfire, Dapper, Onion Architecture

Written for: the engineers building this backend and the AI agents that will work in this repo. Dense on purpose.

This supersedes `REWRITE_PLAN.md` in the legacy repo (`C:\jaftimv2\Jaftim`), which assumed EF Core Code First, a
redesigned schema and Razor Pages. **Those decisions are reversed** by the owner (2026-09-17):

| Decision | Legacy plan (2026-07-03) | **This plan (2026-09-17)** |
|---|---|---|
| Frontend | Razor Pages in the same app | **API only** - a separate team builds the UI against this API |
| Data access | EF Core Code First | **Dapper + stored procedures, no EF** |
| Schema | Redesigned, new database, backfill migration | **Database-first on the existing schema**, additive changes only |
| Architecture | Clean Architecture + MediatR | **Onion, simple repository pattern, plain services** (no MediatR) |
| Auth | ASP.NET Identity | **JWT** (access + refresh) against the existing `AspNetUsers` rows |
| Background work | Worker Services | **One Hangfire host** replacing the 4 legacy worker/function projects |
| Tenancy (2026-09-18) | single company, `CompanyId = 1` | **Database per tenant** + shared **catalog** (identity, tenant registry, sessions); users can belong to several tenants and switch |
| Roles (2026-09-18) | one `UserProfile.RoleId` | **Multiple roles per user, optionally time-bound** (`UserRole`); `UserProfile.RoleId` stays the primary role the procedures see; permissions = union |
| Audit (2026-09-18) | trigger history tables, `ActionURlTbl` | those kept + central `AuditLog` per tenant (who/what/before/after) + catalog `AuthAuditLog`; std audit columns on every v2 table |

Everything in `README.md`, `NOTIFICATIONS.md`, `StockStatusBusinessLogicSummary.md` and `AGENTS.md` of the legacy
repo remains the authoritative description of **what the business logic is**. This document is about **where it goes**.

---

## 1. The central insight: the stored procedures ARE the backend

Live UAT inventory (2026-09-17, queried directly - the legacy README numbers are stale):

| Object | Count |
|---|---|
| Tables | 246 (193 in README) |
| Stored procedures | **405** (279 in README) |
| Views | 12 |
| Functions | 19 |
| Triggers | 21 (6 history + 13 stock-status-refresh + 2 allocation guards) |
| Foreign keys | 88 (26 in README - the Shipping/Vendor/Transport/Respond.io modules added real FKs) |
| Table types | 9 |
| Modules compiled with `QUOTED_IDENTIFIER OFF` | 58 |

The C# in `Jaftim_Core` is ~90% pass-through (`SuccessResponse(_repo.X(...))`). The real logic - status cascades,
reservation/bid/discount flow, wallet/allocation, tagging rules, approval chains, lead ingestion, row-level
visibility - is in T-SQL. Several procedures even read `RoleActionMapping` directly for row-level scoping
(`StockGetById` checks ActionIds 626/627/628 against `@CreatedBy`).

**Therefore:** the rewrite is *not* a rewrite of business logic. It is a rewrite of the **delivery layer** (MVC ->
API), the **auth layer** (cookie/Identity -> JWT), and the **job layer** (4 worker projects -> Hangfire), on top of the
**same database and the same procedures**. That is what makes "flows must work exactly as they do today" achievable:
the code that implements the flows does not change.

What *does* get rewritten in C#:
1. Controller-level validation and orchestration (e.g. `ConfirmApproveBid` deciding between `check_discounted_bid`
   and `StockReservation_ConfirmApproveBid`, `StockSave` pushing to the legacy-ERP queue, CSV parsing for bulk
   uploads, Identity user creation before `UserSave`/`CustomerSave`).
2. Everything that today lives in Razor views/JS and must become explicit API contracts (paging, filters, the
   `R_CSS` permission classes -> permission ids).
3. The four generic metadata subsystems are exposed as-is but **whitelisted**: `GetDllAuthTableValues` via
   `SYS_DropDownsWithAuth` (done), `SYS_AuthTable` generic CRUD stays server-side only (never exposed as
   "post any JSON to any table"), `SYS_AuthMethod` keyword-SP indirection is replaced by explicit endpoints.

---

## 2. Target architecture (implemented skeleton in this repo)

```
src/
  Jaftim.Domain          entities (table-shaped POCOs), enums with live-verified ids, Permissions catalog
                         (generated from RoleAction), domain exceptions.               -> no dependencies
  Jaftim.Application     ICurrentUser / IPermissionService / IJobScheduler / ... abstractions, one folder per
                         module: request/response DTOs, FluentValidation validators, IXRepository interface,
                         IXService + XService (orchestration only).                   -> Domain
  Jaftim.Infrastructure  Dapper: IDbConnectionFactory, IDbExecutor (+ SpCall builder, audit injection, tx scope),
                         repositories (one per module, one method per SP), JWT + Identity-compatible password
                         hasher, cached permissions, Blob/Email, Hangfire IJobScheduler.  -> Application
  Jaftim.Api             ASP.NET Core 10 Web API: JWT bearer + token-version check, [HasPermission] policies,
                         IP allowlist + request-audit middleware, ProblemDetails, SignalR notification hub,
                         OpenAPI (Scalar UI), health checks, rate limiting.             -> Application, Infrastructure
  Jaftim.Jobs            Hangfire server + dashboard. Recurring + queued jobs replacing StockJourneyStatusUpdates,
                         StockSync, RespondIOSync, JaftimWebhooks.                    -> Application, Infrastructure
tests/
  Jaftim.Application.Tests   unit tests with hand-rolled fakes (password hasher compatibility, auth rules)
  Jaftim.Api.Tests           WebApplicationFactory smoke tests (auth pipeline, ProblemDetails, CIDR matching)
database/v2/                 additive SQL for v2 (refresh tokens; Hangfire DB note)
docs/                        this plan, ARCHITECTURE, CONVENTIONS, AUTH, DATABASE, MIGRATION_INVENTORY, GETTING_STARTED
```

Dependency rule: arrows point inward only. `Jaftim.Application` must never reference Dapper, ASP.NET or Hangfire.
Enforced by project references and `TreatWarningsAsErrors` on every project.

Key design points (all implemented, see `docs/ARCHITECTURE.md` for detail):

- **`IDbExecutor` + `SpCall`** - the only way to hit SQL. Injects `@CreatedBy = UserProfileId`, `@CreatedAt = UtcNow`,
  `@CompanyId` on every call by default (the legacy `DBRepository` did this *inconsistently* across overloads and
  had a broken `GetSingle` that never bound parameters). Opting out is explicit: `.WithoutAudit()`.
- **Permission = `RoleAction.ActionId`**. `[HasPermission(Permissions.StockSave)]` -> `RoleActionMapping` lookup,
  cached 5 min per role. The legacy server-side check was commented out; here it is on for every endpoint
  (fallback policy = authenticated; every business endpoint additionally names a permission).
- **JWT with server-side revocation** via catalog `Account.TokenVersion`; tokens carry the tenant (`tid`) and the
  tenant-local `uid`. Full model in `docs/AUTH.md`.
- **Errors are ProblemDetails**, business-rule `RAISERROR`/`THROW` (>= 50000) from SPs surface as HTTP 422 with the
  SQL message - the legacy `catch { return null; }` pattern is gone.

---

## 3. Database strategy: shared tenant DB, additive only

> `docs/DATABASE.md` is the authority for everything in this section. Where the two disagree, DATABASE.md wins.

**The legacy MVC app and the new API run against the same tenant database throughout the migration.** No data
migration, no dual-write, no cut-over weekend. This is only possible because the schema is not being redesigned.
(Since the 2026-09-18 tenancy decision "the same database" means *the same database per tenant*, plus a separate
shared catalog for identity - see the topology table in `docs/DATABASE.md`.)

Rules (see `docs/DATABASE.md` - the summary below must not drift from it):

1. **Never** modify a table or procedure the legacy app still uses in a way that changes its behaviour. New needs
   -> new procedure (`X_V2` suffix if it replaces an existing one), new nullable column, or new table. One
   documented exception exists (`v2/006`); DATABASE.md rule 1 states the bar for another.
2. All v2 SQL lives in `database/v2/NNN_*.sql`, numbered, idempotent, `SET QUOTED_IDENTIFIER ON` (57 legacy modules
   are OFF; anything touching a table with a filtered index breaks otherwise - README section 6.3).
3. **Development applies v2 scripts to local databases only. UAT and Live are read-only** - `SELECT` /
   `OBJECT_DEFINITION` reads and bacpac exports only (owner rule, 2026-09-18; `docs/DATABASE.md` rule 6).
   Promotion to UAT and then Live is a separate, owner-authorised release step (AGENTS.md), verified with
   `sqlcmd` after it happens.
4. Schema hardening (missing FKs, `IsDeleted` type normalisation, dead tables like `RefundClaim*`, `TestList_TB`,
   `bk*`) is a **separate, later workstream** with its own data-quality audit - not part of the API rewrite.

Scripts actually present are listed in the `docs/DATABASE.md` "v2 objects" table - that table is the current one,
not this paragraph. Note that Hangfire storage lives in **its own database** on the same server
(`database/v2/002_Hangfire_Database.sql`) so job polling never competes with the 20-DTU business DB.

---

## 4. Auth: JWT against the existing tables (implemented)

Full detail in `docs/AUTH.md`. Summary:

- `POST /api/auth/login` verifies `AspNetUsers.PasswordHash` with an **Identity-V3-compatible PBKDF2 hasher**
  (no Identity package) - existing passwords work unchanged, and hashes the API writes still work for the legacy app.
  Verified on UAT: the stored hashes are V3 format.
- Access token (30 min, HS256) carries `sub` (AspNetUserId), `uid` (UserProfileId), `rid`, `role`, `utid`, `cid`,
  `tv` (token version). **Permissions are not in the token** - resolved per request from cache, so rights changes
  apply within 5 minutes, not at next login.
- Refresh token (14 days) is opaque, SHA-256-hashed at rest, rotated on use; reuse of a rotated token revokes the
  user's whole session family.
- Preserved legacy rules: inactive `UserProfile.StatusId` rejected after password check and logged to
  `LoginAttempts`; `CompanyId = 1`; per-request active check (cached 60 s); IP allowlist (`Base_WhitelistedIPs` +
  static CIDRs, bypass via `RemoteAccessAllowed`) as middleware, off in Development.
- New: account lockout ON (5 attempts / 15 min), login rate-limited per IP, password policy 8+ chars with letter+digit.

---

## 5. Background jobs: one Hangfire host (implemented skeleton)

| Legacy deployable | Legacy mechanism | v2 job | Status |
|---|---|---|---|
| StockJourneyStatusUpdates/Worker | loop 60 s -> `UpdateStockJourneyStatus` | `StockJourneyStatusJob` recurring `* * * * *` | **done** |
| StockJourneyStatusUpdates/StockStatusTimeScheduler | loop 60 s -> `StockStatusRefreshOutbox_EnqueueTimeDependent` | `StockStatusOutboxEnqueueJob` | **done** |
| StockJourneyStatusUpdates/OutboxPublisher + QueueWorker | outbox -> Azure Queue -> `StockStatus_RefreshOne` | `StockStatusOutboxDispatchJob` -> `StockStatusRefreshOneJob` (Hangfire queue `stock-status`, **no Azure queue hop**), same Claim/MarkPublished/MarkFailed contract | **done** |
| StockSync/Worker + ImagesSyncWorker | Azure Queue -> PHP Old ERP | `LegacyErpStockSyncJob.SyncStock/SyncImages`, enqueued by the API via `IJobScheduler` | skeleton |
| RespondIOSync x3 | hosted loops, `Enable*` switches | `RespondIoContactSyncJob` / `CustomerPushJob` / `ConversationSyncJob`, recurring, same switches | skeleton |
| JaftimWebhooks (Azure Function `POST /api/leads`) | bearer key in `AuthorizationKeys` -> `InsertLead` | becomes `POST /api/public/leads` on the API with the same key check | to do (Inquiry phase) |

Hangfire gives what the legacy workers lacked: a dashboard, retries with back-off, failed-job visibility (StockSync
silently dropped messages after 5 attempts), `DisableConcurrentExecution` instead of hand-rolled app locks.

Coexistence rule: **a queue has exactly one consumer**. While the legacy MVC app still produces to the Azure
`stocks`/`images` queues, keep the legacy `StockSync` worker running; the v2 job only handles stocks created/updated
through the API. The stock-status outbox is safe to run from both hosts because the DB lease (`@LeaseId`) is the lock.

---

## 6. Module-by-module plan (strangler at the API surface)

Each module = one folder in `Application/Modules/<Name>`, one repository, one or more controllers, one entry in
`docs/MIGRATION_INVENTORY.md` ticking off every legacy action. The frontend team switches screen by screen.

Order chosen by coupling and by what the frontend team needs first:

| # | Module | Legacy source | Why here | Notes |
|---|---|---|---|---|
| 0 | **Platform** (done) | BaseController, Identity, DBRepository, workers | everything depends on it | auth, permissions, executor, jobs host, notifications hub |
| 1 | **Lookups + Stock read** | AuthFilterController, StockController list/detail | first screens the UI team builds; read-only = zero risk | `GET /api/stocks`, `GET /api/stocks/{id}`, `GET /api/lookups` done; add the stock-detail sections (`Stock_GetSectionById`, images, files, folders, status detail, history tabs) |
| 2 | **Users, Roles & Rights** | UserController, NotificationSettingsController | the UI needs role admin early | **Multi-role / time-bound assignment done** (`/api/users/{id}/roles`), tenants + audit endpoints done. Remaining: user CRUD (`UserSave` creates the tenant `AspNetUsers` row via `IPasswordHasher`; the catalog picks it up via `AccountSyncJob`), role-rights save must call `IPermissionService.InvalidateRole` |
| 3 | **Inquiry / Lead / Customer Tagging** | InquiryController, CustomerTaggingController, JaftimWebhooks, PublicController | smallest blast radius, pipeline entry point | fold the Azure Function into `POST /api/public/leads`; `PublicController.InquirySave` -> `POST /api/public/inquiries` |
| 4 | **Customer core + wallet/finance requests** | CustomerController (66 actions), BankStatementController, MyTask | approval chains; forces the notification triggers to be ported | `CustomerSave` password generation must be crypto-random (legacy used a hardcoded temp password) |
| 5 | **Stock write: status, reservation, bids, discount, financials, ports/BL, files** | StockController (76 actions) | largest, most business-critical | keep `Stock_SaveStatusDetail` + `Update*_Auto` untouched; port only controller validation; enqueue `LegacyErpStockSyncJob` where the legacy pushed to the queue |
| 6 | **Shipping, Document, Inspection, Vendor, Transport, Pricing, Sales module** | respective controllers | newer modules, already SP-clean with real FKs | Transport already has `/api/transport/*` routes in the legacy app - easiest port |
| 7 | **Reporting engine** | ReportController | metadata-driven; dynamic `ProcedureName` execution | keep the engine; whitelist `ReportMaster.ProcedureName` against `sys.procedures` with an `RPT_` prefix rule |
| 8 | **Integrations (Respond.io, legacy ERP sync)** | RespondIOSync, StockSync | port worker bodies into the job skeletons | then retire the 4 legacy deployables |
| 9 | **Decommission** | Jaftim MVC | when every screen is on the new UI | remove Identity UI, EF contexts, `EF_*` controllers |

Per-module definition of done:
1. Every legacy action in the inventory has an endpoint (or a documented "dropped, dead code" note).
2. Every endpoint has `[HasPermission]` with the ActionId the legacy `_CSS_###` class used.
3. Parity check: same SP, same parameters as the legacy repository method (diff the `param` dictionary).
4. Validators reproduce the controller-level `if (...) errors.Add(...)` rules.
5. Notifications raised at the same trigger points (NOTIFICATIONS.md section 6).
6. `docs/MIGRATION_INVENTORY.md` updated.

---

## 7. Known legacy bugs - decide, do not silently carry or fix

From README section 24; each needs an explicit product decision before its module is ported:

| Bug | Module | Default in v2 |
|---|---|---|
| `Stock_RejectBid` ignores `@Stock_RB_D_Id`, rejects every discount request on the stock | 5 | preserve (SP unchanged) - flag to product |
| `ApproveBidManager` tautological WHERE, unreachable from UI | 5 | do not expose |
| `Leads.LeadId` never written to `Inquiry.LeadId` | 3 | preserve - flag to product |
| `CustomerSave`'s duplicate-phone guard never excludes `@InquiryId`, so **every** new enquirer added through the back-office form is rejected after the `Inquiry` row and login have already committed (no party is created). Live on UAT; the legacy screen hides it in `catch { return null; }` | 3 | **fixed** in `database/v2/006` - the one approved exception to additive-only. Full write-up: `docs/INQUIRIES.md` defect 0 |
| `Inquiry_TaggedFromInquiries` inserts an active `CustomerTagging` row with a NULL `CustomerId` when the inquiry has no party, and still returns "Ok" | 3 | refuse in the service (422 / counted as failed in bulk); SP unchanged. `docs/INQUIRIES.md` defect 4 |
| `CustomerTagging_TagCustomerToAgent` returns its rejections ("Limit Exceeded", divisions) as a result row; legacy notified the agent of a tag that never happened | 3 | fixed in the service: 422, no notification. `docs/INQUIRIES.md` defect 5 |
| Reassigning a customer deactivates the old tag without `ModifiedBy`, so it never appears in the previous agent's untag history | 3 | preserve - flag to product (`_V2` needed). Defect 6 |
| `CustomerTagging_AvailableCustomers` uses `NOT IN` over a nullable column: one active NULL-customer tag empties the list | 3 | preserve; v2 never creates such rows. Check UAT (query in defect 7) |
| Tagging from an inquiry (`Inquiry_TaggedFromInquiries`) skips the tag limit and division checks that `CustomerTagging_TagCustomerToAgent` enforces | 3 | preserve - flag to product |
| `Inquiry_GetSectionById` selects from `Base_InquiryType`, which does not exist in any environment - it can never have worked | 3 | do not expose; `GET /api/inquiries/{id}` returns the fields instead |
| 8 stocks fail `StockStatus_RefreshOne` (`RefreshCustomerStatus` NULL `FK_CustomerId`) | 5 | preserve - flag to product (see `docs/MIGRATION_INVENTORY.md`) |
| `RoleActionMapping` covers only 10 of 22 roles (12 roles have **zero** actions) | 2 | seed before go-live, or those roles cannot use the API at all (deny by default) |
| Hardcoded customer temp password | 4 | fix: crypto-random per user |
| Lockout disabled, 6-char passwords | 0 | fixed |
| Server-side permission check disabled | 0 | fixed |
| `BulkStockUpload` bypasses audit injection | 5 | preserve SP contract; pass audit params explicitly |
| Two `PermissionHandler` classes registered nowhere in the legacy DI | 0 | replaced |

---

## 8. Risks

1. **Roles with no `RoleActionMapping` rows** (WebAdmin, QC, Procurement, Operation, HR, Web Executive...) will be
   locked out of everything under deny-by-default. Seeding is a business task, not a code task - start it now.
2. **`StockGetAll_New` and other `_New`/`_Optimized`/`_bk` procedure pairs**: always confirm which one the legacy
   repository actually calls (README section 22) before wiring an endpoint.
3. **UTC** - the executor injects `DateTime.UtcNow` exactly as the legacy did; some SPs use `GETDATE()`. Do not "fix"
   time globally (README section 20).
4. **Hangfire on Azure SQL**: keep it in its own database; monitor DTU on first deploy.
5. **Coexistence of two producers to one Azure queue** - see section 5 rule.
6. **The `Response` envelope changes shape** - the frontend team must build against `ApiResponse<T>` +
   ProblemDetails from day one; do not offer the legacy shape as a compatibility mode.

---

## 9. How we start (the first two weeks)

> **This section is the original two-week kick-off and is kept for its rationale. It is not the current status** -
> `docs/CURRENT_STATE.md` says where the work actually stands, and `docs/MIGRATION_INVENTORY.md` is the per-action
> progress record.

1. ~~Apply v2 SQL to UAT~~ **Done differently (2026-09-18):** development runs against local bacpac copies of UAT
   (`jaftim-local-db`, `jaftim-local-db2`, `jaftim-local-catalog`, `jaftim-local-jobs`); the v2 scripts are applied
   there and the whole auth flow (login, tenant selection/switch, permissions, protected reads, refresh rotation,
   replay detection, logout-all) is verified end-to-end. UAT/Live receive the v2 scripts only as an authorised
   release step. (The original `001_AuthRefreshToken.sql` was superseded by the catalog's `AccountRefreshToken`
   before it ever shipped; `database/v2/001` is now `001_UserRole.sql`.)
2. Configure user-secrets for `Jaftim.Api` and `Jaftim.Jobs` (`docs/GETTING_STARTED.md`), run both, log in with the
   local test account, open Swagger (`/swagger`, or `/scalar`) and Hangfire (`/hangfire`).
3. Hand the frontend team: the OpenAPI document, `docs/AUTH.md`, and the `Permissions` catalog
   (`src/Jaftim.Domain/Security/Permissions.cs`) - the UI hides/shows by ActionId exactly as `_CSS_###` did.
4. Seed `RoleActionMapping` for the 12 empty roles (product task).
5. ~~Next: Module 2, then Module 3~~ Module 1 Stock read, Module 2 (users/roles/rights, navigation, permissions,
   notification routing) and the Module 3 read + add paths are done. **See `docs/CURRENT_STATE.md` for what is
   active now** - do not treat this numbered list as the current plan.
6. Set up CI (`dotnet test`) and a UAT deploy for both hosts; keep the legacy app deployed alongside.

# Current Development State

Last updated: 2026-11-24 (reporting-line lookup)

> Handoff only. This file is **not** authoritative over code, tests, the database or the other docs - if it
> disagrees with them, they win and this file is stale. Permanent knowledge belongs in the documents listed in
> `README.md`, not here.

## Current Phase / Module

**Module 3 - Inquiry, leads, tagging, public** (`docs/MIGRATION_INVENTORY.md`). Modules 0, 1 and 2 are done apart
from the leftovers listed there. A platform change was inserted on 2026-10-05 (below); Module 3 resumes next.

## Current Objective

Finish the remaining Module 3 surface: bulk inquiry import and the public endpoint. Inquiry read, add/update, single
and bulk tag/untag, and the customer tagging module are complete.

No task is mid-edit. The tree is at a clean checkpoint.

## Recently Completed

- **`GET /api/lookups/scoped/ReportingLine?roleId=`** (2026-11-24): the user form's reporting-line dropdown - users
  whose primary role is a direct `UserHierarchy` parent of `roleId` (required), active and not deleted (owner chose
  direct parents and the filter). New `database/v2/015_Lookup_ReportingLine.sql` (both local tenants). Verified:
  build clean, 170 tests pass (154 + 16); id sets equal the legacy query minus deleted/inactive users for all 25 roles
  in both tenants; live on :5150 as 12475 - role 2 -> 8, 11 -> 2, 13 -> empty, no/0 roleId 422, anonymous 401.
  `docs/USERS.md` "Reporting line".
- **`GET /api/lookups/scoped/{table}`** (2026-11-20): one dropdown source scoped to the caller (Team Lead Sales sees
  their reporting subtree in `SalesPerson_View`). The owner asked for a `roleId` parameter; it was implemented from
  the token instead, because a client-supplied role could be omitted to see everything (`docs/AUTH.md`). New
  `database/v2/014_Lookup_Scoped.sql` (both local tenants). Verified: build clean (in a scratchpad copy; the owner's
  VS session held the DLLs), 163 tests pass; id sets equal legacy for all staff callers x all sources; live on :5150
  as 12475 (5 rows), 12459 (39), 1 (40); unknown table 404, anonymous 401, `?roleId=` ignored. Side effect: local
  password `LocalTest1234` set for user 12475. Found: a `ReportsTo_UserId` cycle 12259 <-> 12475
  that breaks `fn_GetUserChildProfiles` (`docs/REWRITE_PLAN.md` section 7).
  **Revised same day (owner):** non-admins no longer see themselves (nobody below = empty list); `Customer_View` for
  roles 12/18 = customers tagged to them or their subtree. Re-verified in SQL for every staff caller; build clean,
  163 tests pass. **Then (owner):** optional `?roleId=` that replaces the token's role in the rules (stock save loads
  a list for a role); not a security boundary, see `docs/AUTH.md`. Verified: default path unchanged for every staff
  caller, override cases in SQL, build clean, 166 tests pass; live on :5150 as 12475 - no roleId 4, `roleId=1` 40,
  `roleId=10` 39, `Customer_View` 31 vs 270, `roleId=0` 422, `roleId=abc` 400.
- **`GET /api/lookups/office-locations`** (2026-11-19): `database/v2/013_Lookup_OfficeLocations.sql` (both local
  tenants). Verified: build clean, 157 tests pass; live on :5150 returns 9/85/185/186, anonymous 401.
- **Editable username on `POST`/`PUT /api/users`** (2026-11-19): new `database/v2/012_UserSave_V2.sql` (applied to
  both local tenants). Verified: build clean, 156 tests pass; live on :5150 (scratch build) - create with/without
  username, rename, omit-keeps, duplicate (`SAAD` vs existing `Saad`) 409, blank 400. Test users 23603/23604 were
  created in `jaftim-local-db` and deactivated.
- **`GET /api/users/{id}/regions/assigned`** (2026-11-19): the region tree pruned to the user's granted countries and
  their groups/divisions (`docs/USERS.md`). No SQL change (reuses `UserRegion_Get`). Verified: build clean, 152 tests
  pass; live on :5150 against `jaftim-local-db` for users 12461 (1 country -> 1 division/1 group), 132 (181 countries,
  4 of 6 divisions) and 1 (all 208), every returned country granted and none missing; unknown user -> 404.
- **Server-side paging for list endpoints** (2026-11-19, owner rule now in `docs/CONVENTIONS.md` "List endpoints"):
  `GET /api/users` and all six `GET /api/tagging/*` lists now take `page/pageSize/search/sortBy/sortDescending` and
  return `PagedResult<T>` (**breaking response shape** for any frontend that read a bare array). New
  `database/v2/010_PagedLists.sql` (four `_V2` procedures, applied to both local tenants). Verified: build clean,
  140 tests pass; `_V2` id sets equal the legacy procedures' for callers of every role branch (29 cases x 2 tenants,
  harness was a scratch script); live API run on :5150 as super admin covered paging, search, sort, `userTypeId`,
  size clamp and past-the-end. **Not exercised live:** `/api/tagging/me/*` (super admin lacks 907; same repository
  path as `agents/{id}/*`, which was). Follow-up same day (owner: "yes"): `GET /api/roles` (`v2/011`) and
  `GET /api/tenants` (`catalog/003`) paged the same way, applied locally, role ids equal legacy, live run OK.
- **Permission keys** (2026-11-18, new in v2): anonymous `GET /api/permissions/keys` - every `RoleAction` as
  KEY -> ActionId, read live (`PermissionKeyService`; the first, hard-coded version was replaced at the owner's request).
  `dotnet build` clean, 139 tests pass; live run on :5120 returned all 165 rows, `?tenantCode=acme` 200, unknown 404. Same day: pinned the transitive
  `System.Security.Cryptography.Pkcs` to 9.0.21 (new advisories against 9.0.13 broke the build; remove the pin once
  `Microsoft.Data.SqlClient` >= 7.1.2).
- **Region administration** (2026-10-05, new in v2): `/api/regions` - divisions/groups CRUD and moving countries,
  permission 905, over `database/v2/009`. Moves re-derive holders' tick rows; access never changes. `docs/USERS.md`.
- **User regions** (2026-10-05, Module 2 leftover): `GET/PUT /api/users/{id}/regions` over `UserRegion_Get`
  (`database/v2/008`) and the legacy `AssignEntitiesToUser`. Owner decisions: countries stay the only access input
  (whole divisions/groups expand at save time); no schema repair, the service validates. Model, rules and six
  defects: `docs/USERS.md`.
- **Messaging and job pipelines** (2026-10-05, owner decision): notifications and e-mail are separate durable outbox
  pipelines (`database/v2/007`), every job runs through `IJobRunner`. Design, guarantees and where each part runs:
  `docs/ARCHITECTURE.md` "Messaging and job pipelines"; recipes: `docs/CONVENTIONS.md`. `TenantScopeRunner` is gone.
  Found and fixed on the way (all silent before - see `docs/NOTIFICATIONS.md`): no realtime push had ever reached a
  client (payload mapping threw after commit; hub joined `t0-user-…`); `DbExecutor.InTransactionAsync` masked a
  procedure's own rollback with "transaction has completed".
- **Customer tagging module** and **bulk tag/untag** (2026-09-24) - `docs/INQUIRIES.md`.

## Work In Progress

Nothing is half-implemented. Not started:

| Not started | Legacy source | Notes |
|---|---|---|
| `POST /api/inquiries/bulk` | `InquiryController.BulkInquirySave` | `BulkInquiryImport_V2` (TVP) exists; a Hangfire job with progress, through `IJobRunner`. |
| `POST /api/public/inquiries` | `PublicController.InquirySave` | Anonymous; needs a captcha/rate-limit decision. |

**Explicitly not scheduled:** porting the `JaftimWebhooks` Azure Function / lead ingestion (owner, 2026-09-23).

## Current Verification State

Latest (2026-11-24, in place): `dotnet build` - **no warnings, no errors**; `dotnet test` - **170 passing**
(154 Application + 16 Api), 0 failing.

Pipeline verification from 2026-10-05 (still the latest live run of these parts):
- `v2/007`, `v2/008`, `v2/009` applied to **both** local tenants (`007` re-run twice - idempotent).
- **Region admin live run** (scratch API on :5140, super admin): create division/group, duplicate name 409, group
  under a group 422, rename, delete non-empty 422; moved Zimbabwe (12 holders) into the test group -> all 12 got
  exactly its new group+division ticks, holders and access unchanged; moved the group to Africa and Zimbabwe back,
  deleted both, 404/400 cases. Then **fully reverted**: `UserEntities` restored from a backup (0 rows differ, backup
  table dropped), Zimbabwe's row restored, test `Entity` rows deleted and identity reseeded to 29, audit rows deleted.
- **Regions live run** (tenant `jaftim`, super admin): GET of 12461 shows its legacy drift (Africa ticked, Eastern
  Africa not); PUT for staff user 135 (whole Oceania + whole Western Europe + Armenia) stored 26 countries and
  exactly the 6 ancestor rows; `fn_GetUserAccessibleCountries(135)` = 26; the legacy read procedure shows no country
  under an unticked group; invalid ids -> one 422 listing all; `0` -> 400; customer -> 422; unknown user -> 404;
  `{}` cleared 135 back to zero rows. Audit rows deleted (`AuditLog` max 40 again).
- **Live run, both hosts, tenant `jaftim`:** `POST /api/tagging/untag` as the super admin -> outbox row persisted as
  actor 1 and pushed; a real SignalR client signed in as `testsysadmin@gmail.com` (12459, a recipient) received it
  ~0.7 s after the request with all 12 payload fields. Rows inserted straight into the outboxes (no signal) were
  picked up by the sweep: a notification delivered + pushed, an unknown type code dead-lettered on attempt 1 with the
  real SQL error, an e-mail processed by the Jobs host and suppressed. Hangfire servers: API = `notifications` only
  (lightweight), Jobs = everything else. **All test rows deleted** (baselines restored: `Notification` max 94,
  `AuditLog` max 40, `CustomerTagging` max 265, outboxes empty).

Not verified / no coverage:

- **No real SMTP send** (no SMTP settings locally) and Redirect mode only in unit tests.
- The rollback-masking fix is unit-reasoned, not exercised live (needs a procedure failing after its own BEGIN TRAN).
- `jaftim-local-db2` never ran a notification or e-mail through the pipeline. Scale-out (two API instances) untried.
- No integration test hits a real database - every automated test uses fakes.

Local-only side effects of this session: `tools/set-local-password.ps1` set `LocalTest1234` for
`testsysadmin@gmail.com`; the first API start (before `IsLightweightServer`) re-scheduled Hangfire jobs 159/160 (two
of the known failing `StockStatus_RefreshOne` stocks) under default retry - harmless, they fail as before.

## Open Questions / Blockers

No blockers. Owed by the owner/product, not blocking:

1. **Release step for `v2/007`, `v2/008`, `v2/009`** (`008`/`009` only for the region endpoints): every notification now goes through `NotificationOutbox`, so UAT/Live need `007`
   **before** this build is deployed there (without it notifications are logged as unqueueable and lost), and the API
   App Service needs `ConnectionStrings:Hangfire` (it now runs a Hangfire server). Live alone sets
   `EmailDelivery:Mode = Send`. Still pending from before: promoting `v2/006`. **`v2/010`-`v2/014` and `catalog/003` are now
   required too**: without them `GET /api/users`, every tagging list, `GET /api/roles`, `GET /api/tenants`,
   `POST`/`PUT /api/users`, `GET /api/lookups/office-locations` and `GET /api/lookups/scoped/{table}` fail; `v2/015`
   is needed for the `ReportingLine` source.
2. 12 roles have zero `RoleActionMapping` rows (query in `docs/DATABASE.md`).
3. Tagging product questions (`docs/REWRITE_PLAN.md` §7).
4. **Reporting-line cycle** 12259 <-> 12475: `UserGetAll`, tagging and inquiry lists error for those callers. Fix the
   two rows on UAT/Live (owner) and decide whether saving a user should reject a cycle.
5. Which other screens should switch to the scoped lookup is a frontend/product call.
6. Office-location id spaces: the new lookup returns `Base_Country` ids, the stored `OfficeLocation_CountryId` is
   `Base_OriginCountry` ids. Whether to migrate the column is an owner decision (`docs/USERS.md` "Office location").

## Next Actions

0. **Apply `database/v2/014_Lookup_Scoped.sql` to UAT** (owner authorised 2026-11-20; it depends only on legacy
   objects; `015` is not yet authorised for UAT). Blocked on credentials: none are configured for this repo. Run it with `sqlcmd -I -b`, then verify with
   `OBJECT_DEFINITION(OBJECT_ID('dbo.Lookup_GetScoped'))` and check UAT for the 12259/12475 cycle.
1. **Port bulk inquiry import** (`InquiryController.BulkInquirySave`) as a Hangfire job over `BulkInquiryImport_V2`
   (TVP) with progress, written the pipeline way (`IJobRunner`; notify through `INotificationDispatcher`). Start by
   reading the legacy action, the procedure and its table type, and how the legacy screen reports per-row errors.
2. Decide captcha/rate-limiting, then port `POST /api/public/inquiries`.
3. Optional: an admin read/requeue endpoint over outbox dead letters (today: `SELECT ... WHERE Status = 2`).
4. Then the remaining Module 2 leftovers (`OrgChart`, user sections/stocks/image) or Module 4 (its `SendResetLink`
   is the first real `IEmailDispatcher` caller).
5. Product, not blocking: tell the region admins that user 12461's Zimbabwe grant will be dropped if they save that
   user on the **legacy** screen (`docs/USERS.md` defect 5); saving through v2 keeps it.

## Working Tree / Git Context

Under git. Remote `https://github.com/shahabuddin1515-maker/jaftim-erp-server` (**public**). Work on `dev` only; the
owner merges onwards - see "Branching and pushing" in `CLAUDE.md`. The local databases are not version-controlled:
check the script table in `docs/DATABASE.md` against them before trusting this handoff.

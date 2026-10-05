# Current Development State

Last updated: 2026-10-05 (messaging and job pipelines)

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

Verified on 2026-10-05 in this repository:

- `dotnet build` - **no warnings, no errors**. `dotnet test` - **118 passing** (109 Application + 9 Api), 0 failing
  (37 new: pipeline order/short-circuit, retry policy, processor settle paths, no-duplicate persist, push best-effort,
  e-mail guard modes and SMTP classification, address rules, job tenant binding).
- `v2/007` applied to **both** local tenants, re-run twice (idempotent).
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

1. **Release step for `v2/007`**: every notification now goes through `NotificationOutbox`, so UAT/Live need `007`
   **before** this build is deployed there (without it notifications are logged as unqueueable and lost), and the API
   App Service needs `ConnectionStrings:Hangfire` (it now runs a Hangfire server). Live alone sets
   `EmailDelivery:Mode = Send`. Still pending from before: promoting `v2/006`.
2. 12 roles have zero `RoleActionMapping` rows (query in `docs/DATABASE.md`).
3. Tagging product questions (`docs/REWRITE_PLAN.md` §7).

## Next Actions

1. **Port bulk inquiry import** (`InquiryController.BulkInquirySave`) as a Hangfire job over `BulkInquiryImport_V2`
   (TVP) with progress, written the pipeline way (`IJobRunner`; notify through `INotificationDispatcher`). Start by
   reading the legacy action, the procedure and its table type, and how the legacy screen reports per-row errors.
2. Decide captcha/rate-limiting, then port `POST /api/public/inquiries`.
3. Optional: an admin read/requeue endpoint over outbox dead letters (today: `SELECT ... WHERE Status = 2`).
4. Then Module 2 leftovers or Module 4 (its `SendResetLink` is the first real `IEmailDispatcher` caller).

## Working Tree / Git Context

Under git. Remote `https://github.com/shahabuddin1515-maker/jaftim-erp-server` (**public**). Work on `dev` only; the
owner merges onwards - see "Branching and pushing" in `CLAUDE.md`. The local databases are not version-controlled:
check the script table in `docs/DATABASE.md` against them before trusting this handoff.

# Current Development State

Last updated: 2026-09-24 (customer tagging module ported)

> Handoff only. This file is **not** authoritative over code, tests, the database or the other docs - if it
> disagrees with them, they win and this file is stale. Permanent knowledge belongs in the documents listed in
> `README.md`, not here.

## Current Phase / Module

**Module 3 - Inquiry, leads, tagging, public** (`docs/MIGRATION_INVENTORY.md`). Modules 0, 1 and 2 are done apart
from the leftovers listed there.

## Current Objective

Finish the remaining Module 3 surface. Inquiry read, add/update, single and bulk tag/untag, and the customer
tagging module are complete; what is left is bulk inquiry import and the public endpoint.

No task is mid-edit. The tree is at a clean checkpoint.

## Recently Completed

- **Customer tagging module** (2026-09-24): `/api/tagging` - agents, an agent's customers/history, `/me/...`
  (907), available customers, tag, untag. New `Tagging` module (`TaggingService`, `TaggingRepository`); it now owns
  every `CustomerTagging` write outside the inquiry procedure, and inquiry bulk-untag goes through it. Rules,
  visibility and defects 5-7: `docs/INQUIRIES.md` → "Customer tagging".
- **Bulk tag/untag** (2026-09-24): `POST /api/inquiries/tag-bulk`, `/untag-bulk`; the single inquiry tag now
  notifies and refuses an unlinked inquiry (defect 4).
- **Inquiry add path** (2026-09-23) and `database/v2/006` (the `CustomerSave` guard repair).

## Work In Progress

Nothing is half-implemented. Not started:

| Not started | Legacy source | Notes |
|---|---|---|
| `POST /api/inquiries/bulk` | `InquiryController.BulkInquirySave` | `BulkInquiryImport_V2` (TVP) exists; wants a Hangfire job with progress. |
| `POST /api/public/inquiries` | `PublicController.InquirySave` | Anonymous; needs a captcha/rate-limit decision. |

**Explicitly not scheduled:** porting the `JaftimWebhooks` Azure Function / lead ingestion. Owner instruction
(2026-09-23): it keeps running as-is and the ingestion chain stays untouched. Do not start it without being asked.

## Current Verification State

Verified on 2026-09-24 in this repository:

- `dotnet build` - **no warnings, no errors**. `dotnet test` - **81 passing** (72 Application + 9 Api), 0 failing.
- Local databases: `v2/001`-`006` present in both `jaftim-local-db` and `jaftim-local-db2`; the `v2/006` patch
  marker confirmed in `CustomerSave` in both.
- **Tagging module run against the local API, tenant `jaftim`, as the super admin:** agents (29), agent 133's
  customers (56) and history (6), available (851 → 849 after two tags), limit-0 agent → 422, agent with no
  divisions → 422, limit-1 agent: first tag 200 then 422, NULL-limit agent → 200, id 0 → 400, untag → 200 and
  shows in history, `/me/customers` → 403 (super admin lacks 907, as seeded). Database: tag rows, 3 audit rows,
  3 notifications (none for rejections). Defect 6 reproduced. **All test rows deleted afterwards** (baselines:
  `CustomerTagging` max 265, `Notification` max 94, `AuditLog` max 40).
- **As Sales Executive 149** (role 2): `/me/customers` → 31 (= its active tags), `/me/history` → 0, `/agents` →
  200 empty (149 has no divisions), 551/552/550 routes → 403 (local role 2 holds only 526 and 907). The
  procedure-level "self only" filter is therefore unreachable for role 2 through the API today.
  Local-only side effect: `tools/set-local-password.ps1` set `LocalTest1234` for `faizan1812@jaftim.com` (149) and
  `smaffan1589@jaftim.com` (133, whose catalog account is inactive - login still refused).
- **Bulk inquiry tag/untag** run the same way earlier the same day (partial success, 422, 400), rows cleaned up.

Not verified / no coverage:

- No integration test hits a real database - every test uses fakes. The procedure calls are covered only by the
  manual runs above.
- `jaftim-local-db2` has never had an inquiry created or tagged against it.
- UAT not checked for active NULL-customer tags (defect 7 query, read-only).

## Open Questions / Blockers

No blockers. Owed by the owner/product, not blocking:

1. **Promoting `database/v2/006` to UAT/Live** - until then every new enquirer added on UAT/Live still fails.
2. 12 roles have zero `RoleActionMapping` rows, so they are locked out of the API. Seed before go-live
   (query in `docs/DATABASE.md`).
3. Tagging product questions (`docs/REWRITE_PLAN.md` §7): should inquiry-path tagging enforce the limit and
   divisions; should reassignment show in the previous agent's history / notify them (needs a `_V2` procedure).

## Important Recent Discoveries

Already in the permanent docs; delete from here once no longer recent.

1. `CustomerTagging_TagCustomerToAgent` returns rejections as a result row; legacy notified on them (defect 5).
   A NULL `CustomerTagLimit` means unlimited.
2. Two tagging paths, two rule sets: the inquiry path skips the limit and division checks.
3. Reassignment is invisible in the previous agent's history (defect 6); one NULL-customer tag would empty
   Available Customers (defect 7).
4. `GetInquiryAll` does not return unlinked inquiries, so `GET /api/inquiries/{id}` is 404 for them.

## Relevant Files

For the next objective (bulk inquiry import):

- `C:\jaftimv2\Jaftim\Jaftim\Controllers\InquiryController.cs` - `BulkInquirySave` and its view/JS.
- `BulkInquiryImport_V2` and its table type (read the definition locally first).
- `src/Jaftim.Jobs/` and `docs/CONVENTIONS.md` "Adding a background job" - the job recipe.
- `src/Jaftim.Application/Modules/Inquiries/InquirySaveService.cs` - the single-row add rules to stay consistent with.

## Next Actions

1. **Port bulk inquiry import** (`InquiryController.BulkInquirySave`) as a Hangfire job over
   `BulkInquiryImport_V2` (TVP) with progress. Start by reading the legacy action, the procedure and its table
   type, and how the legacy screen reports per-row errors.
2. Decide captcha/rate-limiting, then port `POST /api/public/inquiries`.
3. Optional: page/search `GET /api/tagging/available-customers` in memory if the UAT row count makes the full list
   too heavy (check the count on UAT, read-only).
4. Then Module 2 leftovers (user sections/stocks/image, entities/hierarchy/org-chart) or Module 4, owner's choice.

## Working Tree / Git Context

Under git since 2026-09-24. Remote: `https://github.com/shahabuddin1515-maker/jaftim-erp-server` (**public**).
Work on `dev` only; the owner merges to `staging`/`master` - see "Branching and pushing" in `CLAUDE.md`. Never
commit a real credential (`docs/GETTING_STARTED.md`).

The local databases are not version-controlled: check the script table in `docs/DATABASE.md` against them before
trusting this handoff. The legacy reference repo `C:\jaftimv2\Jaftim` is not a git repository.

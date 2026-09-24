# Current Development State

Last updated: 2026-09-24 (bulk tag/untag ported)

> Handoff only. This file is **not** authoritative over code, tests, the database or the other docs - if it
> disagrees with them, they win and this file is stale. Permanent knowledge belongs in the documents listed in
> `README.md`, not here.

## Current Phase / Module

**Module 3 - Inquiry, leads, tagging, public** (`docs/MIGRATION_INVENTORY.md`). Modules 0, 1 and 2 are done apart
from the leftovers listed there.

## Current Objective

Finish the remaining Module 3 surface. Inquiry read, add/update, single tag and bulk tag/untag are complete; what
is left is the `CustomerTagging` screens, bulk import, and the public endpoint.

No task is mid-edit. The tree is at a clean checkpoint.

## Recently Completed

- **Bulk tag/untag** (2026-09-24): `POST /api/inquiries/tag-bulk`, `/untag-bulk` (perm 550). Loops the single-row
  procedures, dedupes (tag by inquiry, untag by customer+agent), partial success, 422 only when nothing succeeded.
  The single-row `POST /api/inquiries/{id}/tag` now also sends `CUSTOMER_TAGGED` (it was missing) and refuses an
  inquiry with no party. Contract and v2 departures: `docs/INQUIRIES.md` → "Tagging"; new defect 4 there.
- **Inquiry add path** (2026-09-23) and `database/v2/006` (the `CustomerSave` guard repair).
- Earlier in Module 3: inquiry list/detail, party classification (`database/v2/005`), party
  sections/inquiries/interactions, contact-status, `PartyKindSyncJob`.

## Work In Progress

Nothing is half-implemented. Not started:

| Not started | Legacy source | Notes |
|---|---|---|
| `CustomerTaggingController.*` | same | Procedures exist: `CustomerTagging_GetAllManagerAgent`, `_AgentTaggedCustomer`, `_AgentUnTaggedCustomerHistory`, `_AvailableCustomers`, `_TagCustomerToAgent`, `_UnTagCustomerFromAgent`. Reuse `IInquiryRepository.UntagCustomerFromAgentAsync` and the notification shapes in `InquiryService`. |
| `POST /api/inquiries/bulk` | `InquiryController.BulkInquirySave` | `BulkInquiryImport_V2` (TVP) exists; wants a Hangfire job with progress. |
| `POST /api/public/inquiries` | `PublicController.InquirySave` | Anonymous; needs a captcha/rate-limit decision. |

**Explicitly not scheduled:** porting the `JaftimWebhooks` Azure Function / lead ingestion. Owner instruction
(2026-09-23): it keeps running as-is and the ingestion chain stays untouched. Do not start it without being asked.

## Current Verification State

Verified on 2026-09-24 in this repository:

- `dotnet build` - **no warnings, no errors**. `dotnet test` - **73 passing** (64 Application + 9 Api), 0 failing.
- Local databases: `v2/001`-`006` present in both `jaftim-local-db` and `jaftim-local-db2`; the `v2/006` patch
  marker confirmed in `CustomerSave` in both.
- **Bulk tag/untag run against the local API, tenant `jaftim`:** partial tag (2 of 3, unlinked inquiry 1488
  failed as 404), all-unlinked → 422, agent 0 → 400, untag with a duplicate pair → 2 calls, invalid untag → 400.
  Confirmed in the database: `CustomerTagging` rows, `Inquiry.AssignedTo`, 4 `AuditLog` rows, notifications to the
  agent (+ System Admins, by the type's `IncludeSystemAdmins`). **All test rows were deleted afterwards** and
  `AssignedTo` restored to NULL (baselines: `CustomerTagging` max 265, `Notification` max 94, `AuditLog` max 40).

Not verified / no coverage:

- No integration test hits a real database - every test uses fakes. `InquirySaveRepository.SaveAsync`'s
  transaction and the bulk tag procedure calls are covered only by the manual runs above.
- `jaftim-local-db2` has never had an inquiry created or tagged against it.
- The outage-rethrow branch of bulk tag is unit-tested only (not provoked against a real database).

## Open Questions / Blockers

No blockers. Owed by the owner/product, not blocking:

1. **Promoting `database/v2/006` to UAT/Live** - until then every new enquirer added on UAT/Live still fails.
2. 12 roles have zero `RoleActionMapping` rows, so they are locked out of the API. Seed before go-live
   (query in `docs/DATABASE.md`).

## Important Recent Discoveries

Already in the permanent docs; delete from here once no longer recent.

1. `Inquiry_TaggedFromInquiries` and `CustomerTagging_UnTagCustomerFromAgent` **always** return "Ok" - the legacy
   "failed" count could only ever come from an exception. An unlinked inquiry gets a NULL-customer tag
   (`docs/INQUIRIES.md` defect 4, `REWRITE_PLAN.md` §7).
2. `GetInquiryAll` does not return unlinked inquiries (observed for 1488), so `GET /api/inquiries/{id}` is 404 for
   them - worth remembering when a "missing" inquiry is reported.
3. `CustomerSave` rejected every new enquirer (fixed by `v2/006`; `docs/INQUIRIES.md` defect 0) - the one approved
   exception to additive-only.

## Relevant Files

For the next objective (`CustomerTaggingController`):

- `C:\jaftimv2\Jaftim\Jaftim\Controllers\CustomerTaggingController.cs` - the legacy actions.
- `src/Jaftim.Application/Modules/Inquiries/InquiryContracts.cs` - `InquiryService` tag/untag + notifications.
- `src/Jaftim.Infrastructure/Repositories/InquiryRepository.cs` - the tag/untag procedure calls.
- `docs/MIGRATION_INVENTORY.md` Module 3 row for the planned `/api/tagging/*` routes and permissions 526/550-553.

## Next Actions

1. **Port `CustomerTaggingController`** - `GET /api/tagging/agents`, `/agents/{id}/customers`,
   `/agents/{id}/history`, `/available-customers`; `POST /api/tagging/tag`, `/untag` (the six `CustomerTagging_*`
   procedures). Read the legacy controller first to confirm the permission per action (inventory says 526/550-553)
   and reuse the tag/untag notification shapes. Consider moving tagging into its own service at that point.
2. Port bulk inquiry import as a Hangfire job over `BulkInquiryImport_V2` (TVP) with progress.
3. Decide captcha/rate-limiting, then port `POST /api/public/inquiries`.
4. Then Module 2 leftovers (user sections/stocks/image, entities/hierarchy/org-chart) or Module 4, owner's choice.

## Working Tree / Git Context

Under git since 2026-09-24. Remote: `https://github.com/shahabuddin1515-maker/jaftim-erp-server` (**public**).
Work on `dev` only; the owner merges to `staging`/`master` - see "Branching and pushing" in `CLAUDE.md`. Never
commit a real credential (`docs/GETTING_STARTED.md`).

The local databases are not version-controlled: check the script table in `docs/DATABASE.md` against them before
trusting this handoff. The legacy reference repo `C:\jaftimv2\Jaftim` is not a git repository.

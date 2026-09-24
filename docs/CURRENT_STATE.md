# Current Development State

Last updated: 2026-09-24

> Handoff only. This file is **not** authoritative over code, tests, the database or the other docs - if it
> disagrees with them, they win and this file is stale. Permanent knowledge belongs in the documents listed in
> `README.md`, not here.

## Current Phase / Module

**Module 3 - Inquiry, leads, tagging, public** (`docs/MIGRATION_INVENTORY.md`). Modules 0, 1 and 2 are done apart
from the leftovers listed there.

## Current Objective

Finish the remaining Module 3 surface. The inquiry **read** path and the inquiry **add/update** path are complete;
what is left is bulk import, bulk tag/untag, the `CustomerTagging` screens, and the public/lead-facing endpoints.

No task is mid-edit. The tree is at a clean checkpoint.

## Recently Completed

- **Inquiry add path** (2026-09-23): `POST /api/inquiries`, `PUT /api/inquiries/{id}`,
  `GET /api/inquiries/check-email`, `GET /api/inquiries/check-phone`. `InquirySave` + the `AspNetUsers` login +
  `CustomerSave` + the `Inquiry.UserProfileId` link run in **one transaction**.
- **`database/v2/006_CustomerSave_InquiryGuard.sql`** - repairs the legacy defect that rejected every new enquirer
  (see Important Recent Discoveries). Applied to both local tenants.
- Earlier in Module 3: inquiry list/detail, party classification (`UserProfile.PartyKind`, `database/v2/005`),
  party sections/inquiries/interactions, contact-status, single tag, `PartyKindSyncJob`.

## Work In Progress

Nothing is half-implemented. The next items have not been started:

| Not started | Legacy source | Notes |
|---|---|---|
| `POST /api/inquiries/tag-bulk`, `/untag-bulk` | `InquiryController.TaggedFromInquiriesBulk` / `UnTagFromInquiriesBulk` | **No bulk procedure exists** - the legacy actions loop in C#, and tag/untag are keyed differently. Contract documented in `docs/INQUIRIES.md` → "Tagging, and why bulk tag and bulk untag are not symmetric". Smallest next unit. |
| `POST /api/inquiries/bulk` | `InquiryController.BulkInquirySave` | `BulkInquiryImport_V2` (TVP) exists; wants a Hangfire job with progress. |
| `CustomerTaggingController.*` | same | Procedures exist: `CustomerTagging_GetAllManagerAgent`, `_AgentTaggedCustomer`, `_AgentUnTaggedCustomerHistory`, `_AvailableCustomers`, `_TagCustomerToAgent`, `_UnTagCustomerFromAgent`. |
| `POST /api/public/inquiries` | `PublicController.InquirySave` | Anonymous; needs a captcha/rate-limit decision. |

**Explicitly not scheduled:** porting the `JaftimWebhooks` Azure Function / lead ingestion. Owner instruction
(2026-09-23): it keeps running as-is and the ingestion chain stays untouched. Do not start it without being asked.

## Current Verification State

Verified on 2026-09-24 in this repository:

- `dotnet build` - **succeeds, no warnings, no errors**.
- `dotnet test` - **69 passing** (60 `Jaftim.Application.Tests` + 9 `Jaftim.Api.Tests`), 0 failing, 0 skipped.
- Local databases: `v2/001`-`006` present in **both** `jaftim-local-db` and `jaftim-local-db2`; the `v2/006` guard
  fix confirmed in both. Catalog has 2 active tenants (`jaftim`, `acme`), 1360 accounts, 2720 memberships.
- UAT audited read-only: **no v2 objects, nothing modified**, `CustomerSave` still unpatched there.

Verified on 2026-09-23 by running the API locally (not re-run since; no code has changed since):

- The five inquiry-create scenarios in `docs/GETTING_STARTED.md` section 4, including the 422 rollback leaving
  **zero** new rows across `Inquiry`/`AspNetUsers`/`UserProfile`.

Not verified / no coverage:

- No integration test hits a real database - every test uses hand-rolled fakes. The transactional create path in
  `InquirySaveRepository.SaveAsync` is therefore covered only by the manual run above. Re-run it after touching
  that method.
- `jaftim-local-db2` has never had an inquiry created against it (the manual run used tenant `jaftim` only).

## Open Questions / Blockers

No blockers. Two decisions are owed by the owner/product, but neither blocks the next action:

1. **Promoting `database/v2/006` to UAT/Live.** It also fixes the legacy app's own add-inquiry screen. Until it is
   promoted, every new enquirer added on UAT/Live still fails. Promotion is an owner-authorised release step.
2. 12 roles have zero `RoleActionMapping` rows, so they are locked out of the API (deny-by-default). Seed before
   go-live - query in `docs/DATABASE.md`.

## Important Recent Discoveries

Both have already been written into permanent docs; kept here only because they are recent and change how the next
session should behave. Delete from this file once they are no longer "recent".

1. **`CustomerSave` rejects every new enquirer, live on UAT today.** Its duplicate-phone guard promises in a
   comment to exclude `@InquiryId` and never does, so the just-inserted inquiry matches itself. The legacy screen
   hides it in `catch (Exception ex) { return null; }`, leaving an orphan inquiry + login and no party. Fixed for
   v2 by `database/v2/006`. Full write-up: `docs/INQUIRIES.md` defect 0. **This is the one approved exception to
   the additive-only rule** (`docs/DATABASE.md` rule 1) - do not treat it as a precedent without asking.
2. **Error-message casing distinguishes the two procedures**: `InquirySave` raises "Duplicate **I**nquiry...",
   `CustomerSave` raises "Duplicate **i**nquiry...". Useful when a 422 could come from either.

## Relevant Files

Only for the current objective (bulk tag/untag next):

- `src/Jaftim.Api/Controllers/InquiriesController.cs` - where the bulk endpoints go; `TagToAgent` is the pattern.
- `src/Jaftim.Application/Modules/Inquiries/InquiryContracts.cs` - `IInquiryService` / `IInquiryRepository`.
- `src/Jaftim.Infrastructure/Repositories/InquiryRepository.cs` - `TagToAgentAsync` is the single-row call.
- `docs/INQUIRIES.md` - the tagging contract and the Customer/Contact model.
- `C:\jaftimv2\Jaftim\Jaftim\Controllers\InquiryController.cs` lines ~432-520 - the legacy bulk actions.

## Next Actions

1. **Port bulk tag/untag** - `POST /api/inquiries/tag-bulk` and `/untag-bulk`, permission 550, following the
   contract in `docs/INQUIRIES.md` (loop the single-row procedures, dedupe, partial success with `tagged`/`failed`
   counts, 400 only when nothing succeeded, `CUSTOMER_TAGGED`/`CUSTOMER_UNTAGGED` notifications). Add service
   tests for the dedupe and partial-failure branches. Tick the row in `docs/MIGRATION_INVENTORY.md`.
2. Port `CustomerTaggingController` (the six `CustomerTagging_*` procedures above).
3. Port bulk inquiry import as a Hangfire job over `BulkInquiryImport_V2` (TVP) with progress.
4. Decide captcha/rate-limiting, then port `POST /api/public/inquiries`.
5. Then Module 2 leftovers (user sections/stocks/image, entities/hierarchy/org-chart) or Module 4, owner's choice.

## Working Tree / Git Context

**This repository is not under version control** - `C:\jaftimv2\JaftimBackend` has no `.git`, so there is no
`git status`, no history and no commits to compare against. A `.gitignore` exists but is unused.

Consequences for a fresh session:
- The startup protocol's "inspect git status / recent history" steps cannot be performed here. Reconcile against
  the **filesystem, the local databases and `dotnet build && dotnet test`** instead.
- There is no way to see what a previous session changed, and no way to roll back. Be correspondingly careful with
  destructive edits, and prefer additive changes.
- Initialising a repository would remove this whole class of risk, but that is the owner's call and has not been
  asked for. Flagged, not actioned.

The legacy reference repo `C:\jaftimv2\Jaftim` is likewise not a git repository.

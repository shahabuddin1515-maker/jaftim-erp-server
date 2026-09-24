# Inquiries, parties, and the Customer/Contact distinction

## The three things the word "contact" used to mean

The legacy system overloads one word. v2 names them apart:

| v2 name | What it is | Where it lives |
|---|---|---|
| **Party** | a person/organisation with the customer role - a **Contact** while unqualified, a **Customer** once qualified | `UserProfile` (RoleId 3) + `UserProfileDetail` |
| **Inquiry** | an enquiry **event** ("I want this car"), with a snapshot of the enquirer's details as received | `Inquiry` |
| **Interaction** | a **touchpoint log** entry ("we called / WhatsApp'd / e-mailed them") | `CustomerContact` |

## Customer vs Contact

A **Contact** is an unqualified, lead-stage party; a **Customer** is a qualified one. "Qualified" is the rule the
legacy code applies - a real name, a validly-shaped e-mail, and a phone:

```
unqualified  <=>  FullName = 'N/A'  OR  Email IS NULL  OR  Email NOT LIKE '%_@_%._%'
                  OR Phone IS NULL  OR  LTRIM(RTRIM(Phone)) = ''
```

Most contacts come from the lead / Respond.io pipeline, which sets the e-mail to the phone digits when no real
address was supplied - so they fail the e-mail shape test by construction. On the reference data: **242 customers,
778 contacts**.

> The legacy `README.md` §4 describes `UserProfile.IsContact` as "a secondary contact person under a business
> customer". That is **wrong**: no such column exists in the database, and the flag is the data-quality expression
> above, re-computed inside `CustomerProfileGetById`, `GetCustomerAllNew` and `GetInquiryAll`. `GetCustomerAllNew`
> hides `IsContact = 1`, which is why leads never show up in the Customers list.

### What v2 changed

The expression became **`UserProfile.PartyKind`** (`1 = Contact`, `2 = Customer`) - stored, indexed, defined once
(`database/v2/005_PartyKind_And_Inquiry.sql`):

- **Stored and indexed**, so "show me all contacts" is a seek instead of a non-SARGable string test in three places.
- **Auto-maintained**: the API re-applies the rule after its own writes; `PartyKindSyncJob` (every 5 min, per tenant)
  catches everything written by the lead pipeline, Respond.io and the legacy app.
- **Overridable**: `PartyKindIsManual` pins a human decision ("this IS a customer, we just have no e-mail"); the rule
  then leaves the row alone until the pin is released.
- **Qualification is an event**: crossing Contact -> Customer writes `Customer.Qualified` to `AuditLog`, with
  `PartyQualifiedAtUtc` recording when it first happened.

The legacy `CASE` expressions are untouched, so every existing screen and report keeps its current numbers. Verified:
0 mismatches between the stored column and the legacy rule across all customer-role parties.

## Snapshot vs current truth

An inquiry keeps the details **as received**; the party holds **current** truth. They legitimately differ:

```
Inquiry 393 : FullName "__sheby", Email "676765568"      <- pseudo-email at lead time
Party 12620 : FullName "__sheby", Email "nnsgjkjk@gmail.com", Phone 255676765568 -> PartyKind = Customer
```

So `GET /api/inquiries` rows carry both: the inquiry fields, plus `userProfileId` and `isContact` for the party.

## Adding an inquiry

`POST /api/inquiries` reproduces the legacy `InquiryController.InquirySave` sequence:

1. force `RoleId = 3` (Customer) and `StatusId = 2` (Active);
2. `phone := countryCode + phone`;
3. **`email := the supplied address, or the phone digits with the '+' stripped`**, and `username := email`. This
   is the "EmailNotRequired" rule, and it is what decides Contact vs Customer: a phone-only enquiry produces a
   party whose e-mail is not an e-mail, which fails the qualification test;
4. `EXEC InquirySave` - matches an existing party by e-mail / username / any phone field, rejects an enquiry whose
   e-mail and phone belong to two *different* customers, then inserts the `Inquiry` row;
5. **only when that created a new inquiry that matched nobody**: create the `AspNetUsers` login and `EXEC CustomerSave`;
6. set `Inquiry.UserProfileId`, then re-apply the qualification rule.

Steps 4-6 run in **one transaction** (`IDbExecutor.InTransactionAsync`). The legacy action ran them unwrapped and
ended in `catch (Exception ex) { return null; }`, so a rejection at step 5 left the inquiry and the login committed
with no party behind them. See defect 0 below - that is not a hypothetical path, it is what happens on UAT today.

`POST /api/inquiries` returns `{ inquiryId, userProfileId, partyKind, partyCreated }`, so the caller knows whether
the enquirer was new and what they were classified as. Rejections from the procedures surface as **422**
(`Duplicate Inquiry exists with same Phone.`, `Email and Phone found in different customers.`); a missing or
malformed phone is a **400** from the validator, before the database is touched.

Two departures from legacy, both deliberate: the hardcoded shared password (`"2342343&"`, in source control, the
same for every customer ever created) is replaced by a per-party random one, and errors are reported rather than
swallowed. The catalog `Account` for the new party is created by `AccountSyncJob` on its next pass - customers do
not sign in through this back-office API, so nothing waits on it.

## Endpoints

| Endpoint | Purpose | Perm |
|---|---|---|
| `POST /api/inquiries` | add an inquiry; matches or creates the party and its login, in one transaction | 539 |
| `PUT /api/inquiries/{id}` | update an inquiry (the procedure only changes name, status, role, gender, country, customer type, ad link) | 539 |
| `GET /api/inquiries/check-email` · `check-phone` | is this enquirer already known? (legacy `CheckEmail`/`CheckPhone` probes on the add form) | 539 |
| `GET /api/inquiries` | paged list; filters incl. `partyKind=Contact\|Customer`, `isTagged`, `isContacted`, `agentId`, `sourceId`, `countryId`, `dateFrom/To`, `followUpStatus` | 538 |
| `GET /api/inquiries/{id}` | one inquiry (fetched through the list, so row visibility still applies -> 404, never someone else's row) | 537 |
| `POST /api/inquiries/{id}/contact-status` | record a contact attempt; appends history to `CustomerRemarks`, refreshes the cached latest status/remark, then re-qualifies the party | 608 |
| `POST /api/inquiries/{id}/tag` | tag the inquiry's party to an agent (422 if the inquiry has no party) | 550 |
| `POST /api/inquiries/tag-bulk` · `untag-bulk` | bulk tag by inquiry / bulk untag by (customer, agent); partial success - see Tagging below | 550 |
| `GET /api/parties/{id}/kind` · `PUT .../kind` · `POST .../kind/refresh` | read / pin / re-apply the classification | 103, 523 |
| `GET /api/parties/{id}/inquiries` | every inquiry this party raised | 546 |
| `GET /api/parties/{id}/sections` | party detail tabs (`Contact_GetSectionById`) | 106 |
| `GET /api/parties/{id}/interactions` · `POST` | the interaction log | 103, 608 |

**Row-level visibility is unchanged** and still lives in `GetInquiryAll`: RoleId 2 (Sales Executive) sees only
inquiries tagged to them, RoleId 12 (CSD Manager) their reporting subtree, everyone else all rows.

## Tagging, and why bulk tag and bulk untag are not symmetric

Ported 2026-09-24 as `POST /api/inquiries/tag-bulk` / `/untag-bulk`. Recorded here because it is not derivable
from the procedure names, and **there is no bulk stored procedure** - the legacy actions loop in C#
(`InquiryController.TaggedFromInquiriesBulk` / `UnTagFromInquiriesBulk`), and so does v2:

| | Bulk **tag** | Bulk **untag** |
|---|---|---|
| Keyed on | the **inquiry** | the **(customer, agent)** pair - *not* the inquiry |
| Deduped by | `InquiryId` | `(CustomerId, AgentId)`, so selected rows sharing a customer collapse into one call |
| Calls, per item | `Inquiry_TaggedFromInquiries(agentId, inquiryId)` | `CustomerTagging_UnTagCustomerFromAgent(customerId, agentId)` |
| Success test (legacy) | the procedure returns `"Ok"` - which it always does, see below | same |
| Notification per success | `CUSTOMER_TAGGED` (only when `CustomerId > 0`) | `CUSTOMER_UNTAGGED` |

The asymmetry is deliberate: an agent is tagged to a **customer**, so untagging is a property of the party, while
tagging is driven from the inquiry row the user selected. Both actions are **partially successful by design** -
they count `tagged`/`failed`, return `"N of M ... successfully"`, and only fail the request (400) when *nothing*
succeeded. v2 keeps that contract (200 with `requested`/`succeeded`/`failed`/`message`) rather than making the
batch atomic, or the screen's behaviour changes.

What v2 does differently, and why:

- **"Nothing succeeded" is 422, not the legacy 400.** v2 uses 400 only for malformed input (no agent, no valid
  id - the validator), and 422 when a well-formed request is rejected (`ApiExceptionHandler`).
- **"Ok" is not the success test.** Both procedures end in an unconditional `SELECT 'Ok'`, so the legacy
  `failed` count could only ever come from an exception. v2 counts an item as failed when it throws. If *nothing*
  succeeded and a failure was not an expected rejection (a DB fault rather than a 404/422), that fault is rethrown
  so an outage is a 500, not "none could be tagged".
- **Bulk tag resolves the party on the server.** The client sends only `inquiryIds`; each item goes through the
  single-row tag, so row visibility (`GetInquiryAll`), the audit row and the notification apply per item.
- **An unlinked inquiry is never tagged** (defect 4 below). In practice `GetInquiryAll` does not return
  unlinked inquiries at all (verified locally for inquiry 1488), so they fail as 404 before the explicit guard is
  reached; the guard stays as a second line.
- **The single-row `POST /api/inquiries/{id}/tag` now sends `CUSTOMER_TAGGED`**, as the legacy action did; the
  first v2 port had omitted it.
- **Untag still notifies when nothing was untagged.** `CustomerTagging_UnTagCustomerFromAgent` reports "Ok" even
  when no active (customer, agent) row matched, and the legacy action notified regardless. v2 preserves this;
  a pre-check would need a per-pair query no procedure provides.

Recipients: `Notification_Create` adds active System Admins (role 10) on top of the agent because the type has
`IncludeSystemAdmins` set - `UseRoleMap = false` does not suppress that. Observed on the local run (agent + 4
admins per notification), identical to legacy. `CustomerTagging.AgentId` is a `UserProfileId` (which is what
notification recipients are keyed by), but 84 of the 265 local rows point at no `UserProfile` - legacy data,
left alone.

## Customer tagging (`/api/tagging`)

Ported 2026-09-24 from `CustomerTaggingController`. A **tag** is a `CustomerTagging` row linking a customer-role
party to the staff member (agent) who looks after them. At most one row per customer is active: tagging a
customer deactivates every other row for that customer, so it *moves* them.

| Endpoint | Procedure | Perm (legacy `_CSS_` gate) |
|---|---|---|
| `GET /api/tagging/agents` | `CustomerTagging_GetAllManagerAgent` | 526 (sidebar entry) |
| `GET /api/tagging/agents/{id}/customers` | `CustomerTagging_AgentTaggedCustomer` | 551 (Tagged History tab) |
| `GET /api/tagging/agents/{id}/history` | `CustomerTagging_AgentUnTaggedCustomerHistory` | 552 (UnTagged History tab) |
| `GET /api/tagging/me/customers` · `me/history` | the same two, for the caller | 907 (v2 "My Tagging", seeded for role 2) |
| `GET /api/tagging/available-customers` | `CustomerTagging_AvailableCustomers` | 550 |
| `POST /api/tagging/tag` · `untag` `{ customerId, agentId }` | `CustomerTagging_TagCustomerToAgent` / `_UnTagCustomerFromAgent` | 550 |

`_CSS_553` ("Cannot Tag Customer") only hides the Assigned Agent column of the legacy Customers list; no endpoint
here uses it. The `/me` routes exist because the legacy sidebar linked Sales Executives to their own
`AgentTaggingDetail?userId=<self>`; v2 navigation points at `/tagging/me`.

Visibility lives in the procedures and keys off `@CreatedBy` (the caller's **primary** role):

- **Agents list:** primary roles 1 and 7 see every agent (`Role.UserTypeId` 2 or 5); everyone else only agents whose
  `CountryId` is in `fn_GetUserAccessibleCountries(caller)`.
- **An agent's customers / history:** a Sales Executive (RoleId 2) gets rows only for themself - for anyone else
  the procedure returns an empty list, not an error. With the current grants that filter is never reached through
  the API: role 2 holds 526 and 907 but not 551/552 (checked locally 2026-09-24), so a Sales Executive uses
  `/me/...` and gets 403 on `/agents/{id}/...`, just as the legacy tabs were hidden from them.
- **Available customers:** every customer-role party (`UserTypeId` 3) with no active tag, with no country filter
  and no paging (851 rows locally). The legacy screen renders it in full; v2 returns it in full too.

Tagging rules (inside `CustomerTagging_TagCustomerToAgent`, checked against the **agent**, not the caller):

1. **Limit:** the agent's active distinct customers must be below `UserProfileDetail.CustomerTagLimit`. A **NULL
   limit means unlimited** (`count >= NULL` is never true). A limit of 0 blocks every tag.
2. **Divisions:** the customer's `CountryId` must be in `fn_GetUserAccessibleCountries(agent)`. An agent with no
   divisions can hold nobody.

The procedure returns these rejections as an ordinary result row (`'Limit Exceeded'`,
`'Customer does not exists in your assigned divisions'`), not as an error. v2 turns them into **422** with
reworded text (the original says "your" divisions, but it checks the agent's) and notifies only on `'Ok'`
(defect 5). Untag always reports `'Ok'`, even when no active tag matched, and the agent is notified anyway, as
in legacy.

**Tagging from an inquiry is a different path with different rules.** `Inquiry_TaggedFromInquiries` (used by
`POST /api/inquiries/{id}/tag` and `tag-bulk`) applies **neither** the limit nor the division check. That is
legacy behaviour, preserved. Whether both paths should enforce the rules is a product question.

Moving a customer notifies only the new agent; the previous agent gets no `CUSTOMER_UNTAGGED`, and the move is
invisible in their untag history (defect 6). Both are legacy behaviour, unchanged.

## Inbound leads are not handled here

`JaftimWebhooks` (the Azure Function) keeps receiving `POST /api/leads` and calling `InsertLead` directly on its own
connection string. Nothing in this backend touches that chain
(`InsertLead` -> `InquiryImport_FromLead` -> `InquirySave_FromLead` -> `CustomerSaveInternal`), and no v2 script
alters those procedures. New parties and inquiries therefore appear without the API's involvement; the reconciliation
job is what keeps `PartyKind` and `Inquiry.UserProfileId` correct for them.

## Legacy defects found here

Nothing below was silently absorbed: each is either documented and left alone, or repaired by a named script.

0. **`CustomerSave` rejects every new enquirer** - *repaired*, `database/v2/006_CustomerSave_InquiryGuard.sql`.
   `CustomerSave` carries a guard whose own comment reads "Exclude `@InquiryId` when converting that same inquiry
   into a customer" - but the predicate never excludes it:

   ```sql
   IF EXISTS (SELECT 1 FROM dbo.Inquiry I
              WHERE ISNULL(I.IsDeleted, 0) = 0
                AND (ISNULL(@UserProfileId, 0) = 0)          -- no  AND I.InquiryId <> @InquiryId
                AND (@Phone IN (I.Phone, I.SecondaryPhone, I.WhatsAppNumber) OR ...))
       RAISERROR('Duplicate inquiry exists with same Phone.', 16, 1)
   ```

   The add-inquiry flow is `InquirySave` (inserts the row) -> create the login -> `CustomerSave` (`@InquiryId` =
   that row), so the guard always finds the enquiry against itself and aborts. **On UAT today the `Inquiry` row and
   the `AspNetUsers` login are committed and no party is ever created**; the screen shows a generic failure because
   the action ends in `catch (Exception ex) { return null; }`. Verified on UAT by reading `OBJECT_DEFINITION`
   (guard active, procedure last modified 2026-09-08).

   The v2 script adds the missing `AND I.InquiryId <> ISNULL(@InquiryId, 0)`, restoring the intent the comment
   already states. The guard still fires for a phone that belongs to a *different* active inquiry, so the
   protection it was added for is kept. It patches whatever definition is deployed (rather than restating the
   180-line body) and refuses to run if it cannot find exactly one occurrence, so it is safe to re-run and cannot
   silently revert an unrelated change.

   Note that source control's copy - `Jaftim/Database/Database Scripts/CustomerSave.sql` - instead has the whole
   block **commented out**, dropping the protection entirely. That version was never deployed. The two definitions
   are otherwise byte-identical.

   The lead-ingestion chain is unaffected: it uses `CustomerSaveInternal`, which has no such guard. That is why
   inbound leads do get parties while the back-office add form does not.

1. **`Inquiry_GetSectionById` can never have worked** - it selects from `Base_InquiryType`, a table that does not
   exist (confirmed on UAT; exactly one procedure references it). The inquiry-sections endpoint is therefore not
   exposed; `GET /api/inquiries/{id}` returns the inquiry's fields in full instead.
2. **`Inquiry.LeadId` is never written.** `LeadId` is threaded through the whole ingestion chain but omitted from
   `InquirySave_FromLead`'s INSERT column list, so every lead-sourced inquiry has `LeadId = NULL` (0 of 1 455 rows
   are linked). v2 does not invent the link. Fixing it means editing an ingestion procedure the Azure Function
   depends on - a product decision.
3. **`Inquiry` had two indexes** (PK + `RI_ContactId`) while the list screen filters on `CreatedAt`, `SourceId`,
   `CountryId`, `LeadStatusId` and joins on `AspNetUserId`. v2 adds the missing ones (non-filtered on purpose: a
   filtered index would force `QUOTED_IDENTIFIER ON` on every writer, and 57 modules are still compiled with it
   OFF - see `docs/DATABASE.md`, which owns that count).
4. **`Inquiry_TaggedFromInquiries` tags a NULL customer for an unlinked inquiry.** It resolves the customer via
   `Inquiry.AspNetUserId -> UserProfile`; when that finds nothing, `@CustomerId` is NULL, and because
   `CustomerTagging.CustomerId` is nullable it inserts an active tag for no one, sets `Inquiry.AssignedTo`, and
   returns "Ok". 0 such rows exist locally today, and 111 of 1 192 local inquiries are unlinked. v2 refuses to tag
   an inquiry without a party (422) and never calls the procedure for it; the procedure is unchanged. Such a row
   would also **empty the Available Customers list** - see defect 7.
5. **`CustomerTagging_TagCustomerToAgent` rejections were reported as success.** The procedure returns
   `'Limit Exceeded'` / `'Customer does not exists in your assigned divisions'` as a result row; the legacy action
   checked only the HTTP status of its own service call, so it sent `CUSTOMER_TAGGED` for a tag that never
   happened. v2 checks the returned text: 422, no notification, no audit row.
6. **Moving a customer leaves no trace in the previous agent's history.** Tagging runs
   `UPDATE CustomerTagging SET IsActive = 0 WHERE CustomerId = @customerId` without setting `ModifiedBy` or
   `ModifiedAt`, and `CustomerTagging_AgentUnTaggedCustomerHistory` **inner**-joins `ModifiedBy` to find who
   untagged. So a reassigned customer disappears from the previous agent's list and never appears in their
   history. Verified locally on 2026-09-24 (customer 13600 moved from agent 133 to 12259). Preserved: fixing it
   means a `_V2` procedure (or a `LEFT JOIN` history variant) - a product decision.
7. **One NULL-customer tag empties Available Customers.** `CustomerTagging_AvailableCustomers` filters with
   `UserProfileId NOT IN (SELECT CustomerId FROM CustomerTagging WHERE IsActive = 1 ...)`. If any active row has
   `CustomerId IS NULL` (which defect 4 can create), `NOT IN` is never true and the list is **empty**. 0 such rows
   locally. **UAT not checked yet**: run
   `SELECT COUNT(*) FROM CustomerTagging WHERE IsActive = 1 AND IsDeleted = 0 AND CustomerId IS NULL` (read-only).

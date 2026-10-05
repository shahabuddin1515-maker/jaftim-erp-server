# Users: regions (divisions, groups, countries)

Durable knowledge about the staff-user module that is not derivable from the code. Roles and permissions are in
`docs/AUTH.md` and `docs/NAVIGATION.md`; this page covers region assignment.

## Regions

A staff user is given a set of **countries**, picked through a two-level region tree. The model is legacy and
unchanged in the database:

```
EntityType     1 = Division, 2 = Group                    (seed of "create table and seed entitytype.sql")
Entity         Division (ParentEntityId NULL)  ->  Group (ParentEntityId = the division)
Base_Country   .EntityId -> the Group the country belongs to
UserEntities   (UserId, EntityId)   divisions/groups ticked on the assignment screen
UserCountries  (UserId, CountryId)  the granted countries
```

Local copy, 2026-10-05: 6 divisions, 19 groups; 208 of 211 countries sit on a group (the other 3 are test rows);
12 users hold assignments, all staff.

### Only the countries decide access

`fn_GetUserAccessibleCountries(@UserId)` reads **only `UserCountries`**. Its branches that would expand a ticked
group or division into countries are commented out in the deployed definition. So a tick grants nothing by itself;
`UserEntities` is screen state. The function scopes five procedures: `CustomerTagging_TagCustomerToAgent` (the
"divisions" rule - `docs/INQUIRIES.md`), `CustomerTagging_GetAllManagerAgent`, `GetCustomerAll`, `GetCustomerAllNew`,
`CustomerProfileGetById`. It ignores inactive countries.

**Decision (owner, 2026-10-05): countries stay the only source of access**; the function is not changed. A country
added to a group later is therefore not granted to anyone holding that group until their regions are saved again.

### The legacy screen's semantics

`UserController.AssignEntities` (view `AssignEntities.cshtml`, gated by `_CSS_548` "User Hierarchy"):

- one tab per division; a group's countries are **enabled** only while the group is ticked, a group only while its
  division is ticked; on load, children of an unticked parent are unticked and disabled;
- countries are then ticked one by one (a *partial* group is legitimate); "Select All" ticks a whole division tab;
- the save posts every ticked division/group as `AssignedEntityCSV` and every ticked country as
  `AssignedCountryCSV` to `AssignEntitiesToUser`, which deletes and re-inserts both lists in one transaction.

Consequence for coexistence: a country stored without its group's row is dropped the next time someone saves the
user on the legacy screen.

### What v2 does (`/api/users/{id}/regions`)

| Endpoint | Perm | Behaviour |
|---|---|---|
| `GET` | 203 Employee Detail | the whole tree with, per node, `selection` (`All` / `Partial` / `None` of its **active** countries), `isTicked` (the stored row) and per country `isActive` / `isGranted`; plus `grantedCountryIds` and `unplacedCountries` |
| `PUT` `{ divisionIds, groupIds, countryIds }` | 548 User Hierarchy | replaces the regions: whole divisions/groups expand to their active countries **now**, `countryIds` add individual ones; the division/group rows written are **exactly the ancestors of the granted countries**; empty clears |

Rules enforced in the service (the database has none of them - see defects): unknown ids, a group not under a
division, a country in no (valid) group, and a newly picked inactive country are one 422 listing every problem. An
inactive country the user **already** holds is kept (11 such grants exist locally; rejecting them would block every
re-save, and they grant nothing). Customers cannot hold regions (422). The save is audited
(`UserProfile.RegionsChanged`, before/after id lists) and the procedure's status row is checked.

Deliberate departures from legacy: the legacy screen refused to save an empty selection ("Please assign at least
one entity or country"); v2 allows it, because removing a leaver's regions is a real need. The legacy screen could
store a ticked group with none of its countries, or a country whose group is unticked; v2 cannot produce either.

### Region administration (`/api/regions`, new in v2)

The legacy app had no screen for the tree; it was maintained by SQL script. v2 exposes it as master data under
permission **905 "Master Data"** (Settings; seeded to the super admin only - grant it to other roles through
`PUT /api/roles/{id}/permissions`). Every write returns the updated tree and is audited (`Region.*`).

| Endpoint | Rule |
|---|---|
| `GET /api/regions` | the valid tree, plus `orphanGroups` (no or a missing division) and `unplacedCountries` (no existing group) |
| `POST /divisions`, `PUT /divisions/{id}` | name 1-200, unique among divisions (trimmed, case-insensitive) -> 409 |
| `POST /groups`, `PUT /groups/{id}` `{ name, divisionId }` | name unique among groups; the division must exist; `PUT` also moves a group (with its countries) or repairs an orphan |
| `DELETE /divisions/{id}` · `/groups/{id}` | soft delete, only when empty (no groups / no countries) -> 422 otherwise; drops the region's `UserEntities` rows |
| `PUT /countries/{id}/group` `{ groupId }` | the group must be under a division; also places an unplaced country |

**No operation changes access** (`UserCountries`). But moving a country, or a group to another division, changes the
ancestors of countries people hold, so `UserRegion_RederiveTicks` rewrites the `UserEntities` of every user holding
an affected country to exactly the ancestors of what they hold - in the same transaction. Without that, the legacy
screen would drop those grants on its next save. A side effect: those users' screen-only ticks (a ticked group with
no granted country) are dropped, and their drift (defect 5) is repaired.

Integrity is checked twice: in the service, for precise messages, and again inside each `database/v2/009`
procedure under `UPDLOCK, HOLDLOCK` (`THROW 50000` -> 422), because the table itself enforces nothing. These are the
first procedures that write `Entity`; they are compiled with `QUOTED_IDENTIFIER ON` (the filtered index, defect 4).
`Entity.ModifiedBy` is a `datetime` column, so the actor is recorded in `AuditLog` only (`ModifiedAt` is set).

### Defects found here

Recorded 2026-10-05 from the local copy. Nothing below was changed in the database (owner decision: validate in
v2; the schema repair belongs to the hardening backlog in `docs/DATABASE.md`).

1. **`AssignEntitiesToUser` failures were reported as success.** Its CATCH rolls back and *returns*
   `SELECT 500 AS StatusCode, ERROR_MESSAGE()`; the legacy repository called it with `_db.Execute` and never read
   the row, so the screen said "saved". v2 reads the row and raises (500).
2. **The deployed `Entity` table has no primary key**, no `ParentEntityId -> Entity` foreign key and no
   `CK_Entity_DivisionParent` check, although `create table entity.sql` declares all three. Duplicate ids, a group
   without a division, or a cycle are all accepted by the database. v2 builds the tree defensively and ignores rows
   that break the shape.
3. **Column types:** `Entity.ModifiedBy` is `datetime` (should be `bigint`); `UserEntities` / `UserCountries` store
   `UserId` and `EntityId`/`CountryId` as `int` against `bigint` keys, with no foreign keys; `AssignEntitiesToUser`
   takes `@UserId INT`.
4. **Filtered index** `IX_Entity_ParentEntityId_Type` (`EntityTypeId = 2`): any procedure that writes `Entity` must
   be compiled with `QUOTED_IDENTIFIER ON` - the `v2/009` procedures are.
5. **Ticks and grants drift on the legacy screen.** Locally one user has a ticked group missing two of its
   countries (a partial pick - legitimate) and user 12461 holds Zimbabwe with Africa ticked but its group (Eastern
   Africa) not - the legacy screen will drop that grant on the next save there. `GET` shows such cases
   (`isTicked: false` on a group with `selection` other than `None`).
6. `GetEntityHierarchyForUserAssignment` (the legacy read) offers inactive countries for ticking and filters
   `IsDeleted = 0` without `ISNULL`; v2 reads through `UserRegion_Get` (`database/v2/008`) instead.

## Not ported yet

`UserController.OrgChart` (`GetOrgChart`) - still `todo` in `docs/MIGRATION_INVENTORY.md`.

# Database strategy (database-first, additive, one database per tenant)

## Topology (decided 2026-09-18)

| Database | Contents | Scripts |
|---|---|---|
| **catalog** (`jaftim-<env>-catalog`, one per environment) | `Tenant` (code + connection string / `kv:` reference), `Account` (shared credentials, Identity-V3 hash, `TokenVersion`, `PasswordChangedByApiAtUtc`), `AccountTenant` (which `UserProfileId` an account is inside each tenant), `AccountRefreshToken`, `AuthAuditLog`; `Catalog_*` procedures | `database/catalog/` |
| **tenant** (`jaftim-<env>-<tenant>`, one per company) | the complete legacy schema, untouched, plus the additive v2 objects: `UserRole` + `fn_GetUserEffectiveRoles` + `UserRole_*`, `AuditLog` + `AuditLog_*` | `database/v2/` (run against EVERY tenant) |
| **jobs** (`jaftim-<env>-jobs`) | Hangfire storage (self-created) | - |

No cross-database queries (Azure SQL): the catalog is seeded and kept in step by `AccountSyncJob`
(`database/catalog/002_Seed_From_Tenant.md`). Provisioning a tenant = bacpac import + v2 scripts + one `Tenant` row;
the jobs host picks it up within 5 minutes.

**Standard v2 audit columns** on every new table: `CreatedAtUtc datetime2(3)`, `CreatedBy bigint`, `ModifiedAtUtc`,
`ModifiedBy`, `IsDeleted bit`, `DeletedAtUtc`, `DeletedBy`, `RowVersion`. Legacy tables keep their columns.

The tenant database is the system of record for both the legacy MVC app and this API for the whole migration.
`jaftim-uat-db` on `jaftim-uat-db-server.database.windows.net` is UAT; Live is `jaftim-live-db` (20 DTU). Connection
strings live in user-secrets / App Service settings, never in this repo.

## Live inventory (UAT, 2026-09-17)

246 tables, 405 procedures, 12 views, 19 functions, 21 triggers, 88 foreign keys, 9 table types. Full object lists
are in `docs/inventory/` (`tables.txt`, `procedures.txt`).

**57 modules are compiled with `QUOTED_IDENTIFIER OFF`** (54 of them procedures) - measured 2026-09-24 on UAT and
on the local copy, which agree. The legacy `README.md` section 6.5 says 55 and earlier drafts of these docs said
58; the count drifts as objects are recreated, so re-measure rather than trusting any number here:
`SELECT COUNT(*) FROM sys.sql_modules WHERE uses_quoted_identifier = 0`. The count is not the point - the rule is:
**do not add a filtered index to a table those modules write**, or every one of them fails at runtime.

Tables that are dead or scratch (do not build on them): `bk`, `bk_`, `bk_SYS_AuthTable`, `Test*`, `TestList_TB`,
`TestLead`, `Testing`, `TestParamLog`, `TestPayment`, `Payment`, `RefundClaim`, `RefundClaimDetail`,
`EmployeePolicy*`, `old_Inquiry`, `Temp_BulkData`, `test_StockData`, `Debug_GetPublicMethodData`.

Procedures that are stale siblings (confirm the live caller before use): `StockGetAll` vs `StockGetAll_New`
(the app calls `_New`; `StockGetAll` is used only by `StockViewGetId`), `StockGetAll_Backup`, `GetInquiryAll_bk`,
`UpdateAuthTable_Backup`, `SalesModule_GetAllStock` vs `_Optimized` (app calls `_Optimized`),
`StockMarkAvailbleForSale` (typo twin of `StockMarkAvailableForSale`), `UpdatePaid _Auto` (with a space) vs
`UpdatePaid_Auto`, `TEST1`.

## Rules

1. **Additive only.** New procedure / new nullable column / new table. Existing procedures the legacy app calls are
   never changed in behaviour by this repo. If the API needs a different shape, create `Name_V2`.

   *One deliberate exception so far:* `v2/006_CustomerSave_InquiryGuard.sql` narrows a guard inside the legacy
   `CustomerSave`. It is not a behaviour change the legacy app can notice in any working flow - the guard it
   repairs aborts **every** new enquirer, including the legacy screen's own (`docs/INQUIRIES.md` defect 0), so the
   only behaviour it alters is a total failure. A `_V2` copy was rejected because the legacy screen would keep the
   broken one. Deviations of this kind get their own numbered script, an explanation in the file header, and a row
   here - they are never folded into an unrelated script.
2. **Every v2 script** goes in `database/v2/NNN_Name.sql`: numbered, idempotent (`IF OBJECT_ID ... IS NULL`,
   `CREATE OR ALTER`), `SET QUOTED_IDENTIFIER ON` in its own batch at the top. Apply every tenant script to
   **every** local tenant database. Next free number: **007**. (`002` is used twice - `002_AuditLog.sql` and
   `002_Hangfire_Database.sql`, which targets the separate jobs database; don't repeat that, take the next number.)
3. **Audit trio contract.** Procedures the API calls with default injection must declare
   `@CreatedBy BIGINT, @CreatedAt DATETIME, @CompanyId BIGINT = NULL`. `@CreatedBy` is `UserProfile.UserProfileId`.
4. **UTC.** `@CreatedAt` is `DateTime.UtcNow`. Do not change.
5. **Soft delete.** Filter with `ISNULL(IsDeleted, 0) = 0`. `IsDeleted` is bigint/bit/tinyint by table and carries
   negative reason codes in `Stock_StatusDetail` (-1/-9/-923/-99923/-999).
6. **Environments.** v2 schema work happens on **local copies of UAT** (`jaftim-local-db`, `jaftim-local-db2` as a
   second tenant, `jaftim-local-catalog`; see GETTING_STARTED section 1) - never apply v2 scripts to UAT during development (owner rule, 2026-09-18). UAT is read-only
   reference for schema/procedure definitions and bacpac exports. Promotion to UAT and then Live happens as an
   explicit release step with the owner's authorisation, verified with `sqlcmd` (`sys.procedures`,
   `OBJECT_DEFINITION`).

## v2 objects

| Script | Objects | Status |
|---|---|---|
| `catalog/001_Catalog_Schema.sql` | catalog tables + 16 `Catalog_*` procedures | applied to `jaftim-local-catalog` (2026-09-18), verified end-to-end |
| `v2/001_UserRole.sql` | `UserRole` (backfilled from `UserProfile.RoleId`), `fn_GetUserEffectiveRoles`, `UserRole_GetByUser/GetEffective/Save/Revoke`, `UserProfile_SetPrimaryRole` | applied to both local tenants |
| `v2/002_AuditLog.sql` | `AuditLog`, `AuditLog_Write`, `AuditLog_GetByEntity` | applied to both local tenants |
| `v2/004_PermissionHierarchy.sql` | v2 permissions 900-907 (modules/screens that had no permission), seeded grants, `RoleAction_Save`, `RoleActionMapping_Replace` | applied to both local tenants |
| `v2/003_Navigation.sql` | `NavigationItem` (seeded from the legacy sidebar, regrouped by module), `Navigation_GetAll/Save/Delete` | applied to both local tenants |
| `v2/005_PartyKind_And_Inquiry.sql` | `UserProfile.PartyKind/PartyKindIsManual/PartyQualifiedAtUtc/PartyKindModifiedAtUtc`, `fn_PartyKind`, `Party_RecomputeKind/SetKindManual/ReconcileKinds`, `Inquiry.UserProfileId` + FK + `Inquiry_ReconcileUserProfileId`, 4 missing indexes | applied to both local tenants; 0 mismatches vs the legacy CASE |
| `v2/006_CustomerSave_InquiryGuard.sql` | patches `CustomerSave`'s duplicate-phone guard to exclude the inquiry being converted (its own comment already promised this). **Required for `POST /api/inquiries`** - without it every new enquirer is rejected. Self-adapting and re-runnable; see `docs/INQUIRIES.md` defect 0 | applied to both local tenants |
| `v2/002_Hangfire_Database.sql` | notes for creating the jobs database | local `jaftim-local-jobs` created |

Pre-existing tables the API reuses with no change: `AspNetUsers` (kept because three ingestion procedures insert into it; mirrored to the catalog),
`LoginAttempts`, `ActionURlTbl`, `Base_WhitelistedIPs`, `RoleAction`, `RoleActionMapping`, `SYS_DropDownsWithAuth`,
all `Notification*` tables.

## Hardening backlog (separate workstream, after the API is live)

Do these with a data-quality audit per table, and only once the legacy app no longer writes to the table:

- Missing FKs on `Stock_*` (`Stock_Id`), `CustomerTagging` (`CustomerId`, `AgentId`), `Inquiry` (`AspNetUserId`),
  `Customer_Wallet*`, `Stock_Financial` (`Stock_R_D_CustomerId`) - expect orphans; decide per table.
- `IsDeleted` -> `BIT` + `DeletionReason` where sentinels carry meaning.
- Drop dead tables listed above and the stale procedure siblings.
- Recompile the `QUOTED_IDENTIFIER OFF` modules with it ON (57 as of 2026-09-24; needed before any filtered index
  on the tables they write).
- `Leads.LeadId` -> `Inquiry.LeadId` link (product decision first - README section 6.4).

## Useful queries

```sql
-- parameters of a procedure (does it declare the audit trio? has defaults?)
SELECT p.name, t.name, p.has_default_value FROM sys.parameters p JOIN sys.types t ON t.user_type_id = p.user_type_id
WHERE p.object_id = OBJECT_ID('dbo.StockGetAll_New') ORDER BY p.parameter_id;

-- definition
SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.StockGetAll_New'));

-- modules compiled with QUOTED_IDENTIFIER OFF
SELECT OBJECT_NAME(object_id) FROM sys.sql_modules WHERE uses_quoted_identifier = 0 ORDER BY 1;

-- which roles have no rights at all (they cannot use the API)
SELECT r.RoleId, r.RoleName FROM Role r WHERE ISNULL(r.IsDeleted,0)=0
AND NOT EXISTS (SELECT 1 FROM RoleActionMapping m WHERE m.RoleId = r.RoleId AND ISNULL(m.IsDeleted,0)=0);
```

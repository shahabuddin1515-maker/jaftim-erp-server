# Getting started

## Prerequisites
- .NET SDK 10.0.x (`dotnet --version`)
- A **local SQL Server** (2022 Developer / Express / LocalDB). Development runs against a local copy of the UAT
  database; **v2 schema changes are applied to local databases only, never to UAT** (owner rule, 2026-09-18).
- `sqlcmd` and `sqlpackage` (`dotnet tool install -g microsoft.sqlpackage --version 162.5.57` - the 170.x builds
  need a newer .NET 10 runtime patch than most machines have)

## 1. Local databases = catalog + one tenant copy of UAT (+ optional second tenant)
Azure SQL cannot BACKUP to a file, so use a bacpac. Export is read-only against UAT.
```
sqlpackage /Action:Export /TargetFile:jaftim-uat.bacpac /SourceServerName:jaftim-uat-db-server.database.windows.net /SourceDatabaseName:jaftim-uat-db /SourceUser:<user> /SourcePassword:<password> /SourceEncryptConnection:True /p:VerifyExtraction=False /p:CommandTimeout=600
sqlpackage /Action:Import /SourceFile:jaftim-uat.bacpac /TargetServerName:localhost /TargetDatabaseName:jaftim-local-db  /TargetTrustServerCertificate:True
sqlpackage /Action:Import /SourceFile:jaftim-uat.bacpac /TargetServerName:localhost /TargetDatabaseName:jaftim-local-db2 /TargetTrustServerCertificate:True   # optional second tenant
sqlcmd -S localhost -E -Q "CREATE DATABASE [jaftim-local-catalog]; CREATE DATABASE [jaftim-local-jobs]"
sqlcmd -S localhost -E -d jaftim-local-catalog -i database/catalog/001_Catalog_Schema.sql
sqlcmd -S localhost -E -d jaftim-local-db  -i database/v2/001_UserRole.sql
sqlcmd -S localhost -E -d jaftim-local-db  -i database/v2/002_AuditLog.sql
sqlcmd -S localhost -E -d jaftim-local-db  -i database/v2/003_Navigation.sql
sqlcmd -S localhost -E -d jaftim-local-db  -i database/v2/004_PermissionHierarchy.sql
sqlcmd -S localhost -E -d jaftim-local-db  -i database/v2/005_PartyKind_And_Inquiry.sql
sqlcmd -S localhost -E -d jaftim-local-db  -i database/v2/006_CustomerSave_InquiryGuard.sql
sqlcmd -S localhost -E -d jaftim-local-db2 -i database/v2/001_UserRole.sql      # if you created the second tenant
sqlcmd -S localhost -E -d jaftim-local-db2 -i database/v2/002_AuditLog.sql
sqlcmd -S localhost -E -d jaftim-local-db2 -i database/v2/003_Navigation.sql
sqlcmd -S localhost -E -d jaftim-local-db2 -i database/v2/004_PermissionHierarchy.sql
sqlcmd -S localhost -E -d jaftim-local-db2 -i database/v2/005_PartyKind_And_Inquiry.sql
sqlcmd -S localhost -E -d jaftim-local-db2 -i database/v2/006_CustomerSave_InquiryGuard.sql
```
Pass `-I` (QUOTED_IDENTIFIER ON) to any ad-hoc `sqlcmd` that writes to `UserProfile` or `Inquiry`; those tables
carry computed/indexed objects that reject the sqlcmd default. The scripts above set it themselves.
Register the tenants in the catalog (connection strings are plain locally; `kv:<secret>` references in UAT/Live):
```sql
INSERT INTO dbo.Tenant (Code, Name, ConnectionString) VALUES
  ('jaftim', 'Jaftim', 'Server=localhost;Database=jaftim-local-db;Integrated Security=true;TrustServerCertificate=true;'),
  ('acme',   'Acme Local', 'Server=localhost;Database=jaftim-local-db2;Integrated Security=true;TrustServerCertificate=true;');
```
(~5 min export, ~1 min per import, 5 MB.) All scripts are idempotent - re-run them after a fresh import.

Accounts are **not** inserted by script: start `Jaftim.Jobs` (step 3) and `AccountSyncJob` seeds `Account` /
`AccountTenant` from every tenant within a minute (`SELECT COUNT(*) FROM Account` = 1360 for the UAT copy). Then
give yourself a known password (writes catalog + tenant; refuses non-local servers):
```
./tools/set-local-password.ps1 -Email superadmin@jaftim.com -Password 'LocalTest1234'
```

## 2. Secrets (never in appsettings.json)
```
cd src/Jaftim.Api
dotnet user-secrets set "ConnectionStrings:Catalog"  "Server=localhost;Database=jaftim-local-catalog;Integrated Security=true;TrustServerCertificate=true;"
dotnet user-secrets set "ConnectionStrings:Hangfire" "Server=localhost;Database=jaftim-local-jobs;Integrated Security=true;TrustServerCertificate=true;"
dotnet user-secrets set "Auth:SigningKey" "<at least 32 random bytes, e.g. openssl rand -base64 48>"
dotnet user-secrets set "BlobStorage:ConnectionString" "<azure storage connection string, optional locally>"

cd ../Jaftim.Jobs
dotnet user-secrets set "ConnectionStrings:Catalog"  "<same as API>"
dotnet user-secrets set "ConnectionStrings:Hangfire" "<same as API>"
dotnet user-secrets set "Hangfire:Dashboard:Password" "<any local password>"
```
Both hosts must share `ConnectionStrings:Hangfire`; the API only enqueues, Jobs executes.
UAT/Live get the same keys through App Service settings / Key Vault when the v2 scripts are promoted there.

`SystemUser:UserProfileId` (both hosts) must be an active staff `UserProfile` - it is `@CreatedBy` for everything
background jobs write. Default 1 = the first Super Admin on UAT.

## 3. Run
```
dotnet build
dotnet test
dotnet run --project src/Jaftim.Api      # http://localhost:5120/swagger (Swagger UI), /scalar, /swagger/v1/swagger.json, /health
dotnet run --project src/Jaftim.Jobs     # http://localhost:<port>/hangfire  (basic auth)
```

## 4. Smoke test (all verified against the local copy, 2026-09-21)
```
POST /api/auth/login          { "email": "superadmin@jaftim.com", "password": "LocalTest1234" }
                             -> 409 TenantSelectionRequired when the account is in both local tenants and none is default;
                                add "tenantCode": "jaftim" (or "acme")
GET  /api/auth/me             Authorization: Bearer <accessToken>   -> tenant, roles, permissions, memberships
GET  /api/auth/memberships
POST /api/auth/switch-tenant  { "tenantCode": "acme" }   -> new token pair; /api/stocks now reads jaftim-local-db2
GET  /api/stocks?page=1&pageSize=10
GET  /api/stocks/230/sections
POST /api/users/134/roles     { "roleId": 13, "validToUtc": "<now+2min>", "reason": "cover" }  -> user 134 gains Finance Manager perms within 60 s, loses them after expiry
GET  /api/audit?entityType=UserProfile&entityId=134
POST /api/auth/refresh        { "refreshToken": "<refreshToken>" }
POST /api/auth/logout         { "allDevices": true }   -> the old access token is now rejected (tv mismatch)
```

Inquiry add (verified 2026-09-23; needs `database/v2/006` applied or every create returns 422). Use a phone that
is not already in `Inquiry` - a timestamp suffix works:
```
POST /api/inquiries  { "fullName":"Phone Only","phoneCountryCode":"+971","phone":"500<HHMMSS>","sourceId":1,"countryId":174 }
     -> 201 { inquiryId, userProfileId, partyKind: "Contact", partyCreated: true }     (no e-mail => Contact)
POST /api/inquiries  same phone again
     -> 201 { same userProfileId, partyCreated: false }                                (party reused)
POST /api/inquiries  ... "email":"cust<HHMMSS>@example.com", different phone
     -> 201 { partyKind: "Customer", partyCreated: true }                              (real e-mail => Customer)
POST /api/inquiries  e-mail of one party + phone of another
     -> 422 "Email and Phone found in different customers."  and ZERO new rows (the whole create is one transaction)
POST /api/inquiries  without "phone"
     -> 400 { errors: { Phone: [...] } }                                               (validator, before any SQL)
```
Clean test rows up afterwards - and note `sqlcmd` needs `-I` to delete from `UserProfile`/`Inquiry`.

## 5. Regenerating the permission catalog
When `RoleAction` gains rows (`PUT /api/permissions`, or a navigation item with `newPermissionName`), regenerate
`src/Jaftim.Domain/Security/Permissions.cs`: `./tools/gen-permissions.ps1 -Server localhost -Database jaftim-local-db`
(integrated auth; add `-User/-Password` for Azure). Commit the result; never hand-edit values.

## 6. Repo layout
See `docs/ARCHITECTURE.md`. Start with `docs/REWRITE_PLAN.md`, then `docs/CONVENTIONS.md` before adding code.

## Troubleshooting
- `Auth:SigningKey must be configured` -> step 2.
- 401 on every call with a fresh token -> `tv` claim must equal catalog `Account.TokenVersion`, `Account.IsActive = 1`, and the tenant `UserProfile.StatusId = 2`.
- Login 401 for an account you just set a password on -> check `LEN(PasswordHash)` is 84 in BOTH `Account` (catalog) and `AspNetUsers` (tenant); `dotnet run --project tools/HashPassword -- <password> <hash>` prints Success/Failed.
- Account exists in the tenant but not in the catalog -> Jobs host not running (AccountSyncJob seeds every minute).
- 403 on an endpoint the user "should" have -> the role has no `RoleActionMapping` row for that ActionId. 12 roles
  have none at all (docs/DATABASE.md query).
- Hangfire dashboard 401 -> `Hangfire:Dashboard:Password` not set.

## 7. API documentation (Swagger)
- `http://localhost:5120/swagger` - Swagger UI. Click **Authorize**, paste the `accessToken` from `POST /api/auth/login`
  (no `Bearer ` prefix); it is kept across reloads. `/scalar` shows the same document in Scalar.
- `http://localhost:5120/swagger/v1/swagger.json` - the OpenAPI 3 document to hand to the frontend team / code
  generators.
- Every operation shows its **required permission** (`RoleAction.ActionId`) and the shared error responses
  (400 validation, 401/403, 422 business rule, 500) as ProblemDetails. Text comes from the XML `<summary>` /
  `<param>` comments on controllers, DTOs and entities - document new endpoints there, not in a separate file.
- Off outside Development unless `OpenApi:Enabled = true` (never enable it on Live).

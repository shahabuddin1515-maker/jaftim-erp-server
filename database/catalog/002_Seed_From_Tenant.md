# Seeding the catalog from a tenant database

Performed by `Jaftim.Jobs` -> `AccountSyncJob` (recurring, per tenant, every minute) rather than by T-SQL, because
Azure SQL databases cannot query each other. Mapping, per tenant `AspNetUsers` row joined to `UserProfile` on
`UserProfile.AspNetUserId`:

| Tenant (source) | Catalog (target) |
|---|---|
| `AspNetUsers.Id` | `Account.AccountId` (first tenant that seeds the e-mail wins; later tenants link to the existing account) |
| `AspNetUsers.Email` | `Account.Email` / `NormalizedEmail` |
| `AspNetUsers.PasswordHash` | `Account.PasswordHash` (copied verbatim - Identity V3 format, verified by `IdentityCompatiblePasswordHasher`) |
| `LockoutEnabled`, `LockoutEnd`, `AccessFailedCount` | same |
| `UserProfile.UserProfileId` | `AccountTenant.UserProfileId` for that `TenantId` |
| `UserProfile.StatusId = 2 AND ISNULL(IsDeleted,0) = 0` | `AccountTenant.IsActive` |

Rules:
- Tenant -> catalog is the direction **while the legacy MVC app is still writing tenant credentials** (its
  Identity password reset, and the three ingestion procedures that insert customers). A changed tenant hash
  overwrites the catalog hash.
- The API writes the catalog first and **mirrors** password changes to the tenant `AspNetUsers` row of every
  membership, so the legacy app and the procedures keep working.
- After the legacy app is decommissioned the job keeps running only to pick up customers created by the ingestion
  procedures; password changes then originate from the API alone.
- The job is idempotent and cheap (a few thousand rows per tenant); it upserts through `Catalog_Account_UpsertFromTenant`.

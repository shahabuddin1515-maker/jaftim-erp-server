# Catalog database scripts

The catalog is the one shared database in a database-per-tenant deployment. It owns identity (`Account`), the
tenant registry (`Tenant`, including each tenant database connection string or Key Vault reference), memberships
(`AccountTenant` - which `UserProfileId` an account is inside each tenant), refresh-token sessions and the auth
audit trail. **No business data lives here.**

| Script | Purpose |
|---|---|
| `001_Catalog_Schema.sql` | tables + `Catalog_*` procedures; idempotent |
| `002_Seed_From_Tenant.md` | how accounts are seeded from each tenant `AspNetUsers` (done by the `AccountSyncJob`, not by T-SQL, because Azure SQL has no cross-database queries) |

Local: `jaftim-local-catalog`. Register the local tenant after applying `001`:

```sql
INSERT INTO dbo.Tenant (Code, Name, ConnectionString)
VALUES ('jaftim', 'Jaftim', 'Server=localhost;Database=jaftim-local-db;Integrated Security=true;TrustServerCertificate=true;');
```

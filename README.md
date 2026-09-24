# Jaftim Backend (v2)

.NET 10 API + Hangfire job host for the Jaftim ERP - the backend half of the rewrite. The frontend is a separate
project built by another team against this API.

- **Reference system**: the legacy ASP.NET Core MVC solution at `C:\jaftimv2\Jaftim` (read its `README.md`,
  `NOTIFICATIONS.md`, `StockStatusBusinessLogicSummary.md`, `AGENTS.md` first - they describe the business logic
  this API must preserve).
- **Where the work stands right now**: [docs/CURRENT_STATE.md](docs/CURRENT_STATE.md) - the session handoff:
  current objective, what is verified, blockers, next actions. **Read this first.** It is a handoff note, not an
  authority - the code, the tests and the other docs win if it disagrees.
- **Plan**: [docs/REWRITE_PLAN.md](docs/REWRITE_PLAN.md) - why the schema and the stored procedures stay, what gets
  rewritten, module order, risks, how to start.
- **Architecture**: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) - onion layers, request flow, cross-cutting.
- **How to add things**: [docs/CONVENTIONS.md](docs/CONVENTIONS.md).
- **Auth & tenancy**: [docs/AUTH.md](docs/AUTH.md) - JWT, shared catalog (accounts, tenants, sessions), one database per tenant, multiple/time-bound roles, audit.
- **Modules, navigation & permissions**: [docs/NAVIGATION.md](docs/NAVIGATION.md) - module hierarchy (module -> screen -> action), navbar as backend data, role/permission admin API; contract for the frontend team.
- **Inquiries, parties, Customer vs Contact**: [docs/INQUIRIES.md](docs/INQUIRIES.md) - what "contact" means, the stored PartyKind, snapshot-vs-current-truth, and the untouched lead-ingestion boundary.
- **Notifications**: [docs/NOTIFICATIONS.md](docs/NOTIFICATIONS.md) - raising events, the SignalR hub, the inbox endpoints and the routing admin (who receives what, as data).
- **Database**: [docs/DATABASE.md](docs/DATABASE.md) - database-first, additive-only rules and the live inventory.
- **Work list**: [docs/MIGRATION_INVENTORY.md](docs/MIGRATION_INVENTORY.md) - every legacy action -> v2 endpoint.
- **Run it**: [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md).

```
src/Jaftim.Domain          entities, enums, Permissions catalog          (no dependencies)
src/Jaftim.Application     services, validators, repository interfaces  (-> Domain)
src/Jaftim.Infrastructure  Dapper executor, repositories, JWT, blob, email, Hangfire client (-> Application)
src/Jaftim.Api             ASP.NET Core Web API host
src/Jaftim.Jobs            Hangfire server + dashboard
tests/                     xUnit
database/catalog/          shared catalog database (tenants, accounts, sessions, auth audit)
database/v2/               additive SQL for EVERY tenant database - apply all of it to every tenant
                           (see the script table in docs/DATABASE.md)
docs/                      the documents above + docs/inventory (live DB object lists)
tools/                     gen-permissions.ps1, set-local-password.ps1, HashPassword
```

Build & test: `dotnet build && dotnet test` (both hosts build warning-free; 69 tests).

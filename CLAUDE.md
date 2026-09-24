# Start here

This is the v2 backend (API + Hangfire) of the Jaftim ERP. Before changing anything:

1. Read `docs/CURRENT_STATE.md` (where the last session stopped and what to do next), then
   `docs/REWRITE_PLAN.md` (decisions, module order, what must not change), then `docs/CONVENTIONS.md`
   (the exact recipe for adding an endpoint, a procedure, a job).
2. The business logic lives in the stored procedures of the shared database and is documented in the **legacy
   repo** `C:\jaftimv2\Jaftim`: `README.md` (per-module logic, gotchas, live-verified ids), `NOTIFICATIONS.md`,
   `StockStatusBusinessLogicSummary.md`. Read the relevant section before porting a module. Prefer a live UAT query
   over a checked-in `.sql` file when precision matters (they drift).
3. Rules that are not negotiable: onion dependency direction; every SQL call through `IDbExecutor`/`SpCall`;
   every endpoint `[HasPermission]`; additive-only database changes in `database/v2/`; no Identity/EF packages;
   `IDateTimeProvider.UtcNow` only; **UAT and Live are read-only** - only `SELECT`/`OBJECT_DEFINITION` reads and bacpac
   exports may touch them, every script and every host runs against the local databases (`docs/GETTING_STARTED.md`);
   promotion to UAT/Live is a separate, owner-authorised release step; never commit secrets.
4. Keep `docs/MIGRATION_INVENTORY.md` current when you port an action, and update the docs when you learn something
   that is not derivable from the code.

---

## Long-Running Project Continuity

This project spans many Claude Code context windows. **The conversation is not the source of truth - the
repository is.** Write down anything a future session would otherwise have to rediscover, in the document that
owns it.

### Which document owns what

Put durable knowledge in exactly one place. Do not copy it between files; cross-reference instead.

| Document | Authoritative for |
|---|---|
| `CLAUDE.md` (this file) | Non-negotiable rules, the continuity protocol, where things belong |
| `README.md` | Orientation and the index of everything else |
| `docs/REWRITE_PLAN.md` | Long-term strategy, decisions and rationale, module order, risks, known legacy bugs and what v2 does about each |
| `docs/ARCHITECTURE.md` | Current architecture, layer rules, dependency direction, request flow, cross-cutting concerns |
| `docs/CONVENTIONS.md` | How to add an endpoint / procedure / job; naming; code style |
| `docs/AUTH.md` | JWT, catalog vs tenant identity, tenancy, roles/permissions, sessions |
| `docs/DATABASE.md` | **Database rules (the authority), topology, the v2 script table, live inventory, hardening backlog** |
| `docs/GETTING_STARTED.md` | Local bootstrap, running, smoke tests, troubleshooting |
| `docs/MIGRATION_INVENTORY.md` | Per-action porting status - the progress record |
| `docs/INQUIRIES.md`, `docs/NAVIGATION.md`, `docs/NOTIFICATIONS.md` | Durable business/module knowledge for those modules |
| `docs/CURRENT_STATE.md` | **Only** the immediate handoff: current objective, WIP, verification state, blockers, next actions |

Precedence when documents disagree: **code, tests and the live database first**, then `docs/DATABASE.md` for
database rules, then the other authoritative docs, and `docs/CURRENT_STATE.md` last. `CURRENT_STATE.md` is a
handoff note, never an authority over code.

### Fresh session startup

1. Read this file, then `docs/CURRENT_STATE.md`.
2. Read only the authoritative docs relevant to the task (table above). Do not read everything.
3. Reconcile the handoff against reality before trusting it - see below.
4. Read the actual implementation you are about to change.
5. If the task is a port, check `docs/MIGRATION_INVENTORY.md`.
6. Continue from the first valid entry in **Next Actions**. If it is already done or no longer makes sense, say so
   and correct `CURRENT_STATE.md` rather than silently doing something else.

### How to reconcile

This repository **is** under version control (since 2026-09-24), remote
`https://github.com/shahabuddin1515-maker/jaftim-erp-server` - branches `master` (default), `staging`, `dev`.
**The remote is public**; never commit anything you would not publish.

Reconcile in this order:

1. `git status` and `git log --oneline -15` - what changed, and whether the tree is clean.
2. `git diff` / `git diff --stat` for uncommitted work the handoff may not mention.
3. `dotnet build && dotnet test` - the truthful check that the tree is where the handoff claims.
4. `ls database/v2/` against the script table in `docs/DATABASE.md`.
5. For database claims, query the local databases (`sqlcmd -S localhost -E -d jaftim-local-db`). Use `-I`
   (QUOTED_IDENTIFIER ON) for anything that writes to `UserProfile`/`Inquiry`.

Git covers the source tree only. **The local databases are not version-controlled** - a schema change is not
recoverable from git, so check the v2 script table before assuming a database is in the state the handoff claims.

### Branching and pushing (owner's standing instruction, 2026-09-24)

- **All work happens on `dev`.** Check out `dev` at the start of a session and stay on it. Do not create feature
  branches unless the owner asks.
- **Commit and push to `dev` as you go** - you do not need to ask first. Push at every meaningful checkpoint and
  always before ending a session, so nothing of value lives only on this machine.
- **Never touch `staging` or `master`.** The owner merges `dev` onwards themselves. Do not merge, rebase,
  cherry-pick or push to either, and never force-push any branch.
- The remote is **public**: no real credential, key or connection string is ever committed.

### Update documentation during development, not at the end

Update the owning document **in the same task** that creates the knowledge:

| When | Update |
|---|---|
| An architectural decision changes | `REWRITE_PLAN.md` (decision + rationale), `ARCHITECTURE.md` (resulting shape) |
| A new implementation pattern is established | `CONVENTIONS.md` |
| Auth/permission/tenancy behaviour changes | `AUTH.md` |
| A database or deployment rule changes, or a v2 script is added | `DATABASE.md` (rules + script table) |
| Non-obvious module business behaviour is discovered | that module's doc |
| A legacy action is ported, or its status changes | `MIGRATION_INVENTORY.md` |
| A legacy bug or defect is found | `REWRITE_PLAN.md` section 7, plus the module doc for the detail |
| The objective, WIP, blockers, verification state or next actions change | `CURRENT_STATE.md` |

Do **not** document what is obvious from the code. Document what is non-obvious, architectural, operational,
business-critical, or needed to continue. Never write speculative facts - if you could not establish something,
record the uncertainty and how to resolve it.

### Definition of done

Code written is not done. An action is `done` in `MIGRATION_INVENTORY.md` only when it is implemented, has tests
for its branching logic, builds warning-free, `dotnet test` passes, and its behaviour has been checked against the
legacy behaviour it replaces. Anything less is `partial`, with a note saying what is missing.

### Continuity checkpoint

Perform one **before context runs low, before `/clear`, before ending a session, and at any meaningful
checkpoint**. Do not try to preserve the conversation - preserve the repository.

1. Stop starting new work; finish the smallest safe unit if practical.
2. Review what changed this session.
3. Move durable discoveries into the owning documents.
4. Update `MIGRATION_INVENTORY.md` if any porting status changed.
5. Update `CURRENT_STATE.md`: objective, WIP, verification state, blockers, relevant files, next actions.
6. Record truthfully what was and was not tested. **Never claim verification you did not perform**; if work is
   half-finished, say exactly that.
7. Reconcile as above (`git status`, `git log`, build/test).
8. Leave concrete, ordered next actions whose first item is immediately actionable.
9. Commit and push `dev` (see Branching and pushing above) so the checkpoint survives the session.

Keep `CURRENT_STATE.md` short enough to read in a minute. When something in it becomes durable, move it into the
document that owns it and delete it from the handoff.

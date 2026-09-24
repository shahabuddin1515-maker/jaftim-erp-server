# Module hierarchy, navigation & permissions (contract for the frontend)

## The hierarchy

```
Level 0  MODULE   RoleAction root (ActionParentId = 0)        Stock (400), Customers (100), Operations (900) ...
Level 1  SCREEN   child RoleAction = a navbar leaf            Stock Listing (417), Pricing (577), Vendors (605) ...
Level 2  ACTION   child RoleAction = a button / tab / column  Stock Export (558), Stock Tab Images (527) ...

NavigationItem  ──gated by──▶  RoleAction (one ActionId per node, modules included)
Role            ──grants────▶  RoleActionMapping ──▶ RoleAction   (what an admin configures)
User            ──has───────▶  primary role + UserRole rows (time-bound) ──▶ union = effective permissions
```

Three invariants the backend enforces:
1. **Every navigation node has exactly one permission.** A node without one is never shown (`NavigationService.GatePasses`).
2. **Visibility = the caller holds the node's permission**, through any effective role. A group additionally needs at
   least one visible child. No role-id branches (the two legacy ones - agent "My Tagging", admin "Settings" - became
   permissions 907 / 903-906 seeded to the same roles).
3. **Granting a permission grants its ancestors** (`RoleService.WithAncestors`): giving a role "Stock Export" gives it
   "Stock Detail" and "Stock" too, so the navbar can always reach what the role may do.

Everything is data in the tenant database; nothing about modules or menus is hard-coded in the API or the frontend.

## Tables
| Table | Rows | Notes |
|---|---|---|
| `RoleAction` (legacy) | the permission catalog | v2 rows use ids **900+** (`RoleAction_Save` allocates); legacy ids untouched |
| `RoleActionMapping` (legacy) | role -> permission | `RoleActionMapping_Replace` soft-deletes removed rows, inserts new ones |
| `NavigationItem` (v2) | the navbar tree | `Code` (frontend switch key), `Route`, `Icon`, `SortOrder`, `ActionId`, optional `RequiredRoleIdsCsv` |
| `UserRole` (v2) | extra / time-bound roles | see AUTH.md |

## Endpoints
| Method | Path | Perm | Purpose |
|---|---|---|---|
| GET | `/api/navigation/me` | any | `{ modules: [ { code, title, icon, route, actionId, children[] } ], homeRoute }` - filtered for the caller |
| GET | `/api/navigation` | 304 | flat list incl. inactive (admin screen) |
| PUT | `/api/navigation` | 304 | upsert one item; gate with `actionId` **or** mint a permission inline with `newPermissionName` (created under the parent node's permission) |
| DELETE | `/api/navigation/{id}` | 304 | soft-delete the item and its subtree |
| GET | `/api/permissions/catalog` | 304 | `RoleAction` tree: module -> screen -> action |
| PUT | `/api/permissions` | 304 | create (`actionId` null; `parentActionId` null = new module) or rename / re-parent a permission |
| GET | `/api/roles` | 301 | roles |
| PUT | `/api/roles` | 302 | create / rename a role |
| GET | `/api/roles/{id}/permissions` | 304 | the catalog tree with `granted` per node for this role |
| PUT | `/api/roles/{id}/permissions` | 304 | replace the role's grants (`{ actionIds: [...] }`, ancestors added); takes effect within 5 min for signed-in users |
| GET | `/api/auth/me` | any | `permissions` = flat effective ActionIds for in-page hiding; `roles` with validity |

Permissions 301/302/304 are the legacy Role module rights (Super Admin + Sales Manager today). Every change is
written to `AuditLog` (`Role.PermissionsChanged` carries added/removed ids).

## Admin flow: adding a module with screens
1. `PUT /api/permissions { name: "Warehouse" }` -> module permission, e.g. 910.
2. `PUT /api/navigation { code: "warehouse", title: "Warehouse", icon: "fa-warehouse", sortOrder: 45, actionId: 910 }`.
3. `PUT /api/navigation { parentCode: "warehouse", code: "warehouse.bins", title: "Bins", route: "/warehouse/bins", sortOrder: 10, newPermissionName: "Bins" }`
   - creates permission "Bins" under 910 and the leaf in one call.
4. `PUT /api/roles/17/permissions { actionIds: [ ...existing..., <bins id> ] }` - the Operation Manager now sees
   Warehouse -> Bins; the module permission is added automatically.
5. Frontend: implement the route component for code `warehouse.bins`; until then it renders the placeholder.

Backend endpoints for the new screen use `[HasPermission(<bins id>)]`; regenerate `Permissions.cs` with
`tools/gen-permissions.ps1` so the constant exists.

## Seeded modules (from the legacy sidebar, regrouped)
```
Stock (400)        stock.list (417), stock.pricing (577), stock.sales (540)
Customers (100)    customers.list (101), customers.inquiries (537), customers.tagging (526), customers.my-tagging (907)
Operations (900)   operations.shipping (611) -> schedules / manage; documents (590); inspection (603); vendors (605)
Finance (901)      finance.bank-statements (501), finance.income (521)
My Task (524)      tasks
People (902)       people.employees (201), people.roles (301)
Reports (549)      reports
Transport (630)    transport.quotes (631), transport.admin (632)
Settings (903)     settings.notifications (904), settings.master-data (905), settings.navigation (906)
```
Routes are proposals for the frontend team; change them with `PUT /api/navigation` (no deploy).

## Frontend recipe
1. After login (or tenant switch) call `/api/navigation/me`; render `modules` recursively; navigate to `homeRoute`.
2. Register one route component per `code` you implement; render unknown codes as a "coming soon" placeholder so
   the backend can seed items ahead of the UI.
3. Re-fetch on `switch-tenant` and when `/api/auth/me` shows a changed role set (a temporary role can appear or
   lapse within a minute).
4. Inside a screen, gate controls with `permissions.includes(actionId)` from `/api/auth/me`; ids come from
   `/api/permissions/catalog` (Level 2 nodes) and `src/Jaftim.Domain/Security/Permissions.cs`.
5. Admin screens: "Roles & Rights" = `/api/roles/{id}/permissions` tree with checkboxes; "Navigation" =
   `/api/navigation` + `/api/permissions/catalog` for the permission picker.

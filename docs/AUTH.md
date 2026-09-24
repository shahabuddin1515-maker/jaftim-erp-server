# Authentication, tenancy & authorization

## What the legacy app does (so we preserve the right things)

- ASP.NET Core Identity cookie login (`Areas/Identity/Pages/Account/Login.cshtml.cs`): `PasswordSignInAsync` with
  `lockoutOnFailure: false`; after success, `UserGetByEmail` -> if `StatusId != Active` sign out; every attempt
  logged via `LogLoginAttempt`; redirect to `Role.DefaultPath`.
- `BaseController.OnActionExecuting` on every request: rebuild `SessionUser` (UserProfile + Role +
  `RoleActionGetByRoleId`), IP allowlist (static CIDRs AND `Base_WhitelistedIPs`, bypass if `RemoteAccessAllowed`),
  re-check active status, `SaveActionURL`. Server-side route permission check **commented out**.
- One role per user (`UserProfile.RoleId`); users/customers are created through `UserManager.CreateAsync` **and**
  directly in SQL (`InquiryImport_FromLead`, `BulkInquiryImport_V2`, `tpi_respondio_contactsync_process` insert
  `AspNetUsers`). `RoleActionMapping` has rows for 10 of 22 roles.

## v2 model (decided 2026-09-18)

```
catalog DB (shared)                      tenant DB (one per company; legacy schema untouched)
  Tenant  (code, connection string)        UserProfile.RoleId  = PRIMARY role (what every procedure sees)
  Account (email, PasswordHash, TokenVersion)   UserRole       = extra roles, optional ValidFrom/ValidTo (v2)
  AccountTenant (AccountId, TenantId, UserProfileId, IsDefault)   RoleActionMapping = permissions per role (legacy)
  AccountRefreshToken (per session, bound to one tenant)         AspNetUsers = kept; procedures insert customers into it
  AuthAuditLog                                                   AuditLog (v2 business audit)
```

- **Database per tenant.** The JWT carries `tid`; every tenant-bound repository call goes to that tenant's database
  (`TenantConnectionFactory` -> catalog `Tenant.ConnectionString`, cached 5 min). Row-level `CompanyId` is irrelevant
  (always 1 inside a tenant DB).
- **Users can span tenants.** One catalog `Account` per e-mail, one `AccountTenant` row per tenant it belongs to
  (each with the `UserProfileId` inside that tenant). Login picks a tenant; `switch-tenant` issues tokens for another.
- **Multiple roles, time-bound.** Permissions = union of `RoleActionMapping` over the *effective* roles: the primary
  role plus every active `UserRole` row inside its `[ValidFromUtc, ValidToUtc)` window. The primary role is what the
  69 legacy procedures that branch on `RoleId` use (row scoping, approval routing) - additional roles never change
  procedure behaviour, only what the API lets the user call. Cache: role -> actions 5 min, user -> roles 60 s, so a
  temporary role starts/stops within a minute (verified live: 36 -> 99 -> 36 permissions).

### Credential store
`Account.PasswordHash` is the ASP.NET Identity V3 format, verified/produced by `IdentityCompatiblePasswordHasher`
(no Identity package). Seeded from each tenant's `AspNetUsers` by `AccountSyncJob` (every minute, per tenant), which
also links the same e-mail across tenants without duplicating the account. Direction of truth per account:
- tenant -> catalog while `Account.PasswordChangedByApiAtUtc IS NULL` (the legacy app still owns the password);
- catalog -> tenant once the API has changed it (`change-password` mirrors the new hash into `AspNetUsers` of
  every membership and stamps the column; the job then pushes the catalog hash down, never up).

### Tokens
| | Access token | Refresh token |
|---|---|---|
| Format | JWT HS256 | 64 random bytes, base64 |
| Lifetime | `Auth:AccessTokenMinutes` (30) | `Auth:RefreshTokenDays` (14) |
| Storage | none | `AccountRefreshToken` - SHA-256 hex, `TenantId` |
| Revocation | `tv` claim vs `Account.TokenVersion` (per request, cached 60 s) | `RevokedAtUtc`; rotated on refresh; reuse -> revoke all + bump TokenVersion |

Claims: `sub` = AccountId, `tid`/`tcode` = tenant, `uid` = UserProfileId in that tenant (**`@CreatedBy` for every
SP**), `rid`/`role` = primary role, `utid`, `cid`, `name`, `email`, `tv`, `jti`. Permissions and extra roles are
**not** in the token; `GET /api/auth/me` returns them.

### Endpoints
| Method | Path | Auth | Notes |
|---|---|---|---|
| POST | `/api/auth/login` | anonymous, 10/min/IP | `{ email, password, tenantCode? }`. One membership or a default -> tokens. Several without a choice -> **409** `{ code: "TenantSelectionRequired", memberships: [...] }` |
| POST | `/api/auth/refresh` | anonymous, rate-limited | same tenant as the refresh token |
| POST | `/api/auth/switch-tenant` | bearer | `{ tenantCode }` -> new token pair for that membership (404 if not a member) |
| GET | `/api/auth/memberships` | bearer | companies the account can sign in to |
| POST | `/api/auth/logout` | bearer | `{ refreshToken?, allDevices }`; `allDevices` bumps TokenVersion |
| GET | `/api/auth/me` | bearer | profile, tenant, `roles` (effective, with validity), `permissions` (ActionIds), `memberships` |
| POST | `/api/auth/change-password` | bearer | verifies current, re-hashes, mirrors to every tenant, revokes all sessions |
| GET/POST | `/api/users/{id}/roles` | 203 / 202 | list all assignments (incl. expired/future) / assign or re-scope `{ roleId, validFromUtc?, validToUtc?, reason? }` |
| DELETE | `/api/users/{id}/roles/{roleId}` | 202 | revoke a non-primary role |
| PUT | `/api/users/{id}/roles/primary` | 202 | change the primary role (`UserProfile_SetPrimaryRole`) |
| GET | `/api/tenants` | 304 | tenant registry (no connection strings) |
| GET | `/api/audit?entityType=&entityId=` | 304 | business audit trail of the current tenant |

Role rules (`UserRoleService`): role must exist; staff cannot get `Customer`, customers cannot get staff roles;
the primary role cannot be time-boxed or revoked (change it first); `validTo > validFrom`; every change invalidates
the user's permission cache and writes `AuditLog` (`UserRole.Assigned/Revoked`, `UserProfile.PrimaryRoleChanged`).

### Authorization
- Fallback policy: authenticated. `[AllowAnonymous]` is explicit and rare.
- `[HasPermission(Permissions.X)]` -> `PermissionHandler` -> `CachedPermissionService.HasPermissionAsync(uid, actionId)`.
- Row-level scoping stays inside the procedures, keyed on `@CreatedBy` and the primary role.
- After saving role rights call `IPermissionService.InvalidateRole`; after deactivating a user call
  `IAuthRepository.BumpTokenVersionAsync` and `TokenVersionValidator.Invalidate`.

### Per-request validation (`TokenVersionValidator`)
Binds the scoped tenant from `tid`, then (cached 60 s per account+tenant) checks `Account.IsActive`,
`Account.TokenVersion == tv` and the tenant `UserProfile` is active/not deleted.

### Network restriction
`IpAllowlistMiddleware` unchanged from before (static CIDRs + `Base_WhitelistedIPs` of the current tenant, bypass
via `RemoteAccessAllowed`); off in Development.

### Frontend integration checklist
1. Handle 409 `TenantSelectionRequired` on login by showing `memberships` and retrying with `tenantCode`.
2. Store the access token in memory; keep the refresh token out of localStorage. On 401 -> `/api/auth/refresh` once.
3. Offer a company switcher from `/api/auth/memberships` -> `/api/auth/switch-tenant`; replace both tokens.
4. Hide/show by `permissions.includes(actionId)`; show `roles` with validity on the profile screen.
5. Expect ProblemDetails on errors: `{ type, title, status, detail, instance, errors?, code?, memberships? }`.

# Notifications (v2 API)

The subsystem itself is unchanged: the same tables and procedures the legacy app uses
(`Base_NotificationType`, `NotificationTypeRoleMap`, `NotificationTypeUserMap`, `Notification`,
`NotificationRecipient`, `Notification_Create`, …). The legacy `NOTIFICATIONS.md` in `C:\jaftimv2\Jaftim` remains the
reference for how an audience is composed; this page is only the v2 surface.

## Raising an event

Business code calls `INotificationDispatcher.NotifyAsync(new NotificationRequest(...))` and nothing else - never
SignalR, never SQL. The dispatcher persists through `Notification_Create` (which resolves the audience and returns
the recipient ids) and then pushes to those users. It is **best-effort**: a notification failure is logged and never
breaks the business action that raised it.

```csharp
await notifications.NotifyAsync(new NotificationRequest(
    NotificationTypeCodes.StockCreated,
    Message: $"Stock {stockId} created",
    EntityType: "Stock", EntityId: stockId,
    Url: $"/stock/{stockId}"), ct);
```

## Realtime

`/hubs/notifications` (SignalR, JWT via `?access_token=`). Each connection joins the group
`t{TenantId}-user-{UserProfileId}` - keyed by tenant because profile ids repeat across tenant databases - and
receives `ReceiveNotification` with the payload. Scale-out needs a backplane (Azure SignalR Service or Redis); the
group model works unchanged.

## Inbox endpoints (the bell)

Scoped to the signed-in user; authentication is the only requirement, as in the legacy app.

| Endpoint | Purpose |
|---|---|
| `GET /api/notifications/summary` | badge counts (`unreadCount`, `unseenCount`) |
| `GET /api/notifications?onlyUnread=&page=&pageSize=` | paged inbox (`Notification_GetByUser`) |
| `POST /api/notifications/mark-all-seen` | clears the red badge without marking items read |
| `POST /api/notifications/mark-all-read` | marks every item read |
| `POST /api/notifications/{notificationRecipientId}/mark-read` | one item; the procedure scopes by recipient |

**Seen vs read**: `IsSeen` drives the red badge (reset when the dropdown opens), `IsRead` is per item (bold until
opened). A row carries `relatedCustomerId` so the UI can fall back to the customer page when the reader has no
access to the My Task destination.

## Routing admin (`/api/notification-settings`, permission 904)

Who receives what is data - no deploy, and no cache to invalidate: `Notification_Create` reads the configuration on
every raise. The legacy screen was gated on `RoleId IN (1, 10)`; here it is the **Notification Settings** permission
(ActionId 904, nav node `settings.notifications`), so it can be delegated. Every change is written to `AuditLog`.

| Endpoint | Purpose |
|---|---|
| `GET /types` | the admin grid: every wired type with its strategy, role ids and override count |
| `GET /types/{id}` | settings + role set + per-user overrides |
| `PUT /types/{id}/settings` | `isActive`, `recipientStrategy`, `includeSystemAdmins` |
| `PUT /types/{id}/roles` | **replaces** the whole role set |
| `PUT /types/{id}/users` | grant (`Include`) or deny (`Exclude`) one staff user |
| `DELETE /types/{id}/users/{userProfileId}` | removes the override |
| `GET /roles`, `GET /users?query=` | pickers (active staff only) |

`recipientStrategy` decides the **base** audience: `RoleMap` (users in the type's roles), `ReportingChainUp` (the
actor's managers, `fn_GetUserParentProfiles`), `ExplicitOnly` (nobody unless the raising code names them). On top of
that come the role map, system admins (when `includeSystemAdmins`), explicit recipients, then per-user
`Include`/`Exclude` overrides, which always win. Only active (`StatusId = 2`), non-deleted staff can be overridden -
a customer or an inactive profile is refused with 404.

## Adding a new event type

1. Add a constant to `NotificationTypeCodes`.
2. Insert the catalog row (`Base_NotificationType`: code, name, category, icon, priority, strategy) in a
   `database/v2/NNN_*.sql` script and apply it **locally**; set `IsImplemented = 1` in the same change that wires
   the trigger, so the admin grid only ever lists events that can actually fire.
3. Raise it from the business flow via `INotificationDispatcher`.
4. Default routing is data - set it through `/api/notification-settings`, not in code.

using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Domain.Entities.Notifications;
using Jaftim.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>
/// Who receives which notification, as data (legacy NotificationSettingsController, which was gated on
/// RoleId IN (1, 10); here on the Notification Settings permission so it can be delegated). Per type: an active
/// switch, a recipient strategy, an "include system admins" flag, a role set, and per-user grant/deny overrides
/// that always win. Changes apply to the next raise - Notification_Create reads the config every time.
/// See NOTIFICATIONS.md section 11 for how the audience is composed.
/// </summary>
[Route("api/notification-settings")]
[HasPermission(Permissions.NotificationSettings)]
public sealed class NotificationSettingsController(INotificationSettingsService settings) : ApiControllerBase
{
    /// <summary>Event catalog with its current routing (the admin grid). Only wired event types are listed.</summary>
    [HttpGet("types")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<NotificationTypeConfig>>>> GetTypes(CancellationToken ct) =>
        Ok(await settings.GetTypesAsync(ct));

    /// <summary>One event type: settings, its role set and its per-user overrides.</summary>
    [HttpGet("types/{notificationTypeId:int}")]
    public async Task<ActionResult<ApiResponse<NotificationTypeDetail>>> GetTypeDetail(int notificationTypeId, CancellationToken ct) =>
        Ok(await settings.GetTypeDetailAsync(notificationTypeId, ct));

    /// <summary>Active switch, recipient strategy (RoleMap | ReportingChainUp | ExplicitOnly) and the system-admin flag.</summary>
    [HttpPut("types/{notificationTypeId:int}/settings")]
    public async Task<ActionResult<ApiResponse<NotificationTypeDetail>>> SaveSettings(int notificationTypeId, [FromBody] NotificationTypeSettingsRequest request, CancellationToken ct) =>
        Ok(await settings.SaveSettingsAsync(notificationTypeId, request, ct), "Settings saved.");

    /// <summary>Replaces the whole role set of the type.</summary>
    [HttpPut("types/{notificationTypeId:int}/roles")]
    public async Task<ActionResult<ApiResponse<NotificationTypeDetail>>> SaveRoles(int notificationTypeId, [FromBody] NotificationTypeRolesRequest request, CancellationToken ct) =>
        Ok(await settings.SaveRolesAsync(notificationTypeId, request, ct), "Roles saved.");

    /// <summary>Grants (Include) or denies (Exclude) one staff user regardless of their roles.</summary>
    [HttpPut("types/{notificationTypeId:int}/users")]
    public async Task<ActionResult<ApiResponse<NotificationTypeDetail>>> SaveUserOverride(int notificationTypeId, [FromBody] NotificationUserOverrideRequest request, CancellationToken ct) =>
        Ok(await settings.SaveUserOverrideAsync(notificationTypeId, request, ct), "Override saved.");

    /// <summary>Removes a user override, putting that user back under the role/strategy rules.</summary>
    [HttpDelete("types/{notificationTypeId:int}/users/{userProfileId:long}")]
    public async Task<ActionResult<ApiResponse<NotificationTypeDetail>>> RemoveUserOverride(int notificationTypeId, long userProfileId, CancellationToken ct) =>
        Ok(await settings.RemoveUserOverrideAsync(notificationTypeId, userProfileId, ct), "Override removed.");

    /// <summary>Role picker for the roles panel.</summary>
    [HttpGet("roles")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RoleOption>>>> GetRoles(CancellationToken ct) =>
        Ok(await settings.GetRolesAsync(ct));

    /// <summary>Staff search for the grant/deny picker (active staff only, max 20).</summary>
    [HttpGet("users")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<UserSearchResult>>>> SearchUsers([FromQuery] string? query, CancellationToken ct) =>
        Ok(await settings.SearchUsersAsync(query, ct));
}

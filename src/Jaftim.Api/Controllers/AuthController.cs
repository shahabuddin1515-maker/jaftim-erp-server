using FluentValidation;
using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jaftim.Api.Controllers;

public sealed class AuthController(
    IAuthService auth,
    IValidator<LoginRequest> loginValidator,
    IValidator<RefreshTokenRequest> refreshValidator,
    IValidator<SwitchTenantRequest> switchValidator,
    IValidator<ChangePasswordRequest> changePasswordValidator,
    ICurrentUser currentUser,
    ITenantContext tenant,
    TokenVersionValidator tokenVersions) : ApiControllerBase
{
    /// <summary>Email + password (+ optional tenantCode) -> access token (JWT) + refresh token. 409 when the account has several companies and none was chosen.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<ApiResponse<TokenResponse>>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        await loginValidator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await auth.LoginAsync(request, ClientInfo, ct));
    }

    /// <summary>Rotate the refresh token and issue a new access token for the same tenant.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<ApiResponse<TokenResponse>>> Refresh([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        await refreshValidator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await auth.RefreshAsync(request, ClientInfo, ct));
    }

    /// <summary>Issue a token pair for another company the account is a member of. The previous refresh token stays valid until its own logout.</summary>
    [HttpPost("switch-tenant")]
    public async Task<ActionResult<ApiResponse<TokenResponse>>> SwitchTenant([FromBody] SwitchTenantRequest request, CancellationToken ct)
    {
        await switchValidator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await auth.SwitchTenantAsync(request, ClientInfo, ct));
    }

    /// <summary>Companies the current account can sign in to.</summary>
    [HttpGet("memberships")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TenantMembership>>>> Memberships(CancellationToken ct) =>
        Ok(await auth.GetMembershipsAsync(ct));

    /// <summary>Revoke this device's refresh token, or every session when allDevices=true (also invalidates live access tokens).</summary>
    [HttpPost("logout")]
    public async Task<ActionResult<ApiResponse<object?>>> Logout([FromBody] LogoutRequest request, CancellationToken ct)
    {
        await auth.LogoutAsync(request.RefreshToken, request.AllDevices, ClientInfo, ct);
        if (request.AllDevices && currentUser.AccountId is { } id) tokenVersions.Invalidate(id, tenant.TenantId);
        return Ok("Logged out.");
    }

    /// <summary>The caller's profile, tenant, effective roles, permission ids (RoleAction.ActionId) and memberships - what the UI renders from.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<ApiResponse<AuthenticatedUser>>> Me(CancellationToken ct) =>
        Ok(await auth.GetCurrentUserAsync(ct));

    [HttpPost("change-password")]
    public async Task<ActionResult<ApiResponse<object?>>> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        await changePasswordValidator.ValidateAndThrowAppAsync(request, ct);
        await auth.ChangePasswordAsync(request.CurrentPassword, request.NewPassword, ClientInfo, ct);
        if (currentUser.AccountId is { } id) tokenVersions.Invalidate(id, tenant.TenantId);
        return Ok("Password changed. Please sign in again.");
    }
}

/// <param name="RefreshToken">This device's refresh token to revoke (optional when AllDevices is true).</param>
/// <param name="AllDevices">Revoke every session of the account and invalidate live access tokens.</param>
public sealed record LogoutRequest(string? RefreshToken, bool AllDevices = false);

/// <param name="CurrentPassword">Must match the current password.</param>
/// <param name="NewPassword">8-128 characters with at least one letter and one digit.</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        // Stronger than the legacy policy (6 chars, no complexity) but not so strict it blocks migrated users at first login.
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128)
            .Matches("[A-Za-z]").WithMessage("Password must contain a letter.")
            .Matches("[0-9]").WithMessage("Password must contain a digit.");
    }
}

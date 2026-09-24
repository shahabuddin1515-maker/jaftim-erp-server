using FluentValidation;

namespace Jaftim.Application.Modules.Auth;

public interface IAuthService
{
    Task<TokenResponse> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken ct = default);
    Task<TokenResponse> RefreshAsync(RefreshTokenRequest request, ClientInfo client, CancellationToken ct = default);
    /// <summary>Issues a new token pair for another tenant the current account is a member of.</summary>
    Task<TokenResponse> SwitchTenantAsync(SwitchTenantRequest request, ClientInfo client, CancellationToken ct = default);
    /// <summary>Revokes the given refresh token (single device) or, with revokeAll, every session of the current account.</summary>
    Task LogoutAsync(string? refreshToken, bool revokeAll, ClientInfo client, CancellationToken ct = default);
    Task<AuthenticatedUser> GetCurrentUserAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TenantMembership>> GetMembershipsAsync(CancellationToken ct = default);
    Task ChangePasswordAsync(string currentPassword, string newPassword, ClientInfo client, CancellationToken ct = default);
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
        RuleFor(x => x.TenantCode).MaximumLength(50);
    }
}

public sealed class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(512);
    }
}

public sealed class SwitchTenantRequestValidator : AbstractValidator<SwitchTenantRequest>
{
    public SwitchTenantRequestValidator()
    {
        RuleFor(x => x.TenantCode).NotEmpty().MaximumLength(50);
    }
}

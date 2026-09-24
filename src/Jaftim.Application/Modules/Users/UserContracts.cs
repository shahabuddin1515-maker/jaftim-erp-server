using System.Security.Cryptography;
using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Auth;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Enums;
using Jaftim.Domain.Exceptions;
using Jaftim.Domain.Security;

namespace Jaftim.Application.Modules.Users;

/// <summary>UserGetAll row (staff grid). UserProfile.* plus the joined display columns.</summary>
public sealed class UserListItem
{
    public long UserProfileId { get; set; }
    public string? Code { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? URL { get; set; }
    public string? AspNetUserId { get; set; }
    public long RoleId { get; set; }
    public string? RoleName { get; set; }
    public int GenderId { get; set; }
    public string? GenderName { get; set; }
    public int StatusId { get; set; }
    public string? UserStatusName { get; set; }
    public int? CountryId { get; set; }
    public string? CountryName { get; set; }
    public int? CityId { get; set; }
    public string? CityName { get; set; }
    public string? Address { get; set; }
    public string? CountryCode { get; set; }
    public string? ImageURL { get; set; }
    public long? ReportsTo_UserId { get; set; }
    public bool? RemoteAccessAllowed { get; set; }
    public int TaggedCount { get; set; }
    public string? Phone { get; set; }
    public string? SecondaryPhone { get; set; }
    public string? WhatsAppNumber { get; set; }
    public string? CustomerTagLimit { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>UserGetById row (edit form).</summary>
public sealed class UserDetail
{
    public long UserProfileId { get; set; }
    public string? Code { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? URL { get; set; }
    public string? AspNetUserId { get; set; }
    public long RoleId { get; set; }
    public int GenderId { get; set; }
    public string? Address { get; set; }
    public string? CountryCode { get; set; }
    public int? CountryId { get; set; }
    public int? CityId { get; set; }
    public int StatusId { get; set; }
    public string? UserStatusName { get; set; }
    public string? ImageURL { get; set; }
    public long? ReportsTo_UserId { get; set; }
    public bool? RemoteAccessAllowed { get; set; }
    public long? OfficeLocation_CountryId { get; set; }
    public string? WhatsAppNumber { get; set; }
    public string? CustomerTagLimit { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public long? IsDeleted { get; set; }
}

/// <summary>
/// Create/update a staff user (legacy UserController.UserSave form). Email and Password apply on create only: the
/// e-mail is the login identity and is immutable through this endpoint. Password may be omitted on create - a
/// cryptographically random temporary password is then generated and returned once in the create response.
/// </summary>
public sealed record UserUpsertRequest(
    string FullName,
    string? Email,
    string? Password,
    long RoleId,
    int GenderId,
    int StatusId,
    string? Code = null,
    string? Url = null,
    string? Address = null,
    string? CountryCode = null,
    int? CountryId = null,
    int? CityId = null,
    long? ReportsToUserId = null,
    long? OfficeLocationCountryId = null,
    string? WhatsAppNumber = null,
    int? CustomerTagLimit = null,
    bool RemoteAccessAllowed = false);

public sealed record UserCreatedResult(long UserProfileId, string AccountId, string Email, string? TemporaryPassword, bool LinkedExistingAccount);

public sealed class UserUpsertRequestValidator : AbstractValidator<UserUpsertRequest>
{
    public UserUpsertRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => x.Email is not null);
        RuleFor(x => x.Password).MinimumLength(8).MaximumLength(128)
            .Matches("[A-Za-z]").WithMessage("Password must contain a letter.")
            .Matches("[0-9]").WithMessage("Password must contain a digit.")
            .When(x => x.Password is not null);
        RuleFor(x => x.RoleId).GreaterThan(0);
        RuleFor(x => x.StatusId).Must(s => s is (int)UserStatus.Active or (int)UserStatus.Inactive).WithMessage("statusId must be 1 (Inactive) or 2 (Active).");
        RuleFor(x => x.Code).MaximumLength(50);
        RuleFor(x => x.CountryCode).MaximumLength(10);
        RuleFor(x => x.WhatsAppNumber).MaximumLength(200);
        RuleFor(x => x.CustomerTagLimit).GreaterThanOrEqualTo(0).When(x => x.CustomerTagLimit is not null);
    }
}

/// <summary>The parameter set of EXEC UserSave, copied verbatim from the legacy UserRepository.UserSave.</summary>
public sealed record UserSaveArgs(
    long UserProfileId,
    string? Code,
    string FullName,
    string? Username,
    string? Email,
    string? Url,
    string? AspNetUserId,
    long RoleId,
    int GenderId,
    int StatusId,
    string? CustomerTagLimit,
    string? Address,
    string? CountryCode,
    int? CountryId,
    int? CityId,
    long? ReportsToUserId,
    long? OfficeLocationCountryId,
    string? OfficialWhatsapp,
    bool RemoteAccessAllowed);

public interface IUserService
{
    /// <summary>Staff list; the legacy procedure decides visibility from the caller's PRIMARY role (Super Admin sees all, System Admin excludes admins).</summary>
    Task<IReadOnlyList<UserListItem>> GetAllAsync(int? userTypeId, CancellationToken ct = default);
    Task<UserDetail> GetByIdAsync(long userProfileId, CancellationToken ct = default);
    Task<UserCreatedResult> CreateAsync(UserUpsertRequest request, CancellationToken ct = default);
    Task UpdateAsync(long userProfileId, UserUpsertRequest request, CancellationToken ct = default);
    /// <summary>EXEC ActiveInActiveCall - flips StatusId 1&lt;-&gt;2. Deactivation also revokes the account's live sessions in this tenant.</summary>
    Task<int> ToggleActiveAsync(long userProfileId, CancellationToken ct = default);
}

/// <summary>
/// Legacy flow: UserManager.CreateAsync(email, password) -> AspNetUserId -> EXEC UserSave. v2: the credential is the
/// catalog Account (reused when the e-mail already exists in another tenant), mirrored into this tenant's
/// AspNetUsers row (the procedures and the legacy app still read it), then UserSave, then the membership row so the
/// new user can log in immediately without waiting for AccountSyncJob.
/// </summary>
public sealed class UserService(
    IUserRepository users,
    IAuthRepository catalog,
    IPasswordHasher hasher,
    IPermissionService permissions,
    IAuditWriter audit,
    ICurrentUser actor,
    ITenantContext tenant,
    IValidator<UserUpsertRequest> validator) : IUserService
{
    public Task<IReadOnlyList<UserListItem>> GetAllAsync(int? userTypeId, CancellationToken ct = default) =>
        users.GetAllAsync(userTypeId, ct);

    public async Task<UserDetail> GetByIdAsync(long userProfileId, CancellationToken ct = default) =>
        await users.GetDetailAsync(userProfileId, ct) is { } u && (u.IsDeleted ?? 0) == 0
            ? u
            : throw new NotFoundException("User", userProfileId);

    public async Task<UserCreatedResult> CreateAsync(UserUpsertRequest request, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAppAsync(request, ct);
        if (string.IsNullOrWhiteSpace(request.Email))
            throw new BusinessRuleException("email is required to create a user.");
        await EnsureStaffRoleAsync(request.RoleId, ct);

        string email = request.Email.Trim();
        if (await users.GetByEmailAsync(email, ct) is not null)
            throw new ConflictException($"A user with e-mail '{email}' already exists in this company.");

        // Credential: reuse the catalog account when this person already exists in another tenant.
        Domain.Entities.Tenancy.Account? existing = await catalog.GetAccountByEmailAsync(email, ct);
        string? temporaryPassword = null;
        string passwordHash;
        if (existing is not null)
        {
            passwordHash = existing.PasswordHash ?? throw new BusinessRuleException("The existing account has no password set.");
        }
        else
        {
            temporaryPassword = request.Password ?? GenerateTemporaryPassword();
            passwordHash = hasher.Hash(temporaryPassword);
            if (request.Password is not null) temporaryPassword = null; // caller chose it; do not echo it back
        }
        string accountId = existing?.AccountId ?? Guid.NewGuid().ToString();

        // Tenant AspNetUsers row (what UserManager.CreateAsync did) - the procedures and the legacy app read it.
        await users.InsertAspNetUserAsync(accountId, email, passwordHash, ct);

        await users.SaveAsync(ToArgs(0, request, email, accountId), ct);
        UserProfileWithRole created = await users.GetByAspNetUserIdAsync(accountId, ct)
            ?? throw new InvalidOperationException("UserSave did not create the profile.");

        // Catalog membership now, so the user can sign in before the next AccountSyncJob pass.
        await catalog.EnsureAccountAsync(accountId, email, passwordHash, tenant.TenantId, created.UserProfileId, request.StatusId == (int)UserStatus.Active, ct);

        await audit.RecordAsync("User.Created", "UserProfile", created.UserProfileId, null,
            new { created.UserProfileId, email, request.FullName, request.RoleId, request.StatusId, LinkedExistingAccount = existing is not null }, ct);

        return new UserCreatedResult(created.UserProfileId, accountId, email, temporaryPassword, existing is not null);
    }

    public async Task UpdateAsync(long userProfileId, UserUpsertRequest request, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAppAsync(request, ct);
        UserDetail before = await GetByIdAsync(userProfileId, ct);
        await EnsureStaffRoleAsync(request.RoleId, ct);
        if (before.RoleId == RoleIds.Customer)
            throw new BusinessRuleException("Customer profiles are managed through the customer endpoints.");

        // Email/AspNetUserId are immutable here: the procedure ignores them on update and the login identity lives in the catalog.
        await users.SaveAsync(ToArgs(userProfileId, request, before.Email, before.AspNetUserId), ct);
        UserDetail after = await GetByIdAsync(userProfileId, ct);

        if (before.RoleId != after.RoleId) permissions.InvalidateUser(userProfileId);
        if (before.StatusId != after.StatusId && after.StatusId != (int)UserStatus.Active && after.AspNetUserId is { } id)
            await catalog.BumpTokenVersionAsync(id, ct);

        await audit.RecordAsync("User.Updated", "UserProfile", userProfileId, before, after, ct);
    }

    public async Task<int> ToggleActiveAsync(long userProfileId, CancellationToken ct = default)
    {
        UserDetail before = await GetByIdAsync(userProfileId, ct);
        if (userProfileId == actor.UserProfileId)
            throw new BusinessRuleException("You cannot deactivate your own account.");

        await users.ToggleActiveAsync(userProfileId, ct);
        UserDetail after = await GetByIdAsync(userProfileId, ct);

        if (after.StatusId != (int)UserStatus.Active && after.AspNetUserId is { } id)
            await catalog.BumpTokenVersionAsync(id, ct); // live tokens die within the validator cache window
        permissions.InvalidateUser(userProfileId);
        await audit.RecordAsync("User.StatusToggled", "UserProfile", userProfileId, new { before.StatusId }, new { after.StatusId }, ct);
        return after.StatusId;
    }

    private async Task EnsureStaffRoleAsync(long roleId, CancellationToken ct)
    {
        Role role = (await users.GetRolesAsync(ct)).FirstOrDefault(r => r.RoleId == roleId && r.IsDeleted != true)
            ?? throw new NotFoundException("Role", roleId);
        if (role.RoleId == RoleIds.Customer)
            throw new BusinessRuleException("The Customer role cannot be assigned through the staff endpoints.");
    }

    private static UserSaveArgs ToArgs(long userProfileId, UserUpsertRequest r, string? email, string? aspNetUserId) => new(
        userProfileId, r.Code, r.FullName.Trim(), email, email, r.Url, aspNetUserId, r.RoleId, r.GenderId, r.StatusId,
        r.CustomerTagLimit?.ToString(), r.Address, r.CountryCode, r.CountryId, r.CityId, r.ReportsToUserId,
        r.OfficeLocationCountryId, r.WhatsAppNumber, r.RemoteAccessAllowed);

    /// <summary>16 chars from an unambiguous alphabet, always containing a letter and a digit (satisfies the change-password policy).</summary>
    internal static string GenerateTemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        Span<char> chars = stackalloc char[16];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        chars[0] = alphabet[RandomNumberGenerator.GetInt32(0, 24)];   // a letter
        chars[1] = alphabet[RandomNumberGenerator.GetInt32(47, alphabet.Length)]; // a digit
        return new string(chars);
    }
}

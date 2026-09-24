using System.Data;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Users;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>UserRole_* / UserProfile_SetPrimaryRole (database/v2/001_UserRole.sql). v2 procedures: no legacy audit trio, explicit actor.</summary>
public sealed class UserRoleRepository(IDbExecutor db) : IUserRoleRepository
{
    public Task<IReadOnlyList<UserRoleAssignment>> GetByUserAsync(long userProfileId, CancellationToken ct = default) =>
        db.QueryAsync<UserRoleAssignment>(SpCall.Procedure("UserRole_GetByUser").With("@UserProfileId", userProfileId).WithoutAudit(), ct);

    public Task<IReadOnlyList<EffectiveUserRole>> GetEffectiveAsync(long userProfileId, DateTime asOfUtc, CancellationToken ct = default) =>
        db.QueryAsync<EffectiveUserRole>(
            SpCall.Procedure("UserRole_GetEffective").With("@UserProfileId", userProfileId).With("@AsOfUtc", asOfUtc, DbType.DateTime2).WithoutAudit(), ct);

    public Task<long> SaveAsync(long userProfileId, long roleId, DateTime? validFromUtc, DateTime? validToUtc, string? reason, long actorUserProfileId, CancellationToken ct = default) =>
        db.QuerySingleAsync<long>(
            SpCall.Procedure("UserRole_Save")
                .With("@UserProfileId", userProfileId)
                .With("@RoleId", roleId)
                .With("@ValidFromUtc", validFromUtc, DbType.DateTime2)
                .With("@ValidToUtc", validToUtc, DbType.DateTime2)
                .With("@Reason", reason, DbType.String, 400)
                .With("@ActorUserProfileId", actorUserProfileId)
                .WithoutAudit(), ct);

    public Task<int> RevokeAsync(long userProfileId, long roleId, long actorUserProfileId, CancellationToken ct = default) =>
        db.QuerySingleAsync<int>(
            SpCall.Procedure("UserRole_Revoke")
                .With("@UserProfileId", userProfileId)
                .With("@RoleId", roleId)
                .With("@ActorUserProfileId", actorUserProfileId)
                .WithoutAudit(), ct);

    public Task SetPrimaryAsync(long userProfileId, long roleId, long actorUserProfileId, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("UserProfile_SetPrimaryRole")
                .With("@UserProfileId", userProfileId)
                .With("@RoleId", roleId)
                .With("@ActorUserProfileId", actorUserProfileId)
                .WithoutAudit(), ct);
}

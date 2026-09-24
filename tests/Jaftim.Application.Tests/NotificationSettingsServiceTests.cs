using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Domain.Entities.Notifications;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Tests;

public sealed class NotificationSettingsServiceTests
{
    [Fact]
    public async Task Unknown_type_is_404_everywhere()
    {
        NotificationSettingsService service = Build(new SettingsRepoFake { TypeExists = false }, out _);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetTypeDetailAsync(99));
        await Assert.ThrowsAsync<NotFoundException>(() => service.SaveSettingsAsync(99, new NotificationTypeSettingsRequest(true, NotificationRecipientStrategy.RoleMap, false)));
        await Assert.ThrowsAsync<NotFoundException>(() => service.SaveRolesAsync(99, new NotificationTypeRolesRequest([11])));
    }

    [Fact]
    public async Task Save_settings_validates_the_strategy_and_audits_the_change()
    {
        var repo = new SettingsRepoFake();
        NotificationSettingsService service = Build(repo, out AuditSpy audit);

        await Assert.ThrowsAsync<Domain.Exceptions.ValidationException>(() =>
            service.SaveSettingsAsync(1, new NotificationTypeSettingsRequest(true, (NotificationRecipientStrategy)9, false)));

        await service.SaveSettingsAsync(1, new NotificationTypeSettingsRequest(false, NotificationRecipientStrategy.ReportingChainUp, true));

        Assert.Equal(NotificationRecipientStrategy.ReportingChainUp, repo.Settings.RecipientStrategy);
        Assert.True(repo.Settings.IncludeSystemAdmins);
        Assert.False(repo.Settings.IsActive);
        Assert.Single(audit.Records, a => a.Action == "NotificationType.SettingsChanged");
    }

    [Fact]
    public async Task Save_roles_replaces_the_set_and_rejects_unknown_roles()
    {
        var repo = new SettingsRepoFake();
        NotificationSettingsService service = Build(repo, out AuditSpy audit);

        NotificationTypeDetail after = await service.SaveRolesAsync(1, new NotificationTypeRolesRequest([13, 4, 13]));

        Assert.Equal([13, 4], after.RoleIds);          // de-duplicated, whole set replaced
        Assert.Single(audit.Records, a => a.Action == "NotificationType.RolesChanged");

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.SaveRolesAsync(1, new NotificationTypeRolesRequest([999])));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public async Task User_override_must_be_an_active_staff_member_and_removal_requires_an_existing_row()
    {
        var repo = new SettingsRepoFake();
        NotificationSettingsService service = Build(repo, out AuditSpy audit);

        await Assert.ThrowsAsync<NotFoundException>(() => service.SaveUserOverrideAsync(1, new NotificationUserOverrideRequest(4242, NotificationUserOverrideMode.Include)));
        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveUserOverrideAsync(1, 134));

        NotificationTypeDetail after = await service.SaveUserOverrideAsync(1, new NotificationUserOverrideRequest(134, NotificationUserOverrideMode.Exclude));
        Assert.Equal(NotificationUserOverrideMode.Exclude, after.UserOverrides.Single().Mode);

        NotificationTypeDetail removed = await service.RemoveUserOverrideAsync(1, 134);
        Assert.Empty(removed.UserOverrides);
        Assert.Equal(2, audit.Records.Count(a => a.Action.StartsWith("NotificationType.UserOverride")));
    }

    private static NotificationSettingsService Build(SettingsRepoFake repo, out AuditSpy audit)
    {
        audit = new AuditSpy();
        return new NotificationSettingsService(repo, new SettingsUsersFake(), audit,
            new NotificationTypeSettingsRequestValidator(),
            new NotificationTypeRolesRequestValidator(),
            new NotificationUserOverrideRequestValidator());
    }

    /// <summary>Active staff 134 exists; 4242 does not. Mirrors the picker rule the service enforces.</summary>
    private sealed class SettingsUsersFake : Jaftim.Application.Modules.Users.IUserRepository
    {
        public Task<Jaftim.Application.Modules.Users.UserProfileWithRole?> GetByIdAsync(long userProfileId, CancellationToken ct = default) =>
            Task.FromResult(userProfileId == 134 ? new Jaftim.Application.Modules.Users.UserProfileWithRole { UserProfileId = 134, RoleId = 2, StatusId = 2, IsDeleted = 0, FullName = "Agent" } : null);
        public Task<Jaftim.Application.Modules.Users.UserProfileWithRole?> GetByEmailAsync(string email, CancellationToken ct = default) => Task.FromResult<Jaftim.Application.Modules.Users.UserProfileWithRole?>(null);
        public Task<Jaftim.Application.Modules.Users.UserProfileWithRole?> GetByAspNetUserIdAsync(string aspNetUserId, CancellationToken ct = default) => Task.FromResult<Jaftim.Application.Modules.Users.UserProfileWithRole?>(null);
        public Task<IReadOnlyList<Jaftim.Domain.Entities.Users.RoleAction>> GetRoleActionsAsync(long roleId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Jaftim.Domain.Entities.Users.RoleAction>>([]);
        public Task<IReadOnlyList<string>> GetWhitelistedCidrsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task SaveActionUrlAsync(string actionUrl, long userProfileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Jaftim.Domain.Entities.Users.RoleAction>> GetAllRoleActionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Jaftim.Domain.Entities.Users.RoleAction>>([]);
        public Task<IReadOnlyList<Jaftim.Domain.Entities.Users.Role>> GetRolesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Jaftim.Domain.Entities.Users.Role>>([]);
        public Task MirrorPasswordHashAsync(string aspNetUserId, string passwordHash, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Jaftim.Application.Modules.Users.TenantCredentialRow>> GetCredentialRowsForSyncAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Jaftim.Application.Modules.Users.TenantCredentialRow>>([]);
        public Task<IReadOnlyList<Jaftim.Application.Modules.Users.UserListItem>> GetAllAsync(int? userTypeId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Jaftim.Application.Modules.Users.UserListItem>>([]);
        public Task<Jaftim.Application.Modules.Users.UserDetail?> GetDetailAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<Jaftim.Application.Modules.Users.UserDetail?>(null);
        public Task SaveAsync(Jaftim.Application.Modules.Users.UserSaveArgs args, CancellationToken ct = default) => Task.CompletedTask;
        public Task ToggleActiveAsync(long userProfileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task InsertAspNetUserAsync(string id, string email, string passwordHash, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class AuditSpy : IAuditWriter
    {
        public List<(string Action, string EntityType, long? EntityId)> Records { get; } = [];
        public ValueTask RecordAsync(string action, string entityType, long? entityId, object? before, object? after, CancellationToken ct = default)
        {
            Records.Add((action, entityType, entityId));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SettingsRepoFake : INotificationSettingsRepository
    {
        public bool TypeExists { get; init; } = true;
        public NotificationTypeSettings Settings { get; private set; } = new() { NotificationTypeId = 1, Code = "STOCK_CREATED", Name = "Stock created", IsActive = true, RecipientStrategy = NotificationRecipientStrategy.RoleMap };
        private List<long> _roleIds = [11];
        private readonly List<NotificationUserOverride> _overrides = [];

        public Task<IReadOnlyList<NotificationTypeConfig>> GetTypesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NotificationTypeConfig>>([]);

        public Task<NotificationTypeDetail?> GetTypeDetailAsync(int notificationTypeId, CancellationToken ct = default) =>
            Task.FromResult(TypeExists ? new NotificationTypeDetail(Settings, _roleIds.ToList(), _overrides.ToList()) : null);

        public Task SaveSettingsAsync(int notificationTypeId, NotificationTypeSettingsRequest request, CancellationToken ct = default)
        {
            Settings = new NotificationTypeSettings
            {
                NotificationTypeId = Settings.NotificationTypeId, Code = Settings.Code, Name = Settings.Name,
                IsActive = request.IsActive, RecipientStrategy = request.RecipientStrategy, IncludeSystemAdmins = request.IncludeSystemAdmins,
            };
            return Task.CompletedTask;
        }

        public Task SaveRolesAsync(int notificationTypeId, IReadOnlyList<long> roleIds, CancellationToken ct = default)
        {
            _roleIds = roleIds.ToList();
            return Task.CompletedTask;
        }

        public Task SaveUserOverrideAsync(int notificationTypeId, long userProfileId, NotificationUserOverrideMode mode, CancellationToken ct = default)
        {
            _overrides.RemoveAll(o => o.UserProfileId == userProfileId);
            _overrides.Add(new NotificationUserOverride { UserProfileId = userProfileId, Mode = mode, FullName = "Staff" });
            return Task.CompletedTask;
        }

        public Task RemoveUserOverrideAsync(int notificationTypeId, long userProfileId, CancellationToken ct = default)
        {
            _overrides.RemoveAll(o => o.UserProfileId == userProfileId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RoleOption>> GetRolesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RoleOption>>([new RoleOption { RoleId = 11, RoleName = "Sales Manager" }, new RoleOption { RoleId = 13, RoleName = "Finance Manager" }, new RoleOption { RoleId = 4, RoleName = "Finance Director" }]);

        public Task<IReadOnlyList<UserSearchResult>> SearchUsersAsync(string? query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserSearchResult>>([new UserSearchResult { UserProfileId = 134, FullName = "Agent" }]);
    }
}

public sealed class NotificationInboxServiceTests
{
    [Fact]
    public async Task Every_call_is_scoped_to_the_signed_in_user()
    {
        var repo = new InboxRepoFake();
        var service = new NotificationInboxService(repo, new InboxActor(77));

        await service.GetSummaryAsync();
        await service.GetListAsync(onlyUnread: true, new PagedRequest { Page = 2, PageSize = 20 });
        await service.MarkAllSeenAsync();
        await service.MarkAllReadAsync();
        await service.MarkReadAsync(5);

        Assert.All(repo.UserIds, id => Assert.Equal(77, id));
        Assert.Equal(5, repo.UserIds.Count);
        Assert.True(repo.LastOnlyUnread);
        Assert.Equal(20, repo.LastSkip);   // page 2 of 20
    }

    private sealed class InboxActor(long id) : ICurrentUser
    {
        public bool IsAuthenticated => true; public long UserProfileId => id; public string? AccountId => "acc"; public string? Email => "a@b.c";
        public string? FullName => "A"; public long RoleId => 2; public int UserTypeId => 2; public int CompanyId => 1;
    }

    private sealed class InboxRepoFake : INotificationRepository
    {
        public List<long> UserIds { get; } = [];
        public bool LastOnlyUnread { get; private set; }
        public int LastSkip { get; private set; }

        public Task<NotificationCreateResult> CreateAsync(NotificationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NotificationSummary> GetSummaryAsync(long userProfileId, CancellationToken ct = default) { UserIds.Add(userProfileId); return Task.FromResult(new NotificationSummary(0, 0)); }
        public Task<PagedResult<NotificationListItem>> GetByUserAsync(long userProfileId, bool onlyUnread, PagedRequest paging, CancellationToken ct = default)
        {
            UserIds.Add(userProfileId); LastOnlyUnread = onlyUnread; LastSkip = paging.Skip;
            return Task.FromResult(PagedResult<NotificationListItem>.Empty(paging));
        }
        public Task MarkAllSeenAsync(long userProfileId, CancellationToken ct = default) { UserIds.Add(userProfileId); return Task.CompletedTask; }
        public Task MarkAllReadAsync(long userProfileId, CancellationToken ct = default) { UserIds.Add(userProfileId); return Task.CompletedTask; }
        public Task MarkReadAsync(long notificationRecipientId, long userProfileId, CancellationToken ct = default) { UserIds.Add(userProfileId); return Task.CompletedTask; }
    }
}

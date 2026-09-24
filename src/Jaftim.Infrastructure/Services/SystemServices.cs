using System.Linq.Expressions;
using Hangfire;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Notifications;
using Microsoft.Extensions.Options;

namespace Jaftim.Infrastructure.Services;

public sealed class SystemClock : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public sealed class SystemUserOptions
{
    public const string SectionName = "SystemUser";
    /// <summary>UserProfileId stamped as @CreatedBy by background jobs. Must be an existing, active staff profile.</summary>
    public long UserProfileId { get; set; } = 1;
    public int CompanyId { get; set; } = 1;
}

/// <summary>ICurrentUser for hosts with no HTTP caller (Jaftim.Jobs). The API registers its own claims-based implementation.</summary>
public sealed class SystemUser(IOptions<SystemUserOptions> options) : ICurrentUser
{
    public bool IsAuthenticated => true;
    public long UserProfileId => options.Value.UserProfileId;
    public string? AccountId => null;
    public string? Email => "system@jaftim";
    public string? FullName => "System";
    public long RoleId => Domain.Security.RoleIds.SystemAdmin;
    public int UserTypeId => Domain.Security.UserTypeIds.SystemAdmin;
    public int CompanyId => options.Value.CompanyId;
}

/// <summary>Hangfire-backed IJobScheduler. The API only enqueues; Jaftim.Jobs runs the servers.</summary>
public sealed class HangfireJobScheduler(IBackgroundJobClient client) : IJobScheduler
{
    public string Enqueue<TJob>(Expression<Func<TJob, Task>> job, string queue = JobQueues.Default) where TJob : class =>
        client.Create(job, new Hangfire.States.EnqueuedState(queue));

    public string Schedule<TJob>(Expression<Func<TJob, Task>> job, TimeSpan delay) where TJob : class =>
        client.Schedule(job, delay);
}

/// <summary>Used where no realtime channel exists (Jobs). Notifications are still persisted and appear in the inbox.</summary>
public sealed class NoOpNotificationPusher : INotificationPusher
{
    public Task PushAsync(NotificationPayload payload, IReadOnlyList<long> recipientUserIds, CancellationToken ct = default) =>
        Task.CompletedTask;
}

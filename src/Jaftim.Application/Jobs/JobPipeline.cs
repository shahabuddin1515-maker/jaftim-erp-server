using System.Diagnostics;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Tenancy;
using Jaftim.Domain.Entities.Tenancy;
using Microsoft.Extensions.Logging;

namespace Jaftim.Application.Jobs;

/// <summary>One execution of a background job, as seen by the job pipeline's steps.</summary>
public sealed class JobContext(string jobName, string? tenantCode)
{
    public string JobName { get; } = jobName;
    /// <summary>Null for global jobs (no tenant database is bound).</summary>
    public string? TenantCode { get; } = tenantCode;
    public Guid RunId { get; } = Guid.NewGuid();
    /// <summary>Set by <see cref="JobTenantBindingStep"/> once the tenant is bound.</summary>
    public int? TenantId { get; set; }
}

/// <summary>
/// Every background job body runs through here: <c>jobs.RunAsync(new JobContext(Id, tenantCode), ct =&gt; ..., ct)</c>.
/// Cross-cutting behaviour (log scope, timing, tenant binding) lives in <see cref="IPipelineStep{JobContext}"/> steps,
/// never in the job classes.
/// </summary>
public interface IJobRunner
{
    Task RunAsync(JobContext context, Func<CancellationToken, Task> work, CancellationToken ct);
}

public sealed class JobRunner(Pipeline<JobContext> pipeline) : IJobRunner
{
    public Task RunAsync(JobContext context, Func<CancellationToken, Task> work, CancellationToken ct) =>
        pipeline.RunAsync(context, c => work(c), ct);
}

/// <summary>Step 1: a log scope (job, tenant, run id) around the whole run, its duration, and any failure.</summary>
public sealed class JobLoggingStep(ILogger<JobLoggingStep> logger) : IPipelineStep<JobContext>
{
    public async Task InvokeAsync(JobContext context, PipelineStepDelegate next, CancellationToken ct)
    {
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["Job"] = context.JobName,
            ["Tenant"] = context.TenantCode,
            ["JobRunId"] = context.RunId,
        });
        long started = Stopwatch.GetTimestamp();
        try
        {
            await next(ct);
            logger.LogDebug("Job {Job} ({Tenant}) finished in {ElapsedMs} ms", context.JobName, context.TenantCode, Elapsed(started));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Job {Job} ({Tenant}) failed after {ElapsedMs} ms", context.JobName, context.TenantCode, Elapsed(started));
            throw;
        }
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}

/// <summary>
/// Step 2: binds the scoped tenant context to the job's tenant, so every tenant-bound repository in this run hits
/// that tenant's database. Unknown or inactive tenants fail the run. Global jobs (no tenant code) pass through.
/// </summary>
public sealed class JobTenantBindingStep(ITenantRepository tenants, ITenantContextSetter setter) : IPipelineStep<JobContext>
{
    public async Task InvokeAsync(JobContext context, PipelineStepDelegate next, CancellationToken ct)
    {
        if (context.TenantCode is { } code)
        {
            Tenant tenant = await tenants.GetByCodeAsync(code, ct)
                ?? throw new InvalidOperationException($"Tenant '{code}' is unknown.");
            if (!tenant.IsActive) throw new InvalidOperationException($"Tenant '{code}' is inactive.");
            setter.Set(tenant.TenantId, tenant.Code);
            context.TenantId = tenant.TenantId;
        }
        await next(ct);
    }
}

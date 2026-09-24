using System.Linq.Expressions;

namespace Jaftim.Application.Abstractions;

/// <summary>
/// Hands work to the background-job host (Hangfire in Jaftim.Jobs) without the Application layer knowing
/// Hangfire exists. Use it wherever the legacy app pushed to an Azure Storage Queue or fired-and-forgot a task.
/// </summary>
public interface IJobScheduler
{
    /// <summary>Run once, as soon as a worker is free. Returns the job id.</summary>
    string Enqueue<TJob>(Expression<Func<TJob, Task>> job, string queue = JobQueues.Default) where TJob : class;
    /// <summary>Run once after a delay.</summary>
    string Schedule<TJob>(Expression<Func<TJob, Task>> job, TimeSpan delay) where TJob : class;
}

/// <summary>Queue names known to both the API (producer) and Jobs (consumer). Keep in sync with Jaftim.Jobs.</summary>
public static class JobQueues
{
    public const string Critical = "critical";
    public const string Default = "default";
    public const string StockStatus = "stock-status";
    public const string LegacySync = "legacy-sync";
    public const string Integrations = "integrations";
}

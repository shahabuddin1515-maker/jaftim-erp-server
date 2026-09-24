using Hangfire;
using Hangfire.SqlServer;
using Jaftim.Application;
using Jaftim.Application.Abstractions;
using Jaftim.Infrastructure;
using Jaftim.Infrastructure.Services;
using Jaftim.Jobs;
using Jaftim.Jobs.Jobs;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// ---------- Layers (same Application + Infrastructure as the API; the actor is the configured system account) ----------
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<ICurrentUser, SystemUser>();
builder.Services.AddOptions<RespondIoOptions>().Bind(builder.Configuration.GetSection(RespondIoOptions.SectionName));
builder.Services.AddScoped<TenantScopeRunner>();
builder.Services.AddScoped<TenantJobsRegistrarJob>();

// ---------- Hangfire: SQL Server storage in its own database ----------
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("Hangfire"), new SqlServerStorageOptions
    {
        // Keep polling cost low - the business DB is small (20 DTU on Live) and the jobs DB should stay cheap too.
        QueuePollInterval = TimeSpan.FromSeconds(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true,
        PrepareSchemaIfNecessary = true,
    }));

builder.Services.AddHangfireServer(options =>
{
    options.ServerName = $"{Environment.MachineName}:{Environment.ProcessId}";
    options.Queues = JobsRegistry.Queues;
    options.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", Math.Max(4, Environment.ProcessorCount * 2));
});

builder.Services.AddHealthChecks();

var app = builder.Build();

string dashboardUser = app.Configuration["Hangfire:Dashboard:Username"] ?? "admin";
string dashboardPassword = app.Configuration["Hangfire:Dashboard:Password"]
    ?? throw new InvalidOperationException("Hangfire:Dashboard:Password must be configured.");

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    DashboardTitle = "Jaftim Jobs",
    Authorization = [new DashboardAuthorizationFilter(dashboardUser, dashboardPassword)],
    DisplayStorageConnectionString = false,
});
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/hangfire"));

using (IServiceScope scope = app.Services.CreateScope())
{
    // The registrar reconciles per-tenant schedules with the catalog now and every 5 minutes thereafter.
    JobsRegistry.RegisterGlobal(scope.ServiceProvider.GetRequiredService<IRecurringJobManager>());
    await scope.ServiceProvider.GetRequiredService<TenantJobsRegistrarJob>().RunAsync(CancellationToken.None);
}

app.Run();

using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Audit;
using Jaftim.Application.Modules.Auth;
using Jaftim.Application.Modules.Inquiries;
using Jaftim.Application.Modules.Lookups;
using Jaftim.Application.Modules.Navigation;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Application.Modules.Stock;
using Jaftim.Application.Modules.Tagging;
using Jaftim.Application.Modules.Tenancy;
using Jaftim.Application.Modules.Users;
using Jaftim.Infrastructure.Data;
using Jaftim.Infrastructure.Repositories;
using Jaftim.Infrastructure.Repositories.Catalog;
using Jaftim.Infrastructure.Security;
using Jaftim.Infrastructure.Services;
using Jaftim.Infrastructure.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jaftim.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers data access (catalog + tenant), repositories, security primitives and external services.
    /// Hosts must also register an <see cref="ICurrentUser"/> (the API binds it to the JWT; Jobs use
    /// <see cref="SystemUser"/>), may replace <see cref="IAuditRequestContext"/>, and, if they enqueue jobs, a
    /// Hangfire IBackgroundJobClient. The tenant context is scoped and settable (<see cref="ITenantContextSetter"/>).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMemoryCache();

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .PostConfigure(o => o.CatalogConnectionString ??= configuration.GetConnectionString("Catalog"));
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName));
        services.AddOptions<BlobStorageOptions>().Bind(configuration.GetSection(BlobStorageOptions.SectionName));
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.SectionName));
        services.AddOptions<SystemUserOptions>().Bind(configuration.GetSection(SystemUserOptions.SectionName));

        // Tenancy + data access. Catalog is a fixed connection; tenant connections come from the catalog.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantContextSetter>(sp => sp.GetRequiredService<TenantContext>());
        services.TryAddSingleton<ISecretResolver, PassThroughSecretResolver>();
        services.AddScoped<ITenantConnectionResolver, CachedTenantConnectionResolver>();
        services.AddSingleton<ICatalogConnectionFactory, CatalogConnectionFactory>();
        services.AddScoped<IDbConnectionFactory, TenantConnectionFactory>();
        services.AddScoped<ICatalogDbExecutor, CatalogDbExecutor>();
        services.AddScoped<IDbExecutor, DbExecutor>();
        services.AddSingleton<IDateTimeProvider, SystemClock>();

        // Catalog repositories
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IAuthRepository, AuthRepository>();
        services.AddScoped<IAccountSyncRepository, AccountSyncRepository>();

        // Tenant repositories - one per module, all backed by stored procedures through IDbExecutor.
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserRoleRepository, UserRoleRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IStockRepository, StockRepository>();
        services.AddScoped<IInquiryRepository, InquiryRepository>();
        services.AddScoped<ITaggingRepository, TaggingRepository>();
        services.AddScoped<IInquirySaveRepository, InquirySaveRepository>();
        services.AddScoped<ILookupRepository, LookupRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<INotificationSettingsRepository, NotificationSettingsRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<INavigationRepository, NavigationRepository>();

        // Security
        services.AddSingleton<IPasswordHasher, IdentityCompatiblePasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IPermissionService, CachedPermissionService>();

        // Audit (business writes): scoped writer captures context, hosted service flushes.
        services.AddSingleton(ChannelAuditWriter.CreateChannel());
        services.AddScoped<IAuditWriter, ChannelAuditWriter>();
        services.TryAddScoped<IAuditRequestContext, EmptyAuditRequestContext>();
        services.AddHostedService<AuditFlushService>();

        // External services
        services.AddSingleton<IBlobStorage, AzureBlobStorage>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddScoped<IJobScheduler, HangfireJobScheduler>();
        services.TryAddScoped<INotificationPusher, NoOpNotificationPusher>(); // the API replaces this with the SignalR pusher (last registration wins)

        return services;
    }
}

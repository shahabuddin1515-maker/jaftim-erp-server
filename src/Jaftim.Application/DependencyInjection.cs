using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Jobs;
using Jaftim.Application.Modules.Audit;
using Jaftim.Application.Modules.Email;
using Jaftim.Application.Modules.Auth;
using Jaftim.Application.Modules.Inquiries;
using Jaftim.Application.Modules.Lookups;
using Jaftim.Application.Modules.Navigation;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Application.Modules.Stock;
using Jaftim.Application.Modules.Tagging;
using Jaftim.Application.Modules.Tenancy;
using Jaftim.Application.Modules.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Jaftim.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers every application service and validator. Repositories and infrastructure abstractions are
    /// registered by Jaftim.Infrastructure.AddInfrastructure; hosts register ICurrentUser themselves.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>(includeInternalTypes: true);

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<IUserRoleService, UserRoleService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IUserRegionService, UserRegionService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IPermissionCatalogService, PermissionCatalogService>();
        services.AddScoped<INavigationService, NavigationService>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<IStockService, StockService>();
        services.AddScoped<IInquiryService, InquiryService>();
        services.AddScoped<ITaggingService, TaggingService>();
        services.AddScoped<IPartyService, PartyService>();
        services.AddScoped<IInquirySaveService, InquirySaveService>();
        services.AddScoped<ILookupService, LookupService>();
        services.AddScoped<INotificationInboxService, NotificationInboxService>();
        services.AddScoped<INotificationSettingsService, NotificationSettingsService>();

        // ---------- Pipelines (docs/ARCHITECTURE.md "Messaging and job pipelines") ----------
        // A pipeline's steps run in the order they are registered below. Add a step by registering it in place.
        services.AddScoped(typeof(Pipeline<>));

        // Notifications: outbox -> Persist (Notification_Create) -> Push (SignalR, best-effort)
        services.AddScoped<INotificationDispatcher, OutboxNotificationDispatcher>();
        services.AddScoped<IPipelineStep<NotificationDelivery>, PersistNotificationStep>();
        services.AddScoped<IPipelineStep<NotificationDelivery>, PushNotificationStep>();
        services.AddScoped<NotificationOutboxProcessor>();

        // Email (separate pipeline): outbox -> Guard (Suppress/Redirect/Send) -> Send (SMTP)
        services.AddScoped<IEmailDispatcher, EmailDispatcher>();
        services.AddScoped<IPipelineStep<EmailDelivery>, EmailGuardStep>();
        services.AddScoped<IPipelineStep<EmailDelivery>, SendEmailStep>();
        services.AddScoped<EmailOutboxProcessor>();

        // Background jobs: every job body runs through IJobRunner -> Logging scope -> Tenant binding -> body
        services.AddScoped<IJobRunner, JobRunner>();
        services.AddScoped<IPipelineStep<JobContext>, JobLoggingStep>();
        services.AddScoped<IPipelineStep<JobContext>, JobTenantBindingStep>();

        return services;
    }
}

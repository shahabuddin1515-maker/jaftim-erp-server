using FluentValidation;
using Jaftim.Application.Modules.Audit;
using Jaftim.Application.Modules.Auth;
using Jaftim.Application.Modules.Inquiries;
using Jaftim.Application.Modules.Lookups;
using Jaftim.Application.Modules.Navigation;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Application.Modules.Stock;
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
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IPermissionCatalogService, PermissionCatalogService>();
        services.AddScoped<INavigationService, NavigationService>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<IStockService, StockService>();
        services.AddScoped<IInquiryService, InquiryService>();
        services.AddScoped<IPartyService, PartyService>();
        services.AddScoped<IInquirySaveService, InquirySaveService>();
        services.AddScoped<ILookupService, LookupService>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddScoped<INotificationInboxService, NotificationInboxService>();
        services.AddScoped<INotificationSettingsService, NotificationSettingsService>();

        return services;
    }
}

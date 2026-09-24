using System.Text.Json.Serialization;
using FluentValidation;
using System.Threading.RateLimiting;
using Hangfire;
using Jaftim.Api.Hubs;
using Jaftim.Api.Middleware;
using Jaftim.Api.OpenApi;
using Jaftim.Api.Security;
using Jaftim.Application;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Auth;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Infrastructure;
using Jaftim.Infrastructure.Security;
using Jaftim.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------- Logging ----------
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// ---------- Layers ----------
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddValidatorsFromAssemblyContaining<Program>(); // validators declared next to API-only DTOs
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<IAuditRequestContext, HttpAuditRequestContext>();

// Hangfire client only (no server here): lets the API enqueue jobs that Jaftim.Jobs executes.
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("Hangfire")));

// ---------- Authentication: JWT bearer ----------
AuthOptions authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddSingleton<TokenVersionValidator>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep our short claim names (uid, rid, ...) exactly as issued
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = authOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = JwtTokenService.SigningKey(authOptions),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JaftimClaims.Email,
            RoleClaimType = JaftimClaims.RoleName,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx => ctx.HttpContext.RequestServices.GetRequiredService<TokenVersionValidator>().ValidateAsync(ctx),
            // SignalR (browser WebSocket) cannot send an Authorization header; it passes the token as ?access_token=.
            OnMessageReceived = ctx =>
            {
                string? token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments(NotificationHub.Path))
                    ctx.Token = token;
                return Task.CompletedTask;
            },
        };
    });

// ---------- Authorization: permission policies (deny by default) ----------
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// ---------- MVC / API ----------
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddRouting(o => o.LowercaseUrls = true); // documented paths are lowercase (/api/stocks, not /api/Stocks)
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddJaftimSwagger(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddSqlServer(builder.Configuration.GetConnectionString("Catalog") ?? string.Empty, name: "catalog-sql");

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

string[] corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// ---------- Realtime notifications ----------
builder.Services.AddSignalR();
builder.Services.AddScoped<INotificationPusher, SignalRNotificationPusher>();

// ---------- Cross-cutting middleware services ----------
builder.Services.AddOptions<IpAllowlistOptions>().Bind(builder.Configuration.GetSection(IpAllowlistOptions.SectionName));
builder.Services.AddSingleton(RequestAuditWriter.CreateChannel());
builder.Services.AddHostedService<RequestAuditWriter>();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Swagger UI at /swagger and Scalar at /scalar, both reading /swagger/v1/swagger.json (OpenApi:Enabled, default Development).
app.UseJaftimSwagger();
if (app.Configuration.GetValue<bool?>("OpenApi:Enabled") ?? app.Environment.IsDevelopment())
    app.MapScalarApiReference(o => o.WithTitle("Jaftim API").WithTheme(ScalarTheme.Kepler).WithOpenApiRoutePattern(SwaggerSetup.DocumentRoute)).AllowAnonymous();

app.UseHttpsRedirection();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<IpAllowlistMiddleware>();
app.UseAuthorization();
app.UseMiddleware<RequestAuditMiddleware>();

app.MapControllers();
app.MapHub<NotificationHub>(NotificationHub.Path);
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();

/// <summary>Exposed for WebApplicationFactory in Jaftim.Api.Tests.</summary>
public partial class Program;

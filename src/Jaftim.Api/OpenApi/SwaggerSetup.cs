using System.Reflection;
using Jaftim.Api.Security;
using Jaftim.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Jaftim.Api.OpenApi;

/// <summary>Bound from "OpenApi". The document and UIs are served only when Enabled (default: Development only).</summary>
public sealed class OpenApiOptions
{
    public const string SectionName = "OpenApi";
    public bool? Enabled { get; set; }
    public string Title { get; set; } = "Jaftim API";
    public string Version { get; set; } = "v1";
}

public static class SwaggerSetup
{
    public const string DocumentName = "v1";
    public const string DocumentRoute = "/swagger/v1/swagger.json";
    private const string BearerScheme = "Bearer";

    public static IServiceCollection AddJaftimSwagger(this IServiceCollection services, IConfiguration configuration)
    {
        OpenApiOptions options = configuration.GetSection(OpenApiOptions.SectionName).Get<OpenApiOptions>() ?? new OpenApiOptions();
        services.AddOptions<OpenApiOptions>().Bind(configuration.GetSection(OpenApiOptions.SectionName));
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(gen =>
        {
            gen.SwaggerDoc(DocumentName, new OpenApiInfo
            {
                Title = options.Title,
                Version = options.Version,
                Description =
                    "Backend of the Jaftim ERP (v2). Every business endpoint requires a JWT (POST /api/auth/login) and a " +
                    "RoleAction permission; the required ActionId is listed on each operation. Errors are RFC 7807 " +
                    "ProblemDetails; successes are wrapped in { success, data, message }. One database per tenant - the " +
                    "token's `tid` claim selects it.",
            });

            // XML comments from every layer: controller summaries, DTO/entity property descriptions.
            foreach (string xml in Directory.EnumerateFiles(AppContext.BaseDirectory, "Jaftim.*.xml"))
                gen.IncludeXmlComments(xml, includeControllerXmlComments: true);

            gen.AddSecurityDefinition(BearerScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Name = "Authorization",
                Description = "Paste the accessToken returned by POST /api/auth/login (without the 'Bearer ' prefix).",
            });

            gen.OperationFilter<AuthorizationResponsesOperationFilter>();
            gen.OperationFilter<PermissionDescriptionOperationFilter>();
            gen.SchemaFilter<EnumDescriptionSchemaFilter>();

            gen.DocumentFilter<EnsureProblemDetailsSchemaFilter>();
            // Jaftim types keep their (shortened) namespace so same-named DTOs in different modules cannot collide;
            // framework types use the bare name (the shared error responses reference "ProblemDetails" by id).
            gen.CustomSchemaIds(t => t.FullName!.StartsWith("Jaftim.", StringComparison.Ordinal)
                ? t.FullName!.Replace("Jaftim.", string.Empty).Replace('+', '.')
                : t.Name);
            gen.SupportNonNullableReferenceTypes();
            gen.UseAllOfToExtendReferenceSchemas();
            gen.OrderActionsBy(d => $"{d.ActionDescriptor.RouteValues["controller"]}_{d.RelativePath}");
        });
        return services;
    }

    /// <summary>Serves the JSON document, Swagger UI (/swagger) and Scalar (/scalar) from the same generator.</summary>
    public static WebApplication UseJaftimSwagger(this WebApplication app)
    {
        OpenApiOptions options = app.Configuration.GetSection(OpenApiOptions.SectionName).Get<OpenApiOptions>() ?? new OpenApiOptions();
        if (!(options.Enabled ?? app.Environment.IsDevelopment()))
            return app;

        app.UseSwagger();
        app.UseSwaggerUI(ui =>
        {
            ui.SwaggerEndpoint(DocumentRoute, $"{options.Title} {options.Version}");
            ui.DocumentTitle = options.Title;
            ui.DisplayRequestDuration();
            ui.EnablePersistAuthorization();   // keeps the pasted token across page reloads
            ui.DefaultModelsExpandDepth(1);
        });
        return app;
    }

    /// <summary>
    /// Adds the responses every endpoint can produce but nobody wants to annotate by hand: 401/403 for protected
    /// operations, 400 (validation), 422 (business rule / SP RAISERROR), 500; and the Bearer requirement.
    /// </summary>
    private sealed class AuthorizationResponsesOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            bool anonymous = context.MethodInfo.GetCustomAttributes(true).OfType<AllowAnonymousAttribute>().Any()
                || context.MethodInfo.DeclaringType?.GetCustomAttributes(true).OfType<AllowAnonymousAttribute>().Any() == true;

            AddProblem(operation, context.Document, "400", "Validation failed (`errors` lists the fields).");
            AddProblem(operation, context.Document, "422", "A business rule rejected the request (message from the service or stored procedure).");
            AddProblem(operation, context.Document, "500", "Unexpected error.");

            if (anonymous) return;

            AddProblem(operation, context.Document, "401", "Missing, expired or revoked token.");
            AddProblem(operation, context.Document, "403", "Authenticated, but the caller's effective roles lack the required permission (or the network is not allowed).");
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [],
            });
        }

        private static void AddProblem(OpenApiOperation operation, OpenApiDocument document, string status, string description)
        {
            operation.Responses ??= [];
            if (operation.Responses.ContainsKey(status)) return;
            operation.Responses[status] = new OpenApiResponse
            {
                Description = description,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/problem+json"] = new OpenApiMediaType { Schema = new OpenApiSchemaReference(nameof(ProblemDetails), document) },
                },
            };
        }
    }

    /// <summary>Appends "Requires permission: Stock Listing (417)" to every [HasPermission] operation.</summary>
    private sealed class PermissionDescriptionOperationFilter : IOperationFilter
    {
        private static readonly Dictionary<int, string> Names = typeof(Permissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .ToDictionary(f => (int)f.GetRawConstantValue()!, f => f.Name);

        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            IEnumerable<HasPermissionAttribute> attributes = context.MethodInfo.GetCustomAttributes<HasPermissionAttribute>(true)
                .Concat(context.MethodInfo.DeclaringType?.GetCustomAttributes<HasPermissionAttribute>(true) ?? []);

            string[] labels = attributes
                .Select(a => int.Parse(a.Policy![HasPermissionAttribute.PolicyPrefix.Length..]))
                .Distinct()
                .Select(id => $"`{(Names.TryGetValue(id, out string? n) ? n : "?")}` ({id})")
                .ToArray();
            if (labels.Length == 0) return;

            string note = $"**Requires permission:** {string.Join(", ", labels)} - a `RoleAction.ActionId` held by one of the caller's effective roles.";
            operation.Description = string.IsNullOrEmpty(operation.Description) ? note : $"{operation.Description}\n\n{note}";
        }
    }

    /// <summary>Guarantees a ProblemDetails component exists for the shared error responses even if no action declares it.</summary>
    private sealed class EnsureProblemDetailsSchemaFilter : IDocumentFilter
    {
        public void Apply(OpenApiDocument document, DocumentFilterContext context) =>
            context.SchemaGenerator.GenerateSchema(typeof(ProblemDetails), context.SchemaRepository);
    }

    /// <summary>Lists the enum members (they are serialised as strings) in the schema description.</summary>
    private sealed class EnumDescriptionSchemaFilter : ISchemaFilter
    {
        public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
        {
            if (!context.Type.IsEnum || schema is not OpenApiSchema concrete) return;
            concrete.Description = $"{schema.Description} One of: {string.Join(", ", Enum.GetNames(context.Type))}.".Trim();
        }
    }
}

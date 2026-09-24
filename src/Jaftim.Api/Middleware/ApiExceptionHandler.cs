using Jaftim.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace Jaftim.Api.Middleware;

/// <summary>
/// Maps exceptions to RFC 7807 ProblemDetails. Replaces the legacy pattern of catch { return null; } / FaultResponse
/// strings: business rules raised by the stored procedures (RAISERROR/THROW with severity 16, error number >= 50000)
/// surface as 422 with the SQL message, everything else is a logged 500 without internals.
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger, IHostEnvironment environment) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        (int status, string title, IDictionary<string, object?>? extensions) = exception switch
        {
            ValidationException v => (StatusCodes.Status400BadRequest, v.Message, new Dictionary<string, object?> { ["errors"] = v.Errors }),
            NotFoundException n => (StatusCodes.Status404NotFound, n.Message, null),
            ForbiddenException f => (context.User.Identity?.IsAuthenticated == true ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized, f.Message, null),
            ConflictException c => (StatusCodes.Status409Conflict, c.Message, null),
            Jaftim.Application.Modules.Auth.TenantSelectionRequiredException t => (StatusCodes.Status409Conflict, t.Message,
                new Dictionary<string, object?> { ["code"] = "TenantSelectionRequired", ["memberships"] = t.Memberships }),
            BusinessRuleException b => (StatusCodes.Status422UnprocessableEntity, b.Message, null),
            SqlException { Number: >= 50000 } sql => (StatusCodes.Status422UnprocessableEntity, sql.Message, null),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested => (499, "Request cancelled.", null),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", null),
        };

        if (status >= 500)
            logger.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        else
            logger.LogInformation("Request {Method} {Path} failed with {Status}: {Message}", context.Request.Method, context.Request.Path, status, exception.Message);

        context.Response.StatusCode = status;
        var details = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status >= 500 && !environment.IsDevelopment() ? null : exception.Message,
            Instance = context.Request.Path,
        };
        if (extensions is not null)
            foreach ((string key, object? value) in extensions) details.Extensions[key] = value;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = details, Exception = exception });
    }
}

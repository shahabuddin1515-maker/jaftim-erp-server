using Jaftim.Api.Contracts;
using Jaftim.Application.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>
/// Every controller derives from this. [Authorize] is the default (deny unless authenticated); endpoints additionally
/// carry [HasPermission(...)]. Public endpoints must opt out explicitly with [AllowAnonymous].
/// </summary>
[ApiController]
[Authorize]
[Route("api/[controller]")]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected ActionResult<ApiResponse<T>> Ok<T>(T data, string? message = null) => base.Ok(ApiResponse.Ok(data, message));

    protected ActionResult<ApiResponse<object?>> Ok(string? message = null) => base.Ok(ApiResponse.Ok(message));

    protected ClientInfo ClientInfo => new(
        Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() ?? HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString());
}

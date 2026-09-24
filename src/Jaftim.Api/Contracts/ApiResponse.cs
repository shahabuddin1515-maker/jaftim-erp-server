namespace Jaftim.Api.Contracts;

/// <summary>
/// The single response envelope every endpoint returns (replaces the legacy Response { StatusCode, Result,
/// Pagination, message }). Errors use RFC 7807 ProblemDetails instead of this envelope - see ApiExceptionHandler.
/// </summary>
public sealed record ApiResponse<T>(T Data, string? Message = null)
{
    public bool Success => true;
}

public static class ApiResponse
{
    public static ApiResponse<T> Ok<T>(T data, string? message = null) => new(data, message);
    public static ApiResponse<object?> Ok(string? message = null) => new(null, message);
}

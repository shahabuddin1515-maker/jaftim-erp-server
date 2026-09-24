namespace Jaftim.Domain.Exceptions;

/// <summary>Base for every exception the API translates into a non-500 HTTP response.</summary>
public abstract class AppException(string message) : Exception(message);

/// <summary>404.</summary>
public sealed class NotFoundException(string entity, object key)
    : AppException($"{entity} [{key}] was not found.");

/// <summary>422 - a business rule rejected the request (mirrors RAISERROR/THROW in the SPs).</summary>
public sealed class BusinessRuleException(string message) : AppException(message);

/// <summary>403 - authenticated but not permitted.</summary>
public sealed class ForbiddenException(string message = "You do not have permission to perform this action.")
    : AppException(message);

/// <summary>409.</summary>
public sealed class ConflictException(string message) : AppException(message);

/// <summary>400 - input validation (FluentValidation failures are mapped to this).</summary>
public sealed class ValidationException(IReadOnlyDictionary<string, string[]> errors)
    : AppException("One or more validation errors occurred.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

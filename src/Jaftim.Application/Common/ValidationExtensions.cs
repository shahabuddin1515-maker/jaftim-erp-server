using FluentValidation;
using FluentValidation.Results;

namespace Jaftim.Application.Common;

public static class ValidationExtensions
{
    /// <summary>Runs the validator and throws the Domain ValidationException (mapped to HTTP 400 by the API).</summary>
    public static async Task ValidateAndThrowAppAsync<T>(this IValidator<T> validator, T instance, CancellationToken ct = default)
    {
        ValidationResult result = await validator.ValidateAsync(instance, ct);
        if (result.IsValid) return;
        throw new Domain.Exceptions.ValidationException(result.ToErrorDictionary());
    }

    public static IReadOnlyDictionary<string, string[]> ToErrorDictionary(this ValidationResult result) =>
        result.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
}

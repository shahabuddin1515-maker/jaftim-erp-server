namespace Jaftim.Application.Abstractions;

/// <summary>The legacy DBRepository injected DateTime.UtcNow as @CreatedAt; keep that behaviour, but make it mockable.</summary>
public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}

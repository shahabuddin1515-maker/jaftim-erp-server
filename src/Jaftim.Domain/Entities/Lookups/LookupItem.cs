namespace Jaftim.Domain.Entities.Lookups;

/// <summary>
/// Generic Id/Value pair, the shape GetDllAuthTableValues, GetWhitelistedIPs and the SYS_Auth_* procedures return.
/// A mutable class (not a positional record) so Dapper maps whichever subset of columns a procedure emits.
/// </summary>
public sealed class LookupItem
{
    public long Id { get; set; }
    public string Value { get; set; } = string.Empty;
    public string? TableName { get; set; }
    public long? ParentId { get; set; }
}

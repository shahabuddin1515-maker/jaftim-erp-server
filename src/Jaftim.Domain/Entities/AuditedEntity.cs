namespace Jaftim.Domain.Entities;

/// <summary>
/// The audit columns nearly every table carries. IsDeleted is <c>long?</c> on purpose: the legacy schema
/// stores it as bigint/bit/tinyint depending on the table and uses negative sentinels as reason codes in
/// Stock_StatusDetail. Treat "not deleted" as <c>(IsDeleted ?? 0) == 0</c>, never <c>IsDeleted == false</c>.
/// </summary>
public abstract class AuditedEntity
{
    public long? CreatedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
    public long? ModifiedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public long? IsDeleted { get; set; }
    public long? CompanyId { get; set; }

    public bool IsActiveRow => (IsDeleted ?? 0) == 0;
}

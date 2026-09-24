namespace Jaftim.Domain.Entities.Stock;

/// <summary>
/// Row shape returned by StockGetAll_New (list) and StockGetById (detail header). Property names equal the column
/// aliases the procedures emit so Dapper maps by convention; columns a procedure does not return stay null.
/// </summary>
public sealed class StockListItem
{
    public long StockId { get; set; }
    public string? StockCode { get; set; }
    public int? StockTypeId { get; set; }
    public string? StockType { get; set; }
    public string? StockRemarks { get; set; }
    public string? PromotionalNotes { get; set; }
    public decimal? BossPrice { get; set; }
    public string? Supplier { get; set; }
    public string? OriginCountry { get; set; }
    public string? Country { get; set; }
    public string? PurchaseCountry { get; set; }
    public string? City { get; set; }
    public string? DepositRatio { get; set; }
    public string? YardName { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public long? MakeId { get; set; }
    public long? ModelId { get; set; }
    public string? Customer { get; set; }
    public long? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? SalePerson { get; set; }
    public string? Agent { get; set; }
    public string? ImageThumbnailUrl { get; set; }
    public string? ImageUrl { get; set; }
    public DateTime? ManufactureMonthYear { get; set; }
    public string? RegisterMonthYear { get; set; }
    public string? Stock_SP_RegisterMonth { get; set; }
    public string? Stock_SP_RegisterYear { get; set; }
    public string? BodyType { get; set; }
    public string? EngineCC { get; set; }
    public string? Chassis { get; set; }
    public string? AvailabiltyStatus { get; set; }
    public string? DisplayPrice { get; set; }
    public decimal? FOBPrice { get; set; }
    public string? Mileage { get; set; }
    public string? WD { get; set; }
    public string? Color { get; set; }
    public string? Steering { get; set; }
    public string? Fuel { get; set; }
    public string? Seats { get; set; }
    public string? Doors { get; set; }
    public string? Transmission { get; set; }
    public decimal? Stock_R_SoldPrice { get; set; }
    public string? DischargePort { get; set; }
    public string? ShippmentType { get; set; }
    public string? BLNumber { get; set; }
    public string? ReserveStatus { get; set; }
    public decimal? CNFPostRepairModification { get; set; }
    /// <summary>fn_GetStockStatusJsonArray output - the check flags as a JSON array string.</summary>
    public string? StockStatusJsonArray { get; set; }
    public string? Overdue { get; set; }
    public string? Balance { get; set; }
    public string? CurrencyName { get; set; }
    public int? Stock_C_J_Status { get; set; }
    public int? SoldCheck { get; set; }
    public DateTime? EstimatedDepartureDate { get; set; }
    public DateTime? EstimatedArrivalDate { get; set; }
    public DateTime? LastModified { get; set; }
}

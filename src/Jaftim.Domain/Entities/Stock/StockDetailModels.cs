namespace Jaftim.Domain.Entities.Stock;

/// <summary>Stock_GetSectionById row: a key/value pair grouped by section (the detail tabs render these generically).</summary>
public sealed class StockSectionValue
{
    public string? KeyName { get; set; }
    public string? KeyValue { get; set; }
    public string? SectionName { get; set; }
}

/// <summary>StockDetailWorkflowGetById row - release-criteria flags and who owns the stock commercially.</summary>
public sealed class StockWorkflow
{
    public bool JobNoMet { get; set; }
    public bool RequiredDocumentsMet { get; set; }
    public bool InspectionDocumentMet { get; set; }
    public bool CourierDetailsMet { get; set; }
    public bool TrackingMet { get; set; }
    public long? AgentUserId { get; set; }
    public long? ManagerUserId { get; set; }
    public bool ReleaseOk { get; set; }
    public bool HasPendingShippingRequest { get; set; }
}

/// <summary>GetStockFlagsByStockId row (Stock_Info x Stock_InfoTypes).</summary>
public sealed class StockFlag
{
    public long StockId { get; set; }
    public int InfoTypeId { get; set; }
    public string? FlagName { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>Stock_GetStatusDetail row - current value of each check flag (Base_StockStatus x Stock_StatusDetail).</summary>
public sealed class StockStatusDetail
{
    public long Base_StockStatus_Id { get; set; }
    public string? Base_StockStatus_Name { get; set; }
    public int Base_StockStatus_Type { get; set; }
    public int? OrderNo { get; set; }
    public long? Stock_SD_StockStatus_Value { get; set; }
    public string? Stock_SD_StockStatus_Reason { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>Stock_GetStatusDetailHistory row.</summary>
public sealed class StockStatusHistoryItem
{
    public int Base_StockStatus_Id { get; set; }
    public string? Base_StockStatus_Name { get; set; }
    public string? Base_StockStatus_Type { get; set; }
    public string? Stock_SD_StockStatus_Value { get; set; }
    public string? Stock_SD_StockStatus_Value_Old { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>Stock_FinancialGetAllByStockId row.</summary>
public sealed class StockFinancialLine
{
    public long StockId { get; set; }
    public long SNO { get; set; }
    public double Amount { get; set; }
    public string? Remarks { get; set; }
    public int StockFinancialTypeId { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public string? SectionName { get; set; }
}

/// <summary>GetStockInternalNote row.</summary>
public sealed class StockInternalNote
{
    public long Stock_I_N_Id { get; set; }
    public long Stock_Id { get; set; }
    public string? InternalNote { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>StockFolderGetByStockId / StockDocumentsGetByStockId / StockImagesGetByStockId row.</summary>
public sealed class StockFolder
{
    public long Stock_Folder_Id { get; set; }
    public long Stock_Id { get; set; }
    public string? Stock_Folder_Name { get; set; }
    public long Stock_Folder_Parent_Id { get; set; }
    public int Stock_System_Id { get; set; }
}

/// <summary>StockFileGetByFolderId row.</summary>
public sealed class StockFile
{
    public long Stock_File_Id { get; set; }
    public long Stock_FK_Folder_Id { get; set; }
    public long Stock_Id { get; set; }
    public string? Stock_File_Name { get; set; }
    public string? Stock_File_URL { get; set; }
    public string? Stock_File_Extension { get; set; }
    public long Stock_File_Size { get; set; }
    public string? Stock_File_Icon { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>StockImageGetByStockId row - the stock profile image.</summary>
public sealed class StockImage
{
    public long StockImageId { get; set; }
    public long StockId { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImageThumbnailUrl { get; set; }
}

/// <summary>GetStockVideoByStockId row.</summary>
public sealed class StockVideo
{
    public long Stock_Video_Id { get; set; }
    public long Stock_Id { get; set; }
    public string? Stock_Video_Url { get; set; }
}

/// <summary>Stock_GetReservationBids row (README section 8.2 item 5 explains isCheck/IsDiscounted/DoHighlight/BidWaitingApproval).</summary>
public sealed class StockReservationBid
{
    public long Stock_Id { get; set; }
    public long Stock_RB_D_Id { get; set; }
    public string? SalesPerson { get; set; }
    public string? CustomerName { get; set; }
    public long? Stock_RB_D_CustomerId { get; set; }
    public string? DepositRatio { get; set; }
    public decimal? DisplayPrice { get; set; }
    public decimal? FOBPrice { get; set; }
    public decimal? BossPrice { get; set; }
    public string? DestinationCountry { get; set; }
    public string? PortOfDischarge { get; set; }
    public string? FreightType { get; set; }
    public decimal? FreightPrice { get; set; }
    public string? ShipmentType { get; set; }
    public string? ContainerSize { get; set; }
    public decimal? Vanning { get; set; }
    public string? SpecialInstruction { get; set; }
    public decimal? Extras { get; set; }
    public decimal? Inspection { get; set; }
    public decimal? Insurance { get; set; }
    public decimal? Modifications { get; set; }
    public decimal? OtherModifications { get; set; }
    public decimal? Discount { get; set; }
    public long IsDiscounted { get; set; }
    public long BidWaitingApproval { get; set; }
    public long DoHighlight { get; set; }
    public decimal? CNF { get; set; }
    public decimal? CNFPostRepairModification_AED { get; set; }
    public decimal? CNFPostRepairModification_USD { get; set; }
    public string? Currency { get; set; }
    public long? CreatedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? JSON_ExtraData { get; set; }
    public long? isCheck { get; set; }
}

// ----- History tabs (README section 8.5: trigger-fed _History shadow tables) -----

public sealed class StockReservationHistoryItem
{
    public string? SalesPerson { get; set; }
    public string? CustomerName { get; set; }
    public string? ReserveStatus { get; set; }
    public decimal? FOBPrice { get; set; }
    public string? Country { get; set; }
    public string? DischargePort { get; set; }
    public string? ShipmentType { get; set; }
    public string? ContainerType { get; set; }
    public decimal? VanningAmount { get; set; }
    public decimal? FreightAmount { get; set; }
    public decimal? ExtrasAmount { get; set; }
    public string? SpecialInstruction { get; set; }
    public string? Freight { get; set; }
    public string? Currency { get; set; }
    public string? AssociateCustomer { get; set; }
    public string? ActivityBy { get; set; }
    public DateTime? ActivityDate { get; set; }
}

public sealed class StockFinancialHistoryItem
{
    public string? CustomerName { get; set; }
    public string? SubType { get; set; }
    public string? Category { get; set; }
    public decimal? Amount { get; set; }
    public string? ActivityBy { get; set; }
    public string? Type { get; set; }
    public DateTime? ActivityDate { get; set; }
}

public sealed class StockInspectionHistoryItem
{
    public string? InspectionBooked { get; set; }
    public DateTime? InspectionDate { get; set; }
    public string? InspectionResult { get; set; }
    public string? Reason { get; set; }
    public string? InspectionCertificate { get; set; }
    public DateTime? CertificateDate { get; set; }
    public DateTime? ActivityDate { get; set; }
    public string? ActivityBy { get; set; }
    public string? Customer { get; set; }
}

public sealed class StockPortLeaveHistoryItem
{
    public DateTime? PortLeaveDate { get; set; }
    public string? PortName { get; set; }
    public string? ActivityBy { get; set; }
    public DateTime? ActivityDate { get; set; }
}

public sealed class StockBlHistoryItem
{
    public string? BLNO { get; set; }
    public DateTime? BLDate { get; set; }
    public DateTime? ETA { get; set; }
    public DateTime? ETD { get; set; }
    public string? ActivityBy { get; set; }
    public DateTime? ActivityDate { get; set; }
}

namespace Jaftim.Domain.Enums;

/// <summary>Base_CustomerStatus [live-verified].</summary>
public enum CustomerStatus
{
    Transactional = 1,
    Pipeline = 2,
    NonActive = 3,
    Junk = 4,
}

/// <summary>Base_Source ids used by the ingestion pipelines (InsertLead, PublicController, Respond.io).</summary>
public static class SourceIds
{
    public const int Manual = 1;
    public const int Email = 2;
    public const int WhatsApp = 3;
    public const int Other = 5;
    public const int PublicWebsite = 6;
    public const int RespondIo = 20;
}

/// <summary>Base_RemarkSource.</summary>
public enum RemarkSource
{
    Inquiry = 1,
    CustomerRemarks = 2,
}

/// <summary>Stock_Adjustment.AdjustmentType.</summary>
public enum AdjustmentType
{
    Adjustment = 1,
    Waiver = 2,
}

namespace Jaftim.Domain.Enums;

/// <summary>Base_StockStatus.Base_StockStatus_Id - the check flags stored in Stock_StatusDetail [live-verified].</summary>
public enum StockCheck
{
    Availability = 1,
    Reservation = 2,
    DepositOk = 3,
    Paid = 4,
    ShipOk = 5,
    ReleaseOk = 6,
    Release = 7,
    PortLeave = 8,
    Booked = 9,
    SureCheck = 10,
    SoldCheck = 11,
    Inspection = 12,
    ManualShipOk = 13,
    InYard = 14,
    Purchased = 15,
    CustomerOrder = 16,
}

/// <summary>Stock.Stock_C_J_Status - the journey status (Stock_J_Status lookup).</summary>
public enum StockJourneyStatus
{
    Purchased = 0,
    PreDeparture = 1,
    InTransit = 2,
    InGarage = 3,
    ReadyForSale = 4,
    AvailableForSale = 5,
    Sold = 6,
    NotSold = 7,
    Archive = 8,
    PostSaleGarage = 9,
}

/// <summary>StockType.Id [live-verified].</summary>
public enum StockType
{
    Jaftim = 1,
    Dealer = 2,
    CustomerOrder = 3,
    DummyStock = 4,
}

/// <summary>Base_ReservationStatus values written to Stock_StatusDetail for StockCheck.Reservation.</summary>
public enum ReservationStatus
{
    Reserve = 1,
    ReserveCancel = 2,
}

/// <summary>ShipmentParty.ShipmentParty_Type.</summary>
public enum ShipmentPartyType
{
    Booking = 1,
    Consignee = 2,
    NotifyParty = 3,
}

/// <summary>Stock_Financial.Stock_F_FinancialTypeId values observed in live flows (Base_FinancialType).</summary>
public enum FinancialType
{
    AdjustmentCredit = 5,
    Inspection = 9,
    Insurance = 10,
    Modification = 12,
    OtherModification = 13,
    Waiver = 18,
}

/// <summary>Stock_InfoTypes [live-verified].</summary>
public enum StockInfoType
{
    Modified = 1,
    Featured = 2,
    SpecialOffer = 3,
    Discounted = 4,
    OpenForReserveBid = 5,
    WebDisplay = 6,
    ComingSoon = 7,
}

namespace Jaftim.Domain.Security;

/// <summary>
/// Frontend permission keys -> RoleAction.ActionId. The UI gates screens and buttons by these stable names and
/// resolves them against the ActionIds in /api/auth/me. A null value means the UI concept has no RoleAction yet
/// (the frontend decides how to treat it). Several keys may share one ActionId (e.g. bid create/accept are both 404).
/// Values reference <see cref="Permissions"/> so a regenerated catalog that drops an id fails the build.
/// Served anonymously by GET /api/permissions/keys - it is static and holds no tenant or user data.
/// </summary>
public static class PermissionKeys
{
    /// <summary>Insertion order is the response order.</summary>
    public static IReadOnlyDictionary<string, int?> All { get; } = new Dictionary<string, int?>
    {
        ["OVERVIEW_VIEW"] = null,
        ["TASKS_VIEW"] = Permissions.MyTask,
        ["NOTIFICATIONS_VIEW"] = null,

        ["STOCK_READ"] = Permissions.StockListing,
        ["STOCK_DETAIL"] = Permissions.StockDetail,
        ["STOCK_CREATE"] = Permissions.StockSave,
        ["STOCK_UPDATE"] = Permissions.StockEdit,
        ["STOCK_DELETE"] = null,
        ["STOCK_ACTIVITY"] = Permissions.StockActivity,

        ["RESERVATIONS_READ"] = Permissions.StockTabReservation,
        ["RESERVATION_BIDS_READ"] = Permissions.StockTabReservationBids,
        ["RESERVATION_BID_CREATE"] = Permissions.StockReservation,
        ["RESERVATION_BID_EDIT"] = Permissions.ReservationEdit,
        ["RESERVATION_BID_ACCEPT"] = Permissions.StockReservation,

        ["CUSTOMER_READ"] = Permissions.CustomerListing,
        ["CUSTOMER_DETAIL"] = Permissions.CustomerDetail,
        ["CUSTOMER_CREATE"] = Permissions.CustomerCreate,
        ["CUSTOMER_UPDATE"] = Permissions.CustomerEdit,
        ["CUSTOMER_ALLOCATION"] = Permissions.CustomerStockAllocation,
        ["CUSTOMER_TAGGING"] = Permissions.CustomerTagging,
        ["CUSTOMER_STATEMENT"] = Permissions.CustomerStatement,
        ["CUSTOMER_CONSIGNEE"] = Permissions.CustomerTabConsignee,
        ["CUSTOMER_NOTIFY_PARTY"] = Permissions.CustomerTabNotifyParty,

        ["INQUIRY_READ"] = Permissions.InquiryListing,
        ["INQUIRY_CREATE"] = Permissions.InquirySave,
        ["INQUIRY_CONTACT_UPDATE"] = Permissions.InquiryContactEditSave,

        ["SALES_READ"] = Permissions.Sales,
        ["SALES_DISCOUNT_READ"] = Permissions.MyTaskDiscountRequests,
        ["SALES_DISCOUNT_APPROVE"] = null,

        ["PRICING_READ"] = Permissions.PricingModule,
        ["PRICING_UPDATE"] = Permissions.StockPrice,

        ["FINANCE_READ"] = Permissions.Finance,
        ["REMITTANCE_READ"] = Permissions.MyTaskRemittances,
        ["RECONCILIATION_READ"] = Permissions.IncomeReconcilationListing,
        ["REFUND_READ"] = Permissions.MyTaskRefunds,
        ["REFUND_APPROVE"] = null,
        ["WALLET_CONVERSIONS_READ"] = Permissions.CustomerWalletConversions,
        ["WALLET_TRANSFERS_READ"] = Permissions.MyTaskWalletTransfers,
        ["ADJUSTMENTS_READ"] = Permissions.MyTaskAdjustmentsWaivers,

        ["SHIPPING_READ"] = Permissions.ShippingModule,
        ["SHIPPING_UPDATE"] = null,

        ["DOCUMENTS_READ"] = Permissions.DocumentModule,
        ["DOCUMENT_TRACKING"] = Permissions.DocumentDHLTracking,
        ["INSPECTIONS_READ"] = Permissions.InspectionModule,

        ["VENDORS_READ"] = Permissions.VendorModule,
        ["VENDOR_CREATE"] = Permissions.VendorCreateVendor,
        ["VENDOR_JOB_CREATE"] = Permissions.VendorCreateJob,
        ["VENDOR_SETTLE"] = Permissions.VendorSettlePayment,

        ["TRANSPORT_READ"] = Permissions.TransportQuotations,
        ["TRANSPORT_ADMIN"] = Permissions.TransportAdministration,

        ["EMPLOYEES_READ"] = Permissions.EmployeeListing,
        ["EMPLOYEE_CREATE"] = Permissions.EmployeeCreate,
        ["EMPLOYEE_DETAIL"] = Permissions.EmployeeDetail,
        ["EMPLOYEE_UPDATE"] = Permissions.EmployeeSection,
        ["EMPLOYEE_HIERARCHY"] = Permissions.UserHierarchy,

        ["ROLES_READ"] = Permissions.RoleListing,
        ["ROLES_MANAGE"] = Permissions.RoleSaveEdit,
        ["ROLES_ASSIGN"] = Permissions.AssignRoles,
        ["PERMISSIONS_VIEW"] = null,

        ["REPORTS_READ"] = Permissions.ReportingModule,
        ["SETTINGS_READ"] = Permissions.MasterData,
        ["SETTINGS_UPDATE"] = null,
        ["NOTIFICATION_SETTINGS"] = Permissions.NotificationSettings,
    };
}

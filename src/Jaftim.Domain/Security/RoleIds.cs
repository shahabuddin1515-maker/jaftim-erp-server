namespace Jaftim.Domain.Security;

/// <summary>
/// Role.RoleId values [live-verified 2026-09-17]. RoleId and UserTypeId are numbered independently -
/// never derive one from the other. Use <see cref="UserTypeIds"/> for Role.UserTypeId.
/// Prefer <see cref="Permissions"/> checks over role checks; use these only where the legacy SPs
/// or flows genuinely branch on a role (documented in docs/MIGRATION_INVENTORY.md).
/// </summary>
public static class RoleIds
{
    public const long SuperAdmin = 1;
    /// <summary>"Agent" throughout the legacy codebase.</summary>
    public const long SalesExecutive = 2;
    public const long Customer = 3;
    public const long FinanceDirector = 4;
    public const long SalesDirector = 5;
    public const long CustomerSupportExecutive = 6;
    public const long SystemAdmin = 10;
    public const long SalesManager = 11;
    public const long CsdManager = 12;
    public const long FinanceManager = 13;
    public const long WebAdmin = 14;
    public const long QcManager = 15;
    public const long ProcurementManager = 16;
    public const long OperationManager = 17;
    public const long TeamLeadSales = 18;
    public const long FinanceExecutive = 19;
    public const long WebExecutive = 20;
    public const long QcExecutive = 21;
    public const long ProcurementExecutive = 22;
    public const long OperationExecutive = 23;
    public const long Hrbp = 24;
    public const long HrExecutive = 25;
}

/// <summary>UserType.UserTypeId values (Role.UserTypeId) [live-verified].</summary>
public static class UserTypeIds
{
    public const int SuperAdmin = 1;
    public const int SalesExecutive = 2;
    public const int Customer = 3;
    public const int FinanceDirector = 4;
    public const int SalesDirector = 5;
    public const int Csd = 6;
    public const int SystemAdmin = 7;
    public const int SalesManager = 8;
    public const int CsdManager = 9;
    public const int FinanceManager = 10;
    public const int WebAdmin = 11;
    public const int QcManager = 12;
    public const int ProcurementManager = 13;
    public const int OperationManager = 14;
    public const int TeamLeadSales = 15;
    public const int FinanceExecutive = 17;
    public const int WebExecutive = 18;
    public const int QcExecutive = 19;
    public const int ProcurementExecutive = 20;
    public const int OperationExecutive = 21;
    public const int Hrbp = 22;
    public const int HrExecutive = 23;
}

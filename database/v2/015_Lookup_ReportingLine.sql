SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    015_Lookup_ReportingLine.sql - GET /api/lookups/scoped/ReportingLine?roleId=.

    The "Reporting Line" dropdown of the user form: the people a user with role @RoleId can report to. Legacy:
    the form's ddlReportingLine (method-keyword GetReportingLineByRole -> SYS_AuthMethod 15 ->
    SYS_Auth_GetReportingLineByRole), which this copies with two owner decisions (2026-11-24):

      - The hierarchy is UserHierarchy, keyed on USER TYPE, not role: @RoleId -> Role.UserTypeId -> the
        UserHierarchy rows of that type -> their UserTypeParentId(s) -> every Role of those types -> users whose
        primary UserProfile.RoleId is one of them. DIRECT parents only, as legacy (Sales Executive -> Sales Manager
        and Team Lead Sales, not Sales Director). A type can have several parent rows.
      - Only live, active users (IsDeleted 0/NULL, StatusId = 2 Active). Legacy listed deleted and inactive users
        too (3 of the 5 Sales Directors it offered a Sales Manager were deleted).

    Primary role only (UserProfile.RoleId), as legacy and as every procedure sees it; secondary UserRole rows are
    not considered. Unknown or parentless role -> no rows. Not scoped to the caller (the legacy dropdown was not).

    Same columns as GetDllAuthTableValues / Lookup_GetScoped (TableName, Id, Value), ordered by Value.
    Read-only, no table change. Idempotent (CREATE OR ALTER). Apply to every tenant database.
*/
CREATE OR ALTER PROCEDURE dbo.Lookup_ReportingLine
    @RoleId    BIGINT,
    @CreatedBy BIGINT   = NULL,
    @CreatedAt DATETIME = NULL,
    @CompanyId BIGINT   = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT DISTINCT N'ReportingLine' AS TableName, UP.UserProfileId AS Id, UP.FullName AS Value
    FROM dbo.Role R
    INNER JOIN dbo.UserHierarchy UH ON UH.UserTypeId = R.UserTypeId AND UH.IsDeleted = 0
    INNER JOIN dbo.Role ParentRole ON ParentRole.UserTypeId = UH.UserTypeParentId
    INNER JOIN dbo.UserProfile UP ON UP.RoleId = ParentRole.RoleId
    WHERE R.RoleId = @RoleId
      AND ISNULL(UP.IsDeleted, 0) = 0
      AND UP.StatusId = 2                      -- Base_UserStatus Active (Domain UserStatus.Active)
    ORDER BY Value, Id;
END
GO

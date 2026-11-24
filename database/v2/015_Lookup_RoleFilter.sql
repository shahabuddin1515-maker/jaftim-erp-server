SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    015_Lookup_RoleFilter.sql - role-filtered sources for GET /api/lookups/scoped/{table}?roleId=, and the first
    one: vw_UserHierarchy, the user form's "Reporting Line" dropdown.

    Generic rule (owner, 2026-11-24: "takes a role id and a table or view name, returns id and value"):
      A source registered in dbo.LookupRoleFilter is filtered on its role column: WHERE <RoleColumn> = @RoleId, and
      @RoleId is then REQUIRED (THROW 50000 -> 422). Id/Value columns come from SYS_DropDownsWithAuth as for every
      other source. Adding another role-filtered dropdown = a table/view with a role column + one row in each of the
      two tables; no code change. Sources not in LookupRoleFilter keep the 014 rules unchanged.

    vw_UserHierarchy (RoleId, UserProfileId, FullName): one row per (role being filled, user it may report to).
      Legacy SYS_Auth_GetReportingLineByRole (SYS_AuthMethod keyword GetReportingLineByRole), with two owner decisions
      (2026-11-24): DIRECT parents only, as legacy; only live, active users (IsDeleted 0/NULL, StatusId = 2) - legacy
      also listed deleted and inactive ones. The hierarchy is keyed on USER TYPE: Role.UserTypeId -> UserHierarchy
      .UserTypeParentId -> every Role of that type -> users whose PRIMARY RoleId it is (docs/USERS.md "Reporting line").
      Being in SYS_DropDownsWithAuth, the legacy GetDllAuthTableValues / GET /api/lookups also accept it - unfiltered,
      so a user appears once per role below them. Harmless; use the scoped endpoint.

    This script supersedes 014's Lookup_GetScoped (the full definition is below; 014 must run first, 015 after).
    The first, unreleased version of this script created dbo.Lookup_ReportingLine; it is dropped here.

    Additive only: a new view, a new table, one new whitelist row. Idempotent. Apply to every tenant database.
*/

DROP PROCEDURE IF EXISTS dbo.Lookup_ReportingLine;
GO

CREATE OR ALTER VIEW dbo.vw_UserHierarchy
AS
SELECT DISTINCT
    R.RoleId,                -- the role of the user whose reporting line is being chosen (filter column)
    UP.UserProfileId,
    UP.FullName
FROM dbo.Role R
INNER JOIN dbo.UserHierarchy UH ON UH.UserTypeId = R.UserTypeId AND UH.IsDeleted = 0
INNER JOIN dbo.Role ParentRole ON ParentRole.UserTypeId = UH.UserTypeParentId
INNER JOIN dbo.UserProfile UP ON UP.RoleId = ParentRole.RoleId
WHERE ISNULL(UP.IsDeleted, 0) = 0
  AND UP.StatusId = 2;       -- Base_UserStatus Active (Domain UserStatus.Active)
GO

IF OBJECT_ID(N'dbo.LookupRoleFilter', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LookupRoleFilter
    (
        LookupRoleFilterId INT            IDENTITY(1, 1) NOT NULL CONSTRAINT PK_LookupRoleFilter PRIMARY KEY,
        TableName          NVARCHAR(128)  NOT NULL CONSTRAINT UQ_LookupRoleFilter_TableName UNIQUE,  -- = SYS_DropDownsWithAuth.TableName
        RoleColumn         SYSNAME        NOT NULL,                                                   -- compared with @RoleId
        CreatedAtUtc       DATETIME2(3)   NOT NULL CONSTRAINT DF_LookupRoleFilter_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy          BIGINT         NULL,
        ModifiedAtUtc      DATETIME2(3)   NULL,
        ModifiedBy         BIGINT         NULL,
        IsDeleted          BIT            NOT NULL CONSTRAINT DF_LookupRoleFilter_IsDeleted DEFAULT (0)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.SYS_DropDownsWithAuth WHERE TableName = N'vw_UserHierarchy')
    INSERT INTO dbo.SYS_DropDownsWithAuth (TableName, TableId, TableValue)
    VALUES (N'vw_UserHierarchy', N'UserProfileId', N'FullName');

IF NOT EXISTS (SELECT 1 FROM dbo.LookupRoleFilter WHERE TableName = N'vw_UserHierarchy')
    INSERT INTO dbo.LookupRoleFilter (TableName, RoleColumn) VALUES (N'vw_UserHierarchy', N'RoleId');
GO

/*
    Lookup_GetScoped - 014's definition plus the role-filter branch. Rules per source:
      registered in LookupRoleFilter   rows WHERE <RoleColumn> = @RoleId; @RoleId required. Not scoped to the caller.
      SalesPerson_View / Customer_View caller-scoped exactly as 014 (see that file's header).
      anything else                    every row, as legacy.
*/
CREATE OR ALTER PROCEDURE dbo.Lookup_GetScoped
    @TableName NVARCHAR(128),
    @RoleId    BIGINT   = NULL,
    @CreatedBy BIGINT   = NULL,
    @CreatedAt DATETIME = NULL,
    @CompanyId BIGINT   = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Table SYSNAME, @IdColumn SYSNAME, @ValueColumn SYSNAME;
    SELECT TOP (1) @Table = W.TableName, @IdColumn = W.TableId, @ValueColumn = W.TableValue
    FROM dbo.SYS_DropDownsWithAuth W
    WHERE W.TableName = LTRIM(RTRIM(@TableName))
    ORDER BY W.Id;

    IF @Table IS NULL
    BEGIN
        SELECT CAST(NULL AS SYSNAME) AS TableName, CAST(NULL AS BIGINT) AS Id, CAST(NULL AS NVARCHAR(4000)) AS Value
        WHERE 1 = 0;
        RETURN;
    END

    -- Role-filtered source (015): the explicit @RoleId only, never the caller's role - the list belongs to the role
    -- being edited, not to whoever is signed in.
    DECLARE @RoleColumn SYSNAME =
        (SELECT F.RoleColumn FROM dbo.LookupRoleFilter F WHERE F.TableName = @Table AND F.IsDeleted = 0);
    IF @RoleColumn IS NOT NULL
    BEGIN
        IF @RoleId IS NULL
        BEGIN
            DECLARE @Msg NVARCHAR(400) = N'roleId is required for ' + @Table + N': its rows are listed per role.';
            THROW 50000, @Msg, 1;
        END

        DECLARE @RoleSql NVARCHAR(MAX) =
              N'SELECT DISTINCT @T AS TableName, ' + QUOTENAME(@IdColumn) + N' AS Id, '
            + QUOTENAME(@ValueColumn) + N' AS Value FROM dbo.' + QUOTENAME(@Table)
            + N' WHERE ' + QUOTENAME(@RoleColumn) + N' = @R'
            + N' ORDER BY Value, Id;';
        EXEC sys.sp_executesql @RoleSql, N'@T SYSNAME, @R BIGINT', @T = @Table, @R = @RoleId;
        RETURN;
    END

    DECLARE @CallerRoleId BIGINT = COALESCE(@RoleId, (SELECT RoleId FROM dbo.UserProfile WHERE UserProfileId = @CreatedBy));

    -- The caller's reporting subtree, caller included: the same set as fn_GetUserChildProfiles(@CreatedBy), but
    -- walked iteratively so a ReportsTo_UserId cycle ends the walk instead of failing with "maximum recursion
    -- 100 has been exhausted" (12259 <-> 12475 on the UAT copy, 2026-11-20; docs/REWRITE_PLAN.md section 7).
    DECLARE @Tree TABLE (UserProfileId BIGINT PRIMARY KEY);
    IF @CreatedBy IS NOT NULL
       AND (   (@Table = N'SalesPerson_View' AND ISNULL(@CallerRoleId, 0) NOT IN (1, 10))
            OR (@Table = N'Customer_View'    AND @CallerRoleId IN (12, 18)))
    BEGIN
        INSERT @Tree (UserProfileId) VALUES (@CreatedBy);
        WHILE @@ROWCOUNT > 0
            INSERT @Tree (UserProfileId)
            SELECT DISTINCT C.UserProfileId
            FROM dbo.UserProfile C
            INNER JOIN @Tree P ON C.ReportsTo_UserId = P.UserProfileId
            WHERE NOT EXISTS (SELECT 1 FROM @Tree X WHERE X.UserProfileId = C.UserProfileId);
    END

    IF @Table = N'SalesPerson_View'
    BEGIN
        -- Non-admins: the people below them only, never themself (owner, 2026-11-20) - nobody below, nothing loads.
        SELECT @Table AS TableName, SP.UserProfileId AS Id, SP.FullName AS Value
        FROM dbo.SalesPerson_View SP
        INNER JOIN dbo.UserProfile UP ON UP.UserProfileId = SP.UserProfileId
        INNER JOIN dbo.Role R ON R.RoleId = UP.RoleId
        WHERE    (@CallerRoleId = 1)
              OR (@CallerRoleId = 10 AND R.UserTypeId NOT IN (1, 7))
              OR (    ISNULL(@CallerRoleId, 0) NOT IN (1, 10)
                  AND SP.UserProfileId <> @CreatedBy
                  AND EXISTS (SELECT 1 FROM @Tree T WHERE T.UserProfileId = SP.UserProfileId))
        ORDER BY SP.FullName, SP.UserProfileId;
        RETURN;
    END

    IF @Table = N'Customer_View' AND @CallerRoleId IN (12, 18)
    BEGIN
        -- GetInquiryAll's role-12 rule: customers actively tagged to the caller or anyone below them; untagged
        -- customers are not shown. Extended to Team Lead Sales (18) by the owner, 2026-11-20.
        SELECT DISTINCT @Table AS TableName, CV.UserProfileId AS Id, CV.FullName AS Value
        FROM dbo.Customer_View CV
        INNER JOIN dbo.CustomerTagging CT ON CT.CustomerId = CV.UserProfileId
        INNER JOIN @Tree T ON T.UserProfileId = CT.AgentId
        WHERE CT.IsActive = 1 AND CT.AgentId <> 0
        ORDER BY Value, Id;
        RETURN;
    END

    IF @Table = N'Customer_View' AND @CallerRoleId = 2
    BEGIN
        SELECT @Table AS TableName, CV.UserProfileId AS Id, CV.FullName AS Value
        FROM dbo.Customer_View CV
        INNER JOIN dbo.CustomerTagging CT ON CT.CustomerId = CV.UserProfileId
        WHERE CT.IsActive = 1 AND CT.AgentId = @CreatedBy
        ORDER BY CV.FullName, CV.UserProfileId;
        RETURN;
    END

    DECLARE @Sql NVARCHAR(MAX) =
          N'SELECT @T AS TableName, ' + QUOTENAME(@IdColumn) + N' AS Id, '
        + QUOTENAME(@ValueColumn) + N' AS Value FROM dbo.' + QUOTENAME(@Table)
        + N' ORDER BY ' + QUOTENAME(@ValueColumn) + N', ' + QUOTENAME(@IdColumn) + N';';
    EXEC sys.sp_executesql @Sql, N'@T SYSNAME', @T = @Table;
END
GO

SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    014_Lookup_Scoped.sql - GET /api/lookups/scoped/{table}.

    One dropdown source from the SYS_DropDownsWithAuth whitelist, scoped to the CALLER. The caller is @CreatedBy
    (injected by IDbExecutor from the token) and the scoping uses their primary UserProfile.RoleId, like every other
    procedure (docs/AUTH.md). The role is deliberately NOT a parameter: a client-supplied role could be omitted or
    forged to see everything.

    Rules per source:
      SalesPerson_View  the UserGetAll_V2 visibility (database/v2/010): RoleId 1 sees every row, RoleId 10 every row
                        outside user types 1/7, anyone else only their reporting subtree (the fn_GetUserChildProfiles
                        set, caller included, walked cycle-safe). So a Team Lead Sales (18) gets themself and the
                        people under them.
      Customer_View     legacy GetDllAuthTableValues verbatim: RoleId 2 (Sales Executive) only customers actively
                        tagged to them; everyone else every row.
      anything else     every row, as legacy.

    Same columns and order as GetDllAuthTableValues (TableName, Id, Value, ordered by Value). Unknown table -> no
    rows (the API validates against the whitelist first and answers 404). Identifiers come from the whitelist
    table and are QUOTENAME'd; the requested name is never concatenated into SQL.

    Read-only, no table change. Idempotent (CREATE OR ALTER). Apply to every tenant database.
*/
CREATE OR ALTER PROCEDURE dbo.Lookup_GetScoped
    @TableName NVARCHAR(128),
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

    DECLARE @CallerRoleId BIGINT = (SELECT RoleId FROM dbo.UserProfile WHERE UserProfileId = @CreatedBy);

    IF @Table = N'SalesPerson_View'
    BEGIN
        -- The caller's reporting subtree, caller included: the same set as fn_GetUserChildProfiles(@CreatedBy), but
        -- walked iteratively so a ReportsTo_UserId cycle ends the walk instead of failing with "maximum recursion
        -- 100 has been exhausted" (12259 <-> 12475 on the UAT copy, 2026-11-20; docs/REWRITE_PLAN.md section 7).
        DECLARE @Tree TABLE (UserProfileId BIGINT PRIMARY KEY);
        IF ISNULL(@CallerRoleId, 0) NOT IN (1, 10) AND @CreatedBy IS NOT NULL
        BEGIN
            INSERT @Tree (UserProfileId) VALUES (@CreatedBy);
            WHILE @@ROWCOUNT > 0
                INSERT @Tree (UserProfileId)
                SELECT DISTINCT C.UserProfileId
                FROM dbo.UserProfile C
                INNER JOIN @Tree P ON C.ReportsTo_UserId = P.UserProfileId
                WHERE NOT EXISTS (SELECT 1 FROM @Tree X WHERE X.UserProfileId = C.UserProfileId);
        END

        SELECT @Table AS TableName, SP.UserProfileId AS Id, SP.FullName AS Value
        FROM dbo.SalesPerson_View SP
        INNER JOIN dbo.UserProfile UP ON UP.UserProfileId = SP.UserProfileId
        INNER JOIN dbo.Role R ON R.RoleId = UP.RoleId
        WHERE    (@CallerRoleId = 1)
              OR (@CallerRoleId = 10 AND R.UserTypeId NOT IN (1, 7))
              OR (ISNULL(@CallerRoleId, 0) NOT IN (1, 10) AND EXISTS (SELECT 1 FROM @Tree T WHERE T.UserProfileId = SP.UserProfileId))
        ORDER BY SP.FullName, SP.UserProfileId;
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

SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    003_Tenant_GetPage.sql - server-side search, sort and paging for the tenant registry (GET /api/tenants).

    Same rows as Catalog_Tenant_GetAll (non-deleted tenants) with the paged-list contract of
    database/v2/010_PagedLists.sql: page rows, then TotalRecords. Never selects ConnectionString - this is the admin
    list, and the connection string must not leave the server. Search: code, name. Sort keys: code, name, tenantId,
    createdAtUtc; default = Code (the Catalog_Tenant_GetAll order) with TenantId as tie-break.
    Catalog procedures never declare the audit trio. Catalog_Tenant_GetAll is untouched (tenant resolution uses it).
    Idempotent (CREATE OR ALTER). Apply to the catalog database.
*/
CREATE OR ALTER PROCEDURE dbo.Catalog_Tenant_GetPage
    @Search         NVARCHAR(200) = NULL,
    @SortBy         NVARCHAR(50)  = NULL,
    @SortDescending BIT           = 0,
    @PageNumber     INT           = 1,
    @PageSize       INT           = 10
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Like NVARCHAR(700) = NULLIF(LTRIM(RTRIM(@Search)), N'');
    IF @Like IS NOT NULL SET @Like = N'%' + REPLACE(REPLACE(REPLACE(@Like, N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]') + N'%';
    IF @PageNumber IS NULL OR @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize IS NULL OR @PageSize < 1 SET @PageSize = 10;
    IF @PageSize > 200 SET @PageSize = 200;
    SET @SortDescending = ISNULL(@SortDescending, 0);

    SELECT TenantId, Code, Name, IsActive, CreatedAtUtc
    INTO #Rows
    FROM dbo.Tenant
    WHERE IsDeleted = 0
      AND (@Like IS NULL OR Code LIKE @Like OR Name LIKE @Like)
    OPTION (RECOMPILE);

    SELECT TenantId, Code, Name, IsActive
    FROM #Rows
    ORDER BY
        CASE WHEN @SortDescending = 0 THEN CASE @SortBy WHEN 'code' THEN Code WHEN 'name' THEN Name END END ASC,
        CASE WHEN @SortDescending = 1 THEN CASE @SortBy WHEN 'code' THEN Code WHEN 'name' THEN Name END END DESC,
        CASE WHEN @SortDescending = 0 AND @SortBy = 'tenantId' THEN TenantId END ASC,
        CASE WHEN @SortDescending = 1 AND @SortBy = 'tenantId' THEN TenantId END DESC,
        CASE WHEN @SortDescending = 0 AND @SortBy = 'createdAtUtc' THEN CreatedAtUtc END ASC,
        CASE WHEN @SortDescending = 1 AND @SortBy = 'createdAtUtc' THEN CreatedAtUtc END DESC,
        Code ASC, TenantId ASC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;

    SELECT COUNT(*) AS TotalRecords FROM #Rows;
END
GO

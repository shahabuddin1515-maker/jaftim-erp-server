SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    011_RolesGetAll_V2.sql - server-side search, sort and paging for the roles list (GET /api/roles).

    Same rows as the legacy RolesGetAll (non-deleted roles joined to UserType), with the paged-list contract of
    010_PagedLists.sql: @Search / @SortBy / @SortDescending / @PageNumber / @PageSize in, page rows then TotalRecords
    out. Search: role name, user type name, default path. Sort keys: roleName, userTypeName, createdAt, modifiedAt;
    default = legacy order (CreatedAt DESC) with RoleId as tie-break.

    RolesGetAll is untouched (the role save flow and the permission-cache invalidation still read the full list).
    Idempotent (CREATE OR ALTER). Apply to every tenant database.
*/
CREATE OR ALTER PROCEDURE dbo.RolesGetAll_V2
    @Search         NVARCHAR(200) = NULL,
    @SortBy         NVARCHAR(50)  = NULL,
    @SortDescending BIT           = 0,
    @PageNumber     INT           = 1,
    @PageSize       INT           = 10,
    @CreatedBy      BIGINT        = NULL,
    @CreatedAt      DATETIME      = NULL,
    @CompanyId      BIGINT        = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Like NVARCHAR(700) = NULLIF(LTRIM(RTRIM(@Search)), N'');
    IF @Like IS NOT NULL SET @Like = N'%' + REPLACE(REPLACE(REPLACE(@Like, N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]') + N'%';
    IF @PageNumber IS NULL OR @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize IS NULL OR @PageSize < 1 SET @PageSize = 10;
    IF @PageSize > 200 SET @PageSize = 200;
    SET @SortDescending = ISNULL(@SortDescending, 0);

    SELECT R.RoleId, R.RoleName, R.DefaultPath, R.UserTypeId, UT.UserTypeName, R.IsDeleted,
           R.CreatedAt, ISNULL(R.ModifiedAt, R.CreatedAt) AS ModifiedAt
    INTO #Rows
    FROM dbo.[Role] R
    INNER JOIN dbo.UserType UT ON R.UserTypeId = UT.UserTypeId
    WHERE R.IsDeleted = 0
      AND (@Like IS NULL OR R.RoleName LIKE @Like OR UT.UserTypeName LIKE @Like OR R.DefaultPath LIKE @Like)
    OPTION (RECOMPILE);

    SELECT RoleId, RoleName, DefaultPath, UserTypeId, UserTypeName, IsDeleted, CreatedAt, ModifiedAt
    FROM #Rows
    ORDER BY
        CASE WHEN @SortDescending = 0 THEN CASE @SortBy WHEN 'roleName' THEN RoleName WHEN 'userTypeName' THEN UserTypeName END END ASC,
        CASE WHEN @SortDescending = 1 THEN CASE @SortBy WHEN 'roleName' THEN RoleName WHEN 'userTypeName' THEN UserTypeName END END DESC,
        CASE WHEN @SortDescending = 0 THEN CASE @SortBy WHEN 'createdAt' THEN CreatedAt WHEN 'modifiedAt' THEN ModifiedAt END END ASC,
        CASE WHEN @SortDescending = 1 THEN CASE @SortBy WHEN 'createdAt' THEN CreatedAt WHEN 'modifiedAt' THEN ModifiedAt END END DESC,
        CreatedAt DESC, RoleId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;

    SELECT COUNT(*) AS TotalRecords FROM #Rows;
END
GO

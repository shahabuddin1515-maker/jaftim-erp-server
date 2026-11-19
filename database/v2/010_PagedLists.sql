SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    010_PagedLists.sql - server-side search, sort and paging for the staff and customer-tagging lists.

    One _V2 per legacy list procedure. The legacy procedures return every row and the legacy screens filtered in the
    browser; these copy the legacy row-visibility rules VERBATIM and add, inside SQL:

        @Search          free text, LIKE '%term%' over the columns the screen shows (wildcards in the term are literal)
        @SortBy          a whitelisted column key (case-insensitive); anything else falls back to the legacy order
        @SortDescending  0 / 1
        @PageNumber      1-based, clamped to >= 1
        @PageSize        clamped to 1..200

    Result sets (same order as StockGetAll_New): 1) the page rows, 2) TotalRecords (the filtered count).
    Default order is the legacy one (UP.CreatedBy DESC) plus a unique tie-break so pages never overlap.
    Projections are explicit (not UP.*): the legacy UP.* + aliased ModifiedAt returned ModifiedAt twice.

    Legacy procedures untouched: UserGetAll, CustomerTagging_GetAllManagerAgent, CustomerTagging_AgentTaggedCustomer,
    CustomerTagging_AgentUnTaggedCustomerHistory, CustomerTagging_AvailableCustomers.
    Idempotent (CREATE OR ALTER). Apply to every tenant database.
*/

-- =====================================================================================================================
-- UserGetAll_V2 - staff list. Visibility = UserGetAll: caller RoleId 1 sees every non-customer, RoleId 10 every
-- non-customer outside user types 1/7, anyone else only their reporting tree (fn_GetUserChildProfiles).
-- New: @UserTypeId actually filters (UserGetAll declared it and ignored it).
-- Sort keys: code, fullName, email, roleName, userStatusName, taggedCount, createdAt, modifiedAt.
-- =====================================================================================================================
CREATE OR ALTER PROCEDURE dbo.UserGetAll_V2
    @UserTypeId     INT           = NULL,
    @Search         NVARCHAR(200) = NULL,
    @SortBy         NVARCHAR(50)  = NULL,
    @SortDescending BIT           = 0,
    @PageNumber     INT           = 1,
    @PageSize       INT           = 10,
    @CreatedBy      BIGINT,
    @CreatedAt      DATETIME,
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

    DECLARE @CallerRoleId BIGINT = (SELECT RoleId FROM dbo.UserProfile WHERE UserProfileId = @CreatedBy);
    DECLARE @Tree TABLE (UserProfileId BIGINT PRIMARY KEY);
    IF ISNULL(@CallerRoleId, 0) NOT IN (1, 10)
        INSERT @Tree (UserProfileId) SELECT DISTINCT UserProfileId FROM dbo.fn_GetUserChildProfiles(@CreatedBy);

    ;WITH TagCounts AS
    (
        SELECT C.AgentId, COUNT(*) AS TaggedCount
        FROM dbo.CustomerTagging C
        WHERE C.IsDeleted = 0 AND C.IsActive = 1
        GROUP BY C.AgentId
    )
    SELECT UP.UserProfileId, UP.Code, UP.FullName, UP.Username, UP.Email, UP.URL, UP.AspNetUserId,
           UP.RoleId, R.RoleName, UP.GenderId, BG.GenderName, UP.StatusId, BS.UserStatusName,
           UP.CountryId, BCTRY.Base_Country_Name AS CountryName, UP.CityId, BCT.Base_City_Name AS CityName,
           UP.Address, UP.CountryCode, UP.ImageURL, UP.ReportsTo_UserId, UP.RemoteAccessAllowed,
           ISNULL(TC.TaggedCount, 0) AS TaggedCount,
           ISNULL(UPD.Phone, 'N/A') AS Phone,
           ISNULL(UPD.SecondaryPhone, 'N/A') AS SecondaryPhone,
           ISNULL(UPD.WhatsAppNumber, 'N/A') AS WhatsAppNumber,
           ISNULL(CONVERT(NVARCHAR(MAX), UPD.CustomerTagLimit), 'N/A') AS CustomerTagLimit,
           UP.CreatedAt,
           ISNULL(UP.ModifiedAt, UP.CreatedAt) AS ModifiedAt,
           UP.CreatedBy AS SortCreatedBy
    INTO #Rows
    FROM dbo.UserProfile UP
    LEFT JOIN dbo.UserProfileDetail UPD ON UPD.UserProfileId = UP.UserProfileId
    INNER JOIN dbo.Role R ON UP.RoleId = R.RoleId
    LEFT JOIN dbo.Base_Gender BG ON UP.GenderId = BG.GenderId
    LEFT JOIN dbo.Base_UserStatus BS ON UP.StatusId = BS.UserStatusId
    LEFT JOIN dbo.Base_Country BCTRY ON UP.CountryId = BCTRY.Base_Country_Id
    LEFT JOIN dbo.Base_City BCT ON UP.CityId = BCT.Base_City_Id
    LEFT JOIN TagCounts TC ON UP.UserProfileId = TC.AgentId
    WHERE UP.IsDeleted = 0
      AND (    (@CallerRoleId = 1  AND R.UserTypeId NOT IN (3))
            OR (@CallerRoleId = 10 AND R.UserTypeId NOT IN (1, 7, 3))
            OR (ISNULL(@CallerRoleId, 0) NOT IN (1, 10) AND EXISTS (SELECT 1 FROM @Tree T WHERE T.UserProfileId = UP.UserProfileId)))
      AND (@UserTypeId IS NULL OR R.UserTypeId = @UserTypeId)
      AND (@Like IS NULL
           OR UP.Code LIKE @Like OR UP.FullName LIKE @Like OR UP.Username LIKE @Like OR UP.Email LIKE @Like
           OR R.RoleName LIKE @Like OR BCTRY.Base_Country_Name LIKE @Like
           OR UPD.Phone LIKE @Like OR UPD.WhatsAppNumber LIKE @Like)
    OPTION (RECOMPILE);

    SELECT UserProfileId, Code, FullName, Username, Email, URL, AspNetUserId, RoleId, RoleName, GenderId, GenderName,
           StatusId, UserStatusName, CountryId, CountryName, CityId, CityName, Address, CountryCode, ImageURL,
           ReportsTo_UserId, RemoteAccessAllowed, TaggedCount, Phone, SecondaryPhone, WhatsAppNumber, CustomerTagLimit,
           CreatedAt, ModifiedAt
    FROM #Rows
    ORDER BY
        CASE WHEN @SortDescending = 0 THEN CASE @SortBy WHEN 'code' THEN Code WHEN 'fullName' THEN FullName WHEN 'email' THEN Email
                                                        WHEN 'roleName' THEN RoleName WHEN 'userStatusName' THEN UserStatusName END END ASC,
        CASE WHEN @SortDescending = 1 THEN CASE @SortBy WHEN 'code' THEN Code WHEN 'fullName' THEN FullName WHEN 'email' THEN Email
                                                        WHEN 'roleName' THEN RoleName WHEN 'userStatusName' THEN UserStatusName END END DESC,
        CASE WHEN @SortDescending = 0 THEN CASE @SortBy WHEN 'createdAt' THEN CreatedAt WHEN 'modifiedAt' THEN ModifiedAt END END ASC,
        CASE WHEN @SortDescending = 1 THEN CASE @SortBy WHEN 'createdAt' THEN CreatedAt WHEN 'modifiedAt' THEN ModifiedAt END END DESC,
        CASE WHEN @SortDescending = 0 AND @SortBy = 'taggedCount' THEN TaggedCount END ASC,
        CASE WHEN @SortDescending = 1 AND @SortBy = 'taggedCount' THEN TaggedCount END DESC,
        SortCreatedBy DESC, UserProfileId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;

    SELECT COUNT(*) AS TotalRecords FROM #Rows;
END
GO

-- =====================================================================================================================
-- CustomerTagging_GetAllManagerAgent_V2 - agents (roles of user type 2/5) with their customer counts.
-- Visibility = legacy: caller RoleId 1 or 7 sees every agent, anyone else agents in fn_GetUserAccessibleCountries.
-- Sort keys: code, fullName, email, countryName, activeCustomerCount, totalCustomerCount, modifiedAt.
-- =====================================================================================================================
CREATE OR ALTER PROCEDURE dbo.CustomerTagging_GetAllManagerAgent_V2
    @Search         NVARCHAR(200) = NULL,
    @SortBy         NVARCHAR(50)  = NULL,
    @SortDescending BIT           = 0,
    @PageNumber     INT           = 1,
    @PageSize       INT           = 10,
    @CreatedBy      BIGINT,
    @CreatedAt      DATETIME,
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

    DECLARE @SeesAll BIT = CASE WHEN (SELECT RoleId FROM dbo.UserProfile WHERE UserProfileId = @CreatedBy) IN (1, 7) THEN 1 ELSE 0 END;
    DECLARE @Countries TABLE (CountryId BIGINT PRIMARY KEY);
    IF @SeesAll = 0
        INSERT @Countries (CountryId) SELECT DISTINCT * FROM dbo.fn_GetUserAccessibleCountries(@CreatedBy);

    ;WITH ActiveCustomer AS
    (
        SELECT CT.AgentId, COUNT(CT.CustomerId) AS TaggingCount
        FROM dbo.CustomerTagging CT
        WHERE CT.IsActive = 1 AND CT.IsDeleted = 0
        GROUP BY CT.AgentId
    ),
    TotalCustomer AS
    (
        SELECT CT.AgentId, COUNT(DISTINCT CT.CustomerId) AS TaggingCount
        FROM dbo.CustomerTagging CT
        WHERE CT.IsDeleted = 0
        GROUP BY CT.AgentId
    )
    SELECT UP.UserProfileId, UP.Code, UP.FullName, UP.Email, UP.RoleId, R.RoleName,
           UP.CountryId, BCTRY.Base_Country_Name AS CountryName, BCT.Base_City_Name AS CityName, BS.UserStatusName,
           ISNULL(UPD.Phone, 'N/A') AS Phone,
           ISNULL(UPD.WhatsAppNumber, 'N/A') AS WhatsAppNumber,
           UP.ImageURL,
           ISNULL(UP.ModifiedAt, UP.CreatedAt) AS ModifiedAt,
           A.TaggingCount AS ActiveCustomerCount,
           T.TaggingCount AS TotalCustomerCount,
           UP.CreatedBy AS SortCreatedBy
    INTO #Rows
    FROM dbo.UserProfile UP
    LEFT JOIN ActiveCustomer A ON A.AgentId = UP.UserProfileId
    LEFT JOIN TotalCustomer T ON T.AgentId = UP.UserProfileId
    LEFT JOIN dbo.UserProfileDetail UPD ON UPD.UserProfileId = UP.UserProfileId
    INNER JOIN dbo.Role R ON UP.RoleId = R.RoleId AND R.UserTypeId IN (2, 5)
    LEFT JOIN dbo.Base_UserStatus BS ON UP.StatusId = BS.UserStatusId
    LEFT JOIN dbo.Base_Country BCTRY ON UP.CountryId = BCTRY.Base_Country_Id
    LEFT JOIN dbo.Base_City BCT ON UP.CityId = BCT.Base_City_Id
    WHERE UP.IsDeleted = 0
      AND (@SeesAll = 1 OR EXISTS (SELECT 1 FROM @Countries C WHERE C.CountryId = UP.CountryId))
      AND (@Like IS NULL
           OR UP.Code LIKE @Like OR UP.FullName LIKE @Like OR UP.Email LIKE @Like OR R.RoleName LIKE @Like
           OR BCTRY.Base_Country_Name LIKE @Like OR UPD.Phone LIKE @Like)
    OPTION (RECOMPILE);

    SELECT UserProfileId, Code, FullName, Email, RoleId, RoleName, CountryId, CountryName, CityName, UserStatusName,
           Phone, WhatsAppNumber, ImageURL, ModifiedAt, ActiveCustomerCount, TotalCustomerCount
    FROM #Rows
    ORDER BY
        CASE WHEN @SortDescending = 0 THEN CASE @SortBy WHEN 'code' THEN Code WHEN 'fullName' THEN FullName WHEN 'email' THEN Email
                                                        WHEN 'countryName' THEN CountryName END END ASC,
        CASE WHEN @SortDescending = 1 THEN CASE @SortBy WHEN 'code' THEN Code WHEN 'fullName' THEN FullName WHEN 'email' THEN Email
                                                        WHEN 'countryName' THEN CountryName END END DESC,
        CASE WHEN @SortDescending = 0 AND @SortBy = 'modifiedAt' THEN ModifiedAt END ASC,
        CASE WHEN @SortDescending = 1 AND @SortBy = 'modifiedAt' THEN ModifiedAt END DESC,
        CASE WHEN @SortDescending = 0 THEN CASE @SortBy WHEN 'activeCustomerCount' THEN ActiveCustomerCount WHEN 'totalCustomerCount' THEN TotalCustomerCount END END ASC,
        CASE WHEN @SortDescending = 1 THEN CASE @SortBy WHEN 'activeCustomerCount' THEN ActiveCustomerCount WHEN 'totalCustomerCount' THEN TotalCustomerCount END END DESC,
        SortCreatedBy DESC, UserProfileId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;

    SELECT COUNT(*) AS TotalRecords FROM #Rows;
END
GO

-- =====================================================================================================================
-- CustomerTagging_AgentCustomers_V2 - replaces BOTH CustomerTagging_AgentTaggedCustomer (@Untagged = 0, active tags)
-- and CustomerTagging_AgentUnTaggedCustomerHistory (@Untagged = 1, inactive tags + when/by whom). The two legacy
-- procedures differ only in CT.IsActive and the two history columns, so one V2 keeps their rules in one place.
-- Visibility = legacy: a caller whose primary RoleId is 2 gets rows only when @agentId is themself.
-- History rows repeat a customer per untag, so CustomerTaggingId is the tie-break.
-- Sort keys: code, fullName, countryName, customerType, portOfDischarge, unTaggedBy, modifiedAt, unTaggedDate,
-- numberOfTimesTagged.
-- =====================================================================================================================
CREATE OR ALTER PROCEDURE dbo.CustomerTagging_AgentCustomers_V2
    @agentId        BIGINT,
    @Untagged       BIT           = 0,
    @Search         NVARCHAR(200) = NULL,
    @SortBy         NVARCHAR(50)  = NULL,
    @SortDescending BIT           = 0,
    @PageNumber     INT           = 1,
    @PageSize       INT           = 10,
    @CreatedBy      BIGINT,
    @CreatedAt      DATETIME,
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
    SET @Untagged = ISNULL(@Untagged, 0);

    DECLARE @Check BIT = CASE WHEN EXISTS (SELECT 1 FROM dbo.UserProfile WHERE UserProfileId = @CreatedBy AND RoleId = 2) THEN 1 ELSE 0 END;

    SELECT UP.UserProfileId, UP.Code, UP.FullName, UP.Email, UP.CountryId,
           BCTRY.Base_Country_Name AS CountryName, BCT.Base_City_Name AS CityName, BG.GenderName, BS.UserStatusName,
           ISNULL(UPD.Phone, 'N/A') AS Phone,
           ISNULL(UPD.SecondaryPhone, 'N/A') AS SecondaryPhone,
           ISNULL(UPD.WhatsAppNumber, 'N/A') AS WhatsAppNumber,
           ISNULL(BCU.CustomerTypeName, 'N/A') AS CustomerType,
           ISNULL(BSS.SourceName, 'N/A') AS SourceName,
           ISNULL(BPD.Base_DischargePort_Name, 'N/A') AS PortOfDischarge,
           UP.PartyKind,
           ISNULL(CT.ModifiedAt, CT.CreatedAt) AS ModifiedAt,
           ISNULL((SELECT COUNT(CT_inner.CustomerId)
                   FROM dbo.CustomerTagging CT_inner
                   WHERE CT_inner.AgentId = @agentId AND CT_inner.IsDeleted = 0 AND CT_inner.CustomerId = CT.CustomerId), 0) AS NumberOfTimesTagged,
           CASE WHEN @Untagged = 1 THEN CT.ModifiedAt END AS UnTaggedDate,
           UB.FullName AS UnTaggedBy,
           UP.CreatedBy AS SortCreatedBy,
           CT.CustomerTaggingId AS SortTagId
    INTO #Rows
    FROM dbo.UserProfile UP
    INNER JOIN dbo.CustomerTagging CT ON CT.IsActive = CASE WHEN @Untagged = 1 THEN 0 ELSE 1 END
                                     AND CT.IsDeleted = 0 AND CT.CustomerId = UP.UserProfileId
    -- legacy history INNER-joins the untagging user; the tagged list has no such join
    LEFT JOIN dbo.UserProfile UB ON @Untagged = 1 AND UB.UserProfileId = CT.ModifiedBy
    LEFT JOIN dbo.UserProfileDetail UPD ON UPD.UserProfileId = UP.UserProfileId
    LEFT JOIN dbo.Base_CustomerType BCU ON BCU.CustomerTypeId = UPD.CustomerTypeId
    LEFT JOIN dbo.Base_Source BSS ON BSS.SourceId = UPD.SourceId
    LEFT JOIN dbo.Base_DischargePort BPD ON BPD.Base_DischargePort_Id = UPD.PortOfDischargeId
    INNER JOIN dbo.Role R ON UP.RoleId = R.RoleId AND R.UserTypeId = 3
    LEFT JOIN dbo.Base_Gender BG ON UP.GenderId = BG.GenderId
    LEFT JOIN dbo.Base_UserStatus BS ON UP.StatusId = BS.UserStatusId
    LEFT JOIN dbo.Base_Country BCTRY ON UP.CountryId = BCTRY.Base_Country_Id
    LEFT JOIN dbo.Base_City BCT ON UP.CityId = BCT.Base_City_Id
    WHERE UP.IsDeleted = 0 AND CT.AgentId = @agentId AND (@Check = 0 OR @agentId = @CreatedBy)
      AND (@Untagged = 0 OR UB.UserProfileId IS NOT NULL)
      AND (@Like IS NULL
           OR UP.Code LIKE @Like OR UP.FullName LIKE @Like OR UP.Email LIKE @Like
           OR UPD.Phone LIKE @Like OR UPD.WhatsAppNumber LIKE @Like OR BCTRY.Base_Country_Name LIKE @Like
           OR BCU.CustomerTypeName LIKE @Like OR BPD.Base_DischargePort_Name LIKE @Like OR UB.FullName LIKE @Like)
    OPTION (RECOMPILE);

    SELECT UserProfileId, Code, FullName, Email, CountryId, CountryName, CityName, GenderName, UserStatusName,
           Phone, SecondaryPhone, WhatsAppNumber, CustomerType, SourceName, PortOfDischarge, PartyKind,
           ModifiedAt, NumberOfTimesTagged, UnTaggedDate, UnTaggedBy
    FROM #Rows
    ORDER BY
        CASE WHEN @SortDescending = 0 THEN CASE @SortBy WHEN 'code' THEN Code WHEN 'fullName' THEN FullName WHEN 'countryName' THEN CountryName
                                                        WHEN 'customerType' THEN CustomerType WHEN 'portOfDischarge' THEN PortOfDischarge
                                                        WHEN 'unTaggedBy' THEN UnTaggedBy END END ASC,
        CASE WHEN @SortDescending = 1 THEN CASE @SortBy WHEN 'code' THEN Code WHEN 'fullName' THEN FullName WHEN 'countryName' THEN CountryName
                                                        WHEN 'customerType' THEN CustomerType WHEN 'portOfDischarge' THEN PortOfDischarge
                                                        WHEN 'unTaggedBy' THEN UnTaggedBy END END DESC,
        CASE WHEN @SortDescending = 0 THEN CASE @SortBy WHEN 'modifiedAt' THEN ModifiedAt WHEN 'unTaggedDate' THEN UnTaggedDate END END ASC,
        CASE WHEN @SortDescending = 1 THEN CASE @SortBy WHEN 'modifiedAt' THEN ModifiedAt WHEN 'unTaggedDate' THEN UnTaggedDate END END DESC,
        CASE WHEN @SortDescending = 0 AND @SortBy = 'numberOfTimesTagged' THEN NumberOfTimesTagged END ASC,
        CASE WHEN @SortDescending = 1 AND @SortBy = 'numberOfTimesTagged' THEN NumberOfTimesTagged END DESC,
        SortCreatedBy DESC, UserProfileId DESC, SortTagId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;

    SELECT COUNT(*) AS TotalRecords FROM #Rows;
END
GO

-- =====================================================================================================================
-- CustomerTagging_AvailableCustomers_V2 - every customer-role party with no active tag. No caller-based visibility
-- (same as legacy). Sort keys: code, fullName, countryName, customerType, portOfDischarge, modifiedAt.
-- =====================================================================================================================
CREATE OR ALTER PROCEDURE dbo.CustomerTagging_AvailableCustomers_V2
    @Search         NVARCHAR(200) = NULL,
    @SortBy         NVARCHAR(50)  = NULL,
    @SortDescending BIT           = 0,
    @PageNumber     INT           = 1,
    @PageSize       INT           = 10,
    @CreatedBy      BIGINT,
    @CreatedAt      DATETIME,
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

    SELECT UP.UserProfileId, UP.Code, UP.FullName, UP.Email, UP.CountryId,
           BCTRY.Base_Country_Name AS CountryName, BCT.Base_City_Name AS CityName, BG.GenderName, BS.UserStatusName,
           ISNULL(UPD.Phone, 'N/A') AS Phone,
           ISNULL(UPD.SecondaryPhone, 'N/A') AS SecondaryPhone,
           ISNULL(UPD.WhatsAppNumber, 'N/A') AS WhatsAppNumber,
           ISNULL(BCU.CustomerTypeName, 'N/A') AS CustomerType,
           ISNULL(BSS.SourceName, 'N/A') AS SourceName,
           ISNULL(BPD.Base_DischargePort_Name, 'N/A') AS PortOfDischarge,
           UP.PartyKind,
           ISNULL(UP.ModifiedAt, UP.CreatedAt) AS ModifiedAt,
           UP.CreatedBy AS SortCreatedBy
    INTO #Rows
    FROM dbo.UserProfile UP
    LEFT JOIN dbo.UserProfileDetail UPD ON UPD.UserProfileId = UP.UserProfileId
    LEFT JOIN dbo.Base_CustomerType BCU ON BCU.CustomerTypeId = UPD.CustomerTypeId
    LEFT JOIN dbo.Base_Source BSS ON BSS.SourceId = UPD.SourceId
    LEFT JOIN dbo.Base_DischargePort BPD ON BPD.Base_DischargePort_Id = UPD.PortOfDischargeId
    INNER JOIN dbo.Role R ON UP.RoleId = R.RoleId AND R.UserTypeId = 3
    LEFT JOIN dbo.Base_Gender BG ON UP.GenderId = BG.GenderId
    LEFT JOIN dbo.Base_UserStatus BS ON UP.StatusId = BS.UserStatusId
    LEFT JOIN dbo.Base_Country BCTRY ON UP.CountryId = BCTRY.Base_Country_Id
    LEFT JOIN dbo.Base_City BCT ON UP.CityId = BCT.Base_City_Id
    WHERE UP.IsDeleted = 0
      AND UP.UserProfileId NOT IN (SELECT CT.CustomerId FROM dbo.CustomerTagging CT WHERE CT.IsActive = 1 AND CT.IsDeleted = 0)
      AND (@Like IS NULL
           OR UP.Code LIKE @Like OR UP.FullName LIKE @Like OR UP.Email LIKE @Like
           OR UPD.Phone LIKE @Like OR UPD.WhatsAppNumber LIKE @Like OR BCTRY.Base_Country_Name LIKE @Like
           OR BCU.CustomerTypeName LIKE @Like OR BPD.Base_DischargePort_Name LIKE @Like)
    OPTION (RECOMPILE);

    SELECT UserProfileId, Code, FullName, Email, CountryId, CountryName, CityName, GenderName, UserStatusName,
           Phone, SecondaryPhone, WhatsAppNumber, CustomerType, SourceName, PortOfDischarge, PartyKind, ModifiedAt
    FROM #Rows
    ORDER BY
        CASE WHEN @SortDescending = 0 THEN CASE @SortBy WHEN 'code' THEN Code WHEN 'fullName' THEN FullName WHEN 'countryName' THEN CountryName
                                                        WHEN 'customerType' THEN CustomerType WHEN 'portOfDischarge' THEN PortOfDischarge END END ASC,
        CASE WHEN @SortDescending = 1 THEN CASE @SortBy WHEN 'code' THEN Code WHEN 'fullName' THEN FullName WHEN 'countryName' THEN CountryName
                                                        WHEN 'customerType' THEN CustomerType WHEN 'portOfDischarge' THEN PortOfDischarge END END DESC,
        CASE WHEN @SortDescending = 0 AND @SortBy = 'modifiedAt' THEN ModifiedAt END ASC,
        CASE WHEN @SortDescending = 1 AND @SortBy = 'modifiedAt' THEN ModifiedAt END DESC,
        SortCreatedBy DESC, UserProfileId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;

    SELECT COUNT(*) AS TotalRecords FROM #Rows;
END
GO

SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    016_UserPhone_CountryCode.sql - staff phone and WhatsApp kept as country code + number, separately.

    Staff users had one number, the "Assigned Official Whatsapp" (UserProfileDetail.WhatsAppNumber), saved without a
    country code, and no phone. UserProfile.CountryCode exists but nothing ever filled it. From this script on, each
    number is stored as two parts:

        number        UserProfileDetail.Phone / UserProfileDetail.WhatsAppNumber   (existing columns, digits only)
        country code  UserProfileDetail.PhoneCountryCodeId / WhatsAppCountryCodeId  (new, -> Base_CountryCodes)

    The code is an id, not the dial text: one dial code is shared by several countries (+1 = USA, Jamaica, ...), and the
    id keeps which one was picked. Reads return both the id and the dial text (Base_CountryCode_Value, e.g. '+92').
    Customer numbers are NOT changed - they stay combined ('+18765722459') and the duplicate-phone checks rely on that.

    Contents (all additive):
      1. UserProfileDetail.PhoneCountryCodeId, .WhatsAppCountryCodeId (BIGINT NULL, FK -> Base_CountryCodes).
      2. View vw_CountryDialCode (Base_CountryCode_Id, DialCode, CountryName, DisplayName 'Pakistan (+92)') - the
         non-deleted codes - and its SYS_DropDownsWithAuth row, so GET /api/lookups?tables=vw_CountryDialCode serves
         the dropdown.
      3. UserSave_V2 redefined: 012's body + @Phone, @PhoneCountryCodeId, @WhatsAppCountryCodeId, written by both
         UserProfileDetail inserts. Supersedes 012 (run 012 then 016; 016 alone also works - CREATE OR ALTER).
      4. UserGetById_V2: legacy UserGetById + Phone and the two codes (id + dial text). Legacy UserGetById untouched.
      5. UserGetAll_V2 redefined: 010's body + the same four code columns. Supersedes 010's UserGetAll_V2.

    Coexistence: the legacy UserSave (legacy employee edit form) deletes and re-inserts UserProfileDetail without these
    columns, so editing a staff user in the legacy app clears the phone and both codes (it already cleared Phone).

    Idempotent. Apply to every tenant database.
*/

-- 1. Columns ------------------------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.UserProfileDetail', 'PhoneCountryCodeId') IS NULL
    ALTER TABLE dbo.UserProfileDetail ADD PhoneCountryCodeId BIGINT NULL;
GO
IF COL_LENGTH('dbo.UserProfileDetail', 'WhatsAppCountryCodeId') IS NULL
    ALTER TABLE dbo.UserProfileDetail ADD WhatsAppCountryCodeId BIGINT NULL;
GO
IF OBJECT_ID('dbo.FK_UserProfileDetail_PhoneCountryCode', 'F') IS NULL
    ALTER TABLE dbo.UserProfileDetail ADD CONSTRAINT FK_UserProfileDetail_PhoneCountryCode
        FOREIGN KEY (PhoneCountryCodeId) REFERENCES dbo.Base_CountryCodes (Base_CountryCode_Id);
GO
IF OBJECT_ID('dbo.FK_UserProfileDetail_WhatsAppCountryCode', 'F') IS NULL
    ALTER TABLE dbo.UserProfileDetail ADD CONSTRAINT FK_UserProfileDetail_WhatsAppCountryCode
        FOREIGN KEY (WhatsAppCountryCodeId) REFERENCES dbo.Base_CountryCodes (Base_CountryCode_Id);
GO

-- 2. Dropdown source -----------------------------------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.vw_CountryDialCode
AS
SELECT CC.Base_CountryCode_Id,
       CC.Base_CountryCode_Value AS DialCode,
       C.Base_Country_Name      AS CountryName,
       CAST(C.Base_Country_Name + N' (' + CC.Base_CountryCode_Value + N')' AS NVARCHAR(400)) AS DisplayName
FROM dbo.Base_CountryCodes CC
INNER JOIN dbo.Base_Country C ON C.Base_Country_Id = CC.FK_CountryId
WHERE ISNULL(CC.IsDeleted, 0) = 0
  AND ISNULL(C.IsDeleted, 0) = 0
  AND NULLIF(LTRIM(CC.Base_CountryCode_Value), N'') IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM dbo.SYS_DropDownsWithAuth WHERE TableName = 'vw_CountryDialCode')
    INSERT INTO dbo.SYS_DropDownsWithAuth (TableName, TableId, TableValue)
    VALUES ('vw_CountryDialCode', 'Base_CountryCode_Id', 'DisplayName');
GO

-- 3. Save ----------------------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.UserSave_V2
    @UserProfileId            BIGINT,
    @Code                     NVARCHAR(50),
    @FullName                 NVARCHAR(MAX),
    @Username                 NVARCHAR(MAX),
    @Email                    NVARCHAR(MAX),
    @URL                      NVARCHAR(MAX),
    @AspNetUserId             NVARCHAR(450),
    @RoleId                   BIGINT,
    @GenderId                 INT,
    @CustomerTagLimit         NVARCHAR(MAX),
    @Address                  NVARCHAR(MAX) = NULL,
    @CountryCode              NVARCHAR(10)  = NULL,
    @CountryId                INT           = NULL,
    @CityId                   INT           = NULL,
    @ReportsTo_UserId         BIGINT        = NULL,
    @OfficeLocation_CountryId BIGINT        = NULL,
    @OfficialWhatsapp         VARCHAR(200)  = NULL,
    @RemoteAccessAllowed      BIT           = 0,
    @StatusId                 INT,
    @Phone                    NVARCHAR(400) = NULL,   -- 016
    @PhoneCountryCodeId       BIGINT        = NULL,   -- 016
    @WhatsAppCountryCodeId    BIGINT        = NULL,   -- 016
    @CreatedBy                BIGINT,
    @CreatedAt                DATETIME,
    @CompanyId                BIGINT        = NULL
AS
BEGIN
    IF (@UserProfileId > 0)
    BEGIN
        IF @CreatedBy = 1
        BEGIN
            UPDATE UserProfile
            SET RemoteAccessAllowed = @RemoteAccessAllowed
            WHERE UserProfileId = @UserProfileId
        END
        UPDATE UserProfile
        SET FullName = @FullName,
            Username = ISNULL(@Username, Username),   -- 012
            URL = @URL,
            StatusId = @StatusId,
            RoleId = @RoleId,
            GenderId = @GenderId,
            [Address] = @Address,
            CountryCode = @CountryCode,
            CountryId = @CountryId,
            CityId = @CityId,
            ReportsTo_UserId = @ReportsTo_UserId,
            ModifiedBy = @CreatedBy,
            ModifiedAt = @CreatedAt
        WHERE UserProfileId = @UserProfileId
    END
    ELSE
    BEGIN
        INSERT INTO UserProfile (Code, FullName, Username, Email, [URL], [StatusId], CreatedBy, CreatedAt, IsDeleted, CompanyId, AspNetUserId, RoleId, GenderId, [Address], CountryCode, CountryId, CityId, ReportsTo_UserId, RemoteAccessAllowed)
        VALUES (@Code, @FullName, @Username, @Email, @URL, @StatusId, @CreatedBy, @CreatedAt, 0, @CompanyId, @AspNetUserId, @RoleId, @GenderId, @Address, @CountryCode, @CountryId, @CityId, @ReportsTo_UserId, @RemoteAccessAllowed)
        DECLARE @NewUserProfileId INT = SCOPE_IDENTITY()

        INSERT INTO UserProfileDetail (UserProfileId, CustomerTagLimit, OfficeLocation_CountryId, WhatsAppNumber, Phone, PhoneCountryCodeId, WhatsAppCountryCodeId)
        VALUES (@NewUserProfileId, CONVERT(INT, @CustomerTagLimit), @OfficeLocation_CountryId, @OfficialWhatsapp, @Phone, @PhoneCountryCodeId, @WhatsAppCountryCodeId)
    END

    DELETE UserProfileDetail
    WHERE UserProfileId = @UserProfileId

    INSERT INTO UserProfileDetail (UserProfileId, CustomerTagLimit, OfficeLocation_CountryId, WhatsAppNumber, Phone, PhoneCountryCodeId, WhatsAppCountryCodeId)
    VALUES (@UserProfileId, CONVERT(INT, @CustomerTagLimit), @OfficeLocation_CountryId, @OfficialWhatsapp, @Phone, @PhoneCountryCodeId, @WhatsAppCountryCodeId)
END
GO

-- 4. Detail --------------------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.UserGetById_V2
    @UserProfileId BIGINT,
    @CreatedBy     BIGINT,
    @CreatedAt     DATETIME,
    @CompanyId     BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT UP.*, BUS.UserStatusName AS UserStatusName,
           ISNULL(CONVERT(NVARCHAR(MAX), UPD.CustomerTagLimit), '') AS CustomerTagLimit,
           UPD.OfficeLocation_CountryId AS OfficeLocation_CountryId,
           UPD.WhatsAppNumber AS WhatsAppNumber,
           UPD.Phone AS Phone,                                       -- 016 from here
           UPD.PhoneCountryCodeId AS PhoneCountryCodeId,
           PCC.Base_CountryCode_Value AS PhoneCountryCode,
           UPD.WhatsAppCountryCodeId AS WhatsAppCountryCodeId,
           WCC.Base_CountryCode_Value AS WhatsAppCountryCode
    FROM UserProfile UP
    LEFT JOIN UserProfileDetail UPD ON UPD.UserProfileId = UP.UserProfileId
    LEFT JOIN Base_CountryCodes PCC ON PCC.Base_CountryCode_Id = UPD.PhoneCountryCodeId
    LEFT JOIN Base_CountryCodes WCC ON WCC.Base_CountryCode_Id = UPD.WhatsAppCountryCodeId
    INNER JOIN Base_UserStatus BUS ON UP.StatusId = BUS.UserStatusId
    WHERE UP.UserProfileId = @UserProfileId
END
GO

-- 5. List (010's UserGetAll_V2 + the four code columns) ---------------------------------------------------------------
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
           UPD.PhoneCountryCodeId, PCC.Base_CountryCode_Value AS PhoneCountryCode,          -- 016
           UPD.WhatsAppCountryCodeId, WCC.Base_CountryCode_Value AS WhatsAppCountryCode,    -- 016
           UP.CreatedAt,
           ISNULL(UP.ModifiedAt, UP.CreatedAt) AS ModifiedAt,
           UP.CreatedBy AS SortCreatedBy
    INTO #Rows
    FROM dbo.UserProfile UP
    LEFT JOIN dbo.UserProfileDetail UPD ON UPD.UserProfileId = UP.UserProfileId
    LEFT JOIN dbo.Base_CountryCodes PCC ON PCC.Base_CountryCode_Id = UPD.PhoneCountryCodeId
    LEFT JOIN dbo.Base_CountryCodes WCC ON WCC.Base_CountryCode_Id = UPD.WhatsAppCountryCodeId
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
           PhoneCountryCodeId, PhoneCountryCode, WhatsAppCountryCodeId, WhatsAppCountryCode,
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

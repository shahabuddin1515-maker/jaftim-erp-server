SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    012_UserSave_V2.sql - staff create/update (POST/PUT /api/users) with an editable Username.

    Copy of the legacy UserSave (definition read from the local UAT copy, 2026-11-19) with ONE change: the update
    branch also writes Username. Legacy UserSave stored @Username on insert only, so the username typed on the edit
    form (UserGetById.cshtml) was silently dropped. A NULL @Username leaves the stored value as it is.

    Everything else is verbatim, including the two documented quirks (see UserRepository.SaveAsync): every INSERT
    also leaves a UserProfileDetail row with UserProfileId = 0, and only @CreatedBy = 1 may change
    RemoteAccessAllowed. The legacy UserSave is untouched - the legacy app keeps calling it.

    Idempotent (CREATE OR ALTER). Apply to every tenant database.
*/
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
            Username = ISNULL(@Username, Username),   -- the v2 change
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

        INSERT INTO UserProfileDetail (UserProfileId, CustomerTagLimit, OfficeLocation_CountryId, WhatsAppNumber)
        VALUES (@NewUserProfileId, CONVERT(INT, @CustomerTagLimit), @OfficeLocation_CountryId, @OfficialWhatsapp)
    END

    DELETE UserProfileDetail
    WHERE UserProfileId = @UserProfileId

    INSERT INTO UserProfileDetail (UserProfileId, CustomerTagLimit, OfficeLocation_CountryId, WhatsAppNumber)
    VALUES (@UserProfileId, CONVERT(INT, @CustomerTagLimit), @OfficeLocation_CountryId, @OfficialWhatsapp)
END
GO

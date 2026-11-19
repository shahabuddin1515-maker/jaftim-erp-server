SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    013_Lookup_OfficeLocations.sql - GET /api/lookups/office-locations.

    The company's office-location countries: Base_Country rows flagged IsLocationCountry = 1 (the same flag the
    public website's web_load_all uses for its location list), live and active. 4 rows on the local UAT copy
    (2026-11-19): 9 Australia, 85 Japan, 185 United Arab Emirates, 186 United Kingdom. IsLocationCountry is NULL,
    not 0, on every other row.

    NOT the id space of UserProfileDetail.OfficeLocation_CountryId: that column holds Base_OriginCountry ids (the
    legacy "Office" dropdown and RPT_Lookup_OfficeLocations read Base_OriginCountry; docs/ERD.md). Owner decision
    2026-11-19: this lookup reads Base_Country as specified - see docs/USERS.md "Office location".

    Read-only, no table change. Idempotent (CREATE OR ALTER). Apply to every tenant database.
*/
CREATE OR ALTER PROCEDURE dbo.Lookup_OfficeLocations
    @CreatedBy BIGINT   = NULL,
    @CreatedAt DATETIME = NULL,
    @CompanyId BIGINT   = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT C.Base_Country_Id   AS Id,
           C.Base_Country_Name AS Name,
           C.Alpha2Code
    FROM dbo.Base_Country C
    WHERE C.IsLocationCountry = 1
      AND ISNULL(C.IsDeleted, 0) = 0
      AND ISNULL(C.Base_Country_IsActive, 1) = 1
    ORDER BY C.Base_Country_Name, C.Base_Country_Id;
END
GO

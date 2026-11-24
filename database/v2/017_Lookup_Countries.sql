SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    017_Lookup_Countries.sql - GET /api/lookups/countries.

    Every live, active Base_Country (208 of 211 on the local UAT copy, 2026-11-24 - the 3 inactive rows are
    "Test Country 1-3"), sorted by name, with its ISO alpha-2 code and its dial code. Each country has at most one
    Base_CountryCodes row (checked 2026-11-24), so the LEFT JOIN cannot duplicate a country; 4 countries have none
    (the 4 test rows) and return NULLs. DialCodeId is the id the user endpoints take as phoneCountryCodeId /
    whatsAppCountryCodeId (database/v2/016), so one list can feed the country and the phone pickers.

    GET /api/lookups?tables=Base_Country (legacy GetDllAuthTableValues) is unchanged and still returns all 211 rows.

    Read-only, no table change. Run after 016 (no dependency on it, numbering only). Idempotent (CREATE OR ALTER).
    Apply to every tenant database.
*/
CREATE OR ALTER PROCEDURE dbo.Lookup_Countries
    @CreatedBy BIGINT   = NULL,
    @CreatedAt DATETIME = NULL,
    @CompanyId BIGINT   = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT C.Base_Country_Id         AS Id,
           LTRIM(RTRIM(C.Base_Country_Name)) AS Name,   -- stored names can carry spaces (' Lebanon')
           C.Alpha2Code,
           CC.Base_CountryCode_Id    AS DialCodeId,
           CC.Base_CountryCode_Value AS DialCode
    FROM dbo.Base_Country C
    LEFT JOIN dbo.Base_CountryCodes CC
           ON CC.FK_CountryId = C.Base_Country_Id
          AND ISNULL(CC.IsDeleted, 0) = 0
          AND NULLIF(LTRIM(CC.Base_CountryCode_Value), N'') IS NOT NULL
    WHERE ISNULL(C.IsDeleted, 0) = 0
      AND ISNULL(C.Base_Country_IsActive, 1) = 1
    ORDER BY LTRIM(RTRIM(C.Base_Country_Name)), C.Base_Country_Id;
END
GO

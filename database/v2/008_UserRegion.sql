SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    v2/008 - UserRegion_Get: the region tree and one user's region assignment, in one read.

    Additive only: one new read-only procedure. Apply to EVERY tenant database. Idempotent (CREATE OR ALTER).

    The region model (legacy, unchanged - docs/USERS.md "Regions"):
        EntityType 1 = Division, 2 = Group;  Entity: Division -> Group (ParentEntityId);  Base_Country.EntityId -> Group
        UserEntities  = the divisions/groups ticked on the assignment screen (screen state only)
        UserCountries = the granted countries - the ONLY input of fn_GetUserAccessibleCountries
    Saving stays on the legacy AssignEntitiesToUser; the API derives both lists before calling it.

    Why a new procedure instead of the legacy GetEntityHierarchyForUserAssignment: that one returns no country
    active flag (the access function ignores inactive countries, so the API must know which they are), filters
    IsDeleted = 0 without ISNULL, and mixes nodes and assignment into one shape. This one returns raw rows.

    Result sets:
        1  entities   EntityId, EntityName, EntityTypeId, ParentEntityId          (not deleted)
        2  countries  CountryId, CountryName, IsActive, GroupEntityId             (not deleted; GroupEntityId may be NULL)
        3  EntityId of every UserEntities row of the user
        4  CountryId of every UserCountries row of the user
*/
CREATE OR ALTER PROCEDURE dbo.UserRegion_Get
    @UserProfileId BIGINT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT e.EntityId, e.EntityName, e.EntityTypeId, e.ParentEntityId
    FROM dbo.Entity e
    WHERE ISNULL(e.IsDeleted, 0) = 0
    ORDER BY e.EntityTypeId, e.EntityName;

    SELECT bc.Base_Country_Id AS CountryId,
           bc.Base_Country_Name AS CountryName,
           CAST(ISNULL(bc.Base_Country_IsActive, 0) AS BIT) AS IsActive,
           bc.EntityId AS GroupEntityId
    FROM dbo.Base_Country bc
    WHERE ISNULL(bc.IsDeleted, 0) = 0
    ORDER BY bc.Base_Country_Name;

    SELECT CAST(ue.EntityId AS BIGINT) AS EntityId
    FROM dbo.UserEntities ue
    WHERE ue.UserId = @UserProfileId;

    SELECT CAST(uc.CountryId AS BIGINT) AS CountryId
    FROM dbo.UserCountries uc
    WHERE uc.UserId = @UserProfileId;
END
GO

SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    v2/009 - Region administration: create / rename / move / delete divisions and groups, move a country to a group.

    Additive only: four new procedures. The legacy tables (Entity, Base_Country, UserEntities) get no schema change -
    owner decision 2026-10-05 (docs/USERS.md): the API validates, and each procedure re-checks its own rules under
    UPDLOCK/HOLDLOCK so two admins cannot race past the checks. Apply to EVERY tenant database. Idempotent.

    QUOTED_IDENTIFIER ON matters here beyond the usual rule: Entity carries the filtered index
    IX_Entity_ParentEntityId_Type, so a writer compiled with it OFF would fail at runtime. These are the first
    procedures that write Entity at all.

    Rule errors are THROW 50000 (surfaced by the API as 422).

    Entity.ModifiedBy is DATETIME in the deployed table (docs/USERS.md defect 3), so it cannot hold a user id and is
    left untouched; ModifiedAt is set. The actor is in the API's AuditLog row.

        Region_SaveEntity            insert (@EntityId NULL) or rename / re-parent a division or group
        Region_DeleteEntity          soft-delete an empty division (no groups) or group (no countries)
        Region_MoveCountry           Base_Country.EntityId := a live group
        UserRegion_RederiveTicks     internal: UserEntities := ancestors of UserCountries, for the users holding a
                                     country (or any country of a group). Keeps every legacy screen consistent after a
                                     move: a country whose group row is missing is dropped by that screen's next save.
*/

CREATE OR ALTER PROCEDURE dbo.UserRegion_RederiveTicks
    @CountryId     BIGINT = NULL,
    @GroupEntityId BIGINT = NULL,
    @CreatedAt     DATETIME
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @users TABLE (UserId INT PRIMARY KEY);
    INSERT INTO @users (UserId)
    SELECT DISTINCT uc.UserId
    FROM dbo.UserCountries uc
    JOIN dbo.Base_Country bc ON bc.Base_Country_Id = uc.CountryId
    WHERE (@CountryId IS NOT NULL AND uc.CountryId = @CountryId)
       OR (@GroupEntityId IS NOT NULL AND bc.EntityId = @GroupEntityId);

    DELETE ue FROM dbo.UserEntities ue JOIN @users u ON u.UserId = ue.UserId;

    INSERT INTO dbo.UserEntities (UserId, EntityId, CreatedDate)
    SELECT DISTINCT uc.UserId, CAST(x.EntityId AS INT), @CreatedAt
    FROM dbo.UserCountries uc
    JOIN @users u ON u.UserId = uc.UserId
    JOIN dbo.Base_Country bc ON bc.Base_Country_Id = uc.CountryId AND ISNULL(bc.IsDeleted, 0) = 0
    JOIN dbo.Entity g ON g.EntityId = bc.EntityId AND g.EntityTypeId = 2 AND ISNULL(g.IsDeleted, 0) = 0
    JOIN dbo.Entity d ON d.EntityId = g.ParentEntityId AND d.EntityTypeId = 1 AND d.ParentEntityId IS NULL AND ISNULL(d.IsDeleted, 0) = 0
    CROSS APPLY (VALUES (g.EntityId), (d.EntityId)) x(EntityId);

    SELECT COUNT(*) AS AffectedUsers FROM @users;
END
GO

CREATE OR ALTER PROCEDURE dbo.Region_SaveEntity
    @EntityId       BIGINT = NULL,          -- NULL = create
    @EntityName     NVARCHAR(200),
    @EntityTypeId   INT,                    -- 1 Division, 2 Group
    @ParentEntityId BIGINT = NULL,          -- the division, for a group
    @CreatedBy      BIGINT,
    @CreatedAt      DATETIME,
    @CompanyId      BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @name NVARCHAR(200) = LTRIM(RTRIM(@EntityName));
    DECLARE @oldParent BIGINT;

    BEGIN TRANSACTION;

    IF @name IS NULL OR @name = N''
        THROW 50000, N'A name is required.', 1;
    IF @EntityTypeId NOT IN (1, 2)
        THROW 50000, N'Unknown region type.', 1;
    IF @EntityTypeId = 1 AND @ParentEntityId IS NOT NULL
        THROW 50000, N'A division cannot have a parent.', 1;
    IF @EntityTypeId = 2 AND NOT EXISTS (SELECT 1 FROM dbo.Entity WITH (UPDLOCK, HOLDLOCK)
                                         WHERE EntityId = @ParentEntityId AND EntityTypeId = 1
                                           AND ParentEntityId IS NULL AND ISNULL(IsDeleted, 0) = 0)
        THROW 50000, N'The division does not exist.', 1;

    IF @EntityId IS NOT NULL
    BEGIN
        SELECT @oldParent = ParentEntityId FROM dbo.Entity WITH (UPDLOCK, HOLDLOCK)
        WHERE EntityId = @EntityId AND EntityTypeId = @EntityTypeId AND ISNULL(IsDeleted, 0) = 0;
        IF @@ROWCOUNT = 0
            THROW 50000, N'The region does not exist.', 1;
    END

    IF EXISTS (SELECT 1 FROM dbo.Entity WITH (UPDLOCK, HOLDLOCK)
               WHERE EntityTypeId = @EntityTypeId AND ISNULL(IsDeleted, 0) = 0
                 AND LOWER(LTRIM(RTRIM(EntityName))) = LOWER(@name)
                 AND (@EntityId IS NULL OR EntityId <> @EntityId))
        THROW 50000, N'Another region of this type already has that name.', 1;

    IF @EntityId IS NULL
    BEGIN
        INSERT INTO dbo.Entity (EntityName, EntityTypeId, ParentEntityId, CreatedBy, CreatedAt, IsDeleted)
        VALUES (@name, @EntityTypeId, @ParentEntityId, @CreatedBy, @CreatedAt, 0);
        SET @EntityId = CAST(SCOPE_IDENTITY() AS BIGINT);
    END
    ELSE
    BEGIN
        UPDATE dbo.Entity
           SET EntityName = @name,
               ParentEntityId = @ParentEntityId,
               ModifiedAt = @CreatedAt
         WHERE EntityId = @EntityId;

        -- A group moved to another division: its holders' division ticks are now wrong.
        IF @EntityTypeId = 2 AND ISNULL(@oldParent, -1) <> @ParentEntityId
        BEGIN
            DECLARE @affected TABLE (AffectedUsers INT);
            INSERT INTO @affected EXEC dbo.UserRegion_RederiveTicks @GroupEntityId = @EntityId, @CreatedAt = @CreatedAt;
        END
    END

    COMMIT TRANSACTION;
    SELECT @EntityId AS EntityId;
END
GO

CREATE OR ALTER PROCEDURE dbo.Region_DeleteEntity
    @EntityId  BIGINT,
    @CreatedBy BIGINT,
    @CreatedAt DATETIME,
    @CompanyId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @type INT;
    BEGIN TRANSACTION;

    SELECT @type = EntityTypeId FROM dbo.Entity WITH (UPDLOCK, HOLDLOCK)
    WHERE EntityId = @EntityId AND ISNULL(IsDeleted, 0) = 0;
    IF @type IS NULL
        THROW 50000, N'The region does not exist.', 1;

    IF @type = 1 AND EXISTS (SELECT 1 FROM dbo.Entity WITH (UPDLOCK, HOLDLOCK)
                             WHERE ParentEntityId = @EntityId AND ISNULL(IsDeleted, 0) = 0)
        THROW 50000, N'The division still has groups; move or delete them first.', 1;
    IF @type = 2 AND EXISTS (SELECT 1 FROM dbo.Base_Country WITH (UPDLOCK, HOLDLOCK)
                             WHERE EntityId = @EntityId AND ISNULL(IsDeleted, 0) = 0)
        THROW 50000, N'The group still has countries; move them to another group first.', 1;

    UPDATE dbo.Entity SET IsDeleted = 1, ModifiedAt = @CreatedAt WHERE EntityId = @EntityId;
    -- An empty region can only be screen state in UserEntities (it holds no countries) - drop it.
    DELETE FROM dbo.UserEntities WHERE EntityId = @EntityId;

    COMMIT TRANSACTION;
    SELECT @type AS EntityTypeId;
END
GO

CREATE OR ALTER PROCEDURE dbo.Region_MoveCountry
    @CountryId     BIGINT,
    @GroupEntityId BIGINT,
    @CreatedBy     BIGINT,
    @CreatedAt     DATETIME,
    @CompanyId     BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM dbo.Base_Country WITH (UPDLOCK, HOLDLOCK)
                   WHERE Base_Country_Id = @CountryId AND ISNULL(IsDeleted, 0) = 0)
        THROW 50000, N'The country does not exist.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.Entity g WITH (UPDLOCK, HOLDLOCK)
                   JOIN dbo.Entity d WITH (UPDLOCK, HOLDLOCK) ON d.EntityId = g.ParentEntityId
                   WHERE g.EntityId = @GroupEntityId AND g.EntityTypeId = 2 AND ISNULL(g.IsDeleted, 0) = 0
                     AND d.EntityTypeId = 1 AND d.ParentEntityId IS NULL AND ISNULL(d.IsDeleted, 0) = 0)
        THROW 50000, N'The group does not exist or is not under a division.', 1;

    UPDATE dbo.Base_Country
       SET EntityId = @GroupEntityId, ModifiedBy = @CreatedBy, ModifiedAt = @CreatedAt
     WHERE Base_Country_Id = @CountryId;

    DECLARE @affected TABLE (AffectedUsers INT);
    INSERT INTO @affected EXEC dbo.UserRegion_RederiveTicks @CountryId = @CountryId, @CreatedAt = @CreatedAt;

    COMMIT TRANSACTION;
    SELECT AffectedUsers FROM @affected;
END
GO

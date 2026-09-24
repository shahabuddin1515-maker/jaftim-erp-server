/*
    Catalog database (shared across tenants). Holds identity, tenant registry, sessions and auth audit.
    Business data lives in one database per tenant (a copy of the legacy schema, untouched).

    Idempotent. Apply to: local  -> jaftim-local-catalog
                          UAT    -> jaftim-uat-catalog   (release step, with authorisation)
                          Live   -> jaftim-live-catalog  (release step, with authorisation)

    Standard v2 audit columns (every new table): CreatedAtUtc, CreatedBy, ModifiedAtUtc, ModifiedBy,
    IsDeleted (bit), DeletedAtUtc, DeletedBy, RowVersion. CreatedBy/ModifiedBy are UserProfileIds of the acting
    tenant user, or NULL for system/anonymous actions.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ---------------------------------------------------------------- Tenant
IF OBJECT_ID('dbo.Tenant', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Tenant
    (
        TenantId         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Tenant PRIMARY KEY CLUSTERED,
        Code             NVARCHAR(50)   NOT NULL,
        Name             NVARCHAR(200)  NOT NULL,
        -- Full connection string of the tenant database, or a Key Vault secret reference ("kv:<secret-name>")
        -- resolved by ITenantConnectionResolver in UAT/Live.
        ConnectionString NVARCHAR(MAX)  NOT NULL,
        IsActive         BIT            NOT NULL CONSTRAINT DF_Tenant_IsActive DEFAULT (1),
        CreatedAtUtc     DATETIME2(3)   NOT NULL CONSTRAINT DF_Tenant_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy        BIGINT         NULL,
        ModifiedAtUtc    DATETIME2(3)   NULL,
        ModifiedBy       BIGINT         NULL,
        IsDeleted        BIT            NOT NULL CONSTRAINT DF_Tenant_IsDeleted DEFAULT (0),
        DeletedAtUtc     DATETIME2(3)   NULL,
        DeletedBy        BIGINT         NULL,
        RowVersion       ROWVERSION     NOT NULL
    );
    CREATE UNIQUE INDEX UX_Tenant_Code ON dbo.Tenant (Code);
END
GO

-- ---------------------------------------------------------------- Account (credentials, shared)
IF OBJECT_ID('dbo.Account', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Account
    (
        -- Equals the tenant AspNetUsers.Id the account was first seeded from, so the link is stable and the
        -- legacy app keeps working against the same identity during coexistence.
        AccountId         NVARCHAR(450)  NOT NULL CONSTRAINT PK_Account PRIMARY KEY CLUSTERED,
        Email             NVARCHAR(256)  NOT NULL,
        NormalizedEmail   NVARCHAR(256)  NOT NULL,
        PasswordHash      NVARCHAR(MAX)  NULL,      -- ASP.NET Identity V3 format (IdentityCompatiblePasswordHasher)
        LockoutEnabled    BIT            NOT NULL CONSTRAINT DF_Account_LockoutEnabled DEFAULT (1),
        LockoutEnd        DATETIMEOFFSET NULL,
        AccessFailedCount INT            NOT NULL CONSTRAINT DF_Account_AccessFailedCount DEFAULT (0),
        TokenVersion      INT            NOT NULL CONSTRAINT DF_Account_TokenVersion DEFAULT (1),
        -- Set the first time the API changes the password. From then on the catalog hash is authoritative and
        -- AccountSyncJob mirrors catalog -> tenant; before that, tenant -> catalog (legacy app still owns passwords).
        PasswordChangedByApiAtUtc DATETIME2(3) NULL,
        IsActive          BIT            NOT NULL CONSTRAINT DF_Account_IsActive DEFAULT (1),
        CreatedAtUtc      DATETIME2(3)   NOT NULL CONSTRAINT DF_Account_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy         BIGINT         NULL,
        ModifiedAtUtc     DATETIME2(3)   NULL,
        ModifiedBy        BIGINT         NULL,
        IsDeleted         BIT            NOT NULL CONSTRAINT DF_Account_IsDeleted DEFAULT (0),
        DeletedAtUtc      DATETIME2(3)   NULL,
        DeletedBy         BIGINT         NULL,
        RowVersion        ROWVERSION     NOT NULL
    );
    CREATE UNIQUE INDEX UX_Account_NormalizedEmail ON dbo.Account (NormalizedEmail);
END
GO
IF COL_LENGTH('dbo.Account', 'PasswordChangedByApiAtUtc') IS NULL
    ALTER TABLE dbo.Account ADD PasswordChangedByApiAtUtc DATETIME2(3) NULL;
GO

-- ---------------------------------------------------------------- AccountTenant (membership)
IF OBJECT_ID('dbo.AccountTenant', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountTenant
    (
        AccountTenantId  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountTenant PRIMARY KEY CLUSTERED,
        AccountId        NVARCHAR(450)  NOT NULL CONSTRAINT FK_AccountTenant_Account REFERENCES dbo.Account (AccountId),
        TenantId         INT            NOT NULL CONSTRAINT FK_AccountTenant_Tenant REFERENCES dbo.Tenant (TenantId),
        UserProfileId    BIGINT         NOT NULL,   -- UserProfile.UserProfileId inside that tenant database
        IsDefault        BIT            NOT NULL CONSTRAINT DF_AccountTenant_IsDefault DEFAULT (0),
        IsActive         BIT            NOT NULL CONSTRAINT DF_AccountTenant_IsActive DEFAULT (1),
        CreatedAtUtc     DATETIME2(3)   NOT NULL CONSTRAINT DF_AccountTenant_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy        BIGINT         NULL,
        ModifiedAtUtc    DATETIME2(3)   NULL,
        ModifiedBy       BIGINT         NULL,
        IsDeleted        BIT            NOT NULL CONSTRAINT DF_AccountTenant_IsDeleted DEFAULT (0),
        DeletedAtUtc     DATETIME2(3)   NULL,
        DeletedBy        BIGINT         NULL,
        RowVersion       ROWVERSION     NOT NULL
    );
    CREATE UNIQUE INDEX UX_AccountTenant_Account_Tenant ON dbo.AccountTenant (AccountId, TenantId);
    CREATE INDEX IX_AccountTenant_Tenant_User ON dbo.AccountTenant (TenantId, UserProfileId);
END
GO

-- ---------------------------------------------------------------- AccountRefreshToken
IF OBJECT_ID('dbo.AccountRefreshToken', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountRefreshToken
    (
        Id                  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountRefreshToken PRIMARY KEY CLUSTERED,
        AccountId           NVARCHAR(450) NOT NULL CONSTRAINT FK_AccountRefreshToken_Account REFERENCES dbo.Account (AccountId) ON DELETE CASCADE,
        TenantId            INT           NOT NULL,
        UserProfileId       BIGINT        NOT NULL,
        TokenHash           CHAR(64)      NOT NULL,   -- SHA-256 hex; the raw token is never stored
        ExpiresAtUtc        DATETIME2(0)  NOT NULL,
        CreatedAtUtc        DATETIME2(0)  NOT NULL CONSTRAINT DF_AccountRefreshToken_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        RevokedAtUtc        DATETIME2(0)  NULL,
        ReplacedByTokenHash CHAR(64)      NULL,
        CreatedByIp         NVARCHAR(64)  NULL,
        UserAgent           NVARCHAR(400) NULL
    );
    CREATE UNIQUE INDEX UX_AccountRefreshToken_TokenHash ON dbo.AccountRefreshToken (TokenHash);
    CREATE INDEX IX_AccountRefreshToken_Account ON dbo.AccountRefreshToken (AccountId) INCLUDE (RevokedAtUtc, ExpiresAtUtc);
END
GO

-- ---------------------------------------------------------------- AuthAuditLog
IF OBJECT_ID('dbo.AuthAuditLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuthAuditLog
    (
        Id         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuthAuditLog PRIMARY KEY CLUSTERED,
        AtUtc      DATETIME2(3)  NOT NULL CONSTRAINT DF_AuthAuditLog_AtUtc DEFAULT (SYSUTCDATETIME()),
        Email      NVARCHAR(256) NULL,
        AccountId  NVARCHAR(450) NULL,
        TenantId   INT           NULL,
        Event      NVARCHAR(50)  NOT NULL,   -- Login, LoginFailed, Refresh, RefreshReplay, Logout, LogoutAll, SwitchTenant, PasswordChanged, Lockout
        Success    BIT           NOT NULL,
        Detail     NVARCHAR(400) NULL,
        Ip         NVARCHAR(64)  NULL,
        UserAgent  NVARCHAR(400) NULL
    );
    CREATE INDEX IX_AuthAuditLog_Account_At ON dbo.AuthAuditLog (AccountId, AtUtc DESC);
    CREATE INDEX IX_AuthAuditLog_At ON dbo.AuthAuditLog (AtUtc DESC);
END
GO

-- ================================================================ Procedures
CREATE OR ALTER PROCEDURE dbo.Catalog_Tenant_GetAll
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TenantId, Code, Name, ConnectionString, IsActive, CreatedAtUtc, ModifiedAtUtc
    FROM dbo.Tenant WHERE IsDeleted = 0 ORDER BY Code;
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_Tenant_GetByCode
    @Code NVARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP 1 TenantId, Code, Name, ConnectionString, IsActive, CreatedAtUtc, ModifiedAtUtc
    FROM dbo.Tenant WHERE Code = @Code AND IsDeleted = 0;
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_Account_GetByEmail
    @NormalizedEmail NVARCHAR(256)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP 1 AccountId, Email, NormalizedEmail, PasswordHash, LockoutEnabled, LockoutEnd, AccessFailedCount, TokenVersion, PasswordChangedByApiAtUtc, IsActive
    FROM dbo.Account WHERE NormalizedEmail = @NormalizedEmail AND IsDeleted = 0;
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_Account_GetById
    @AccountId NVARCHAR(450)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP 1 AccountId, Email, NormalizedEmail, PasswordHash, LockoutEnabled, LockoutEnd, AccessFailedCount, TokenVersion, PasswordChangedByApiAtUtc, IsActive
    FROM dbo.Account WHERE AccountId = @AccountId AND IsDeleted = 0;
END
GO

-- Called by AccountSyncJob for every tenant AspNetUsers row: creates the account on first sight and, while the
-- legacy app is still the writer of tenant credentials, mirrors a changed password hash into the catalog.
CREATE OR ALTER PROCEDURE dbo.Catalog_Account_UpsertFromTenant
    @AccountId        NVARCHAR(450),
    @Email            NVARCHAR(256),
    @PasswordHash     NVARCHAR(MAX),
    @LockoutEnabled   BIT,
    @LockoutEnd       DATETIMEOFFSET = NULL,
    @AccessFailedCount INT,
    @TenantId         INT,
    @UserProfileId    BIGINT,
    @IsActive         BIT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Normalized NVARCHAR(256) = UPPER(@Email);

    IF NOT EXISTS (SELECT 1 FROM dbo.Account WHERE AccountId = @AccountId)
    BEGIN
        -- Same e-mail already registered under another AccountId (the same person in a second tenant): link, do not duplicate.
        DECLARE @Existing NVARCHAR(450) = (SELECT TOP 1 AccountId FROM dbo.Account WHERE NormalizedEmail = @Normalized);
        IF @Existing IS NOT NULL SET @AccountId = @Existing;
        ELSE
            INSERT INTO dbo.Account (AccountId, Email, NormalizedEmail, PasswordHash, LockoutEnabled, LockoutEnd, AccessFailedCount, IsActive)
            VALUES (@AccountId, @Email, @Normalized, @PasswordHash, @LockoutEnabled, @LockoutEnd, @AccessFailedCount, @IsActive);
    END
    ELSE IF @PasswordHash IS NOT NULL
    BEGIN
        -- Tenant -> catalog only while the legacy app still owns the password (never changed through the API).
        UPDATE dbo.Account
        SET PasswordHash = @PasswordHash, ModifiedAtUtc = SYSUTCDATETIME()
        WHERE AccountId = @AccountId AND PasswordChangedByApiAtUtc IS NULL AND ISNULL(PasswordHash, '') <> @PasswordHash;
    END

    MERGE dbo.AccountTenant AS t
    USING (SELECT @AccountId AS AccountId, @TenantId AS TenantId) AS s ON t.AccountId = s.AccountId AND t.TenantId = s.TenantId
    WHEN MATCHED AND (t.UserProfileId <> @UserProfileId OR t.IsActive <> @IsActive) THEN
        UPDATE SET UserProfileId = @UserProfileId, IsActive = @IsActive, ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (AccountId, TenantId, UserProfileId, IsDefault, IsActive)
        VALUES (@AccountId, @TenantId, @UserProfileId,
                CASE WHEN EXISTS (SELECT 1 FROM dbo.AccountTenant WHERE AccountId = @AccountId AND IsDeleted = 0) THEN 0 ELSE 1 END,
                @IsActive);

    -- Tells the sync job whether the catalog hash must be pushed back down to this tenant row.
    SELECT a.AccountId, a.PasswordHash AS CatalogPasswordHash,
           CAST(CASE WHEN a.PasswordChangedByApiAtUtc IS NOT NULL AND ISNULL(a.PasswordHash, '') <> ISNULL(@PasswordHash, '') THEN 1 ELSE 0 END AS BIT) AS MirrorToTenant
    FROM dbo.Account a WHERE a.AccountId = @AccountId;
END
GO

-- @ByApi = 1 for API password changes (catalog becomes authoritative); 0 for re-hash-on-login of a tenant-owned hash.
CREATE OR ALTER PROCEDURE dbo.Catalog_Account_UpdatePasswordHash
    @AccountId NVARCHAR(450), @PasswordHash NVARCHAR(MAX), @ModifiedBy BIGINT = NULL, @ByApi BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Account
    SET PasswordHash = @PasswordHash, ModifiedAtUtc = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy,
        PasswordChangedByApiAtUtc = CASE WHEN @ByApi = 1 THEN SYSUTCDATETIME() ELSE PasswordChangedByApiAtUtc END
    WHERE AccountId = @AccountId;
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_Account_RecordFailedAccess
    @AccountId NVARCHAR(450), @MaxFailedAttempts INT, @LockoutEnd DATETIMEOFFSET
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Account
    SET LockoutEnd        = CASE WHEN LockoutEnabled = 1 AND AccessFailedCount + 1 >= @MaxFailedAttempts THEN @LockoutEnd ELSE LockoutEnd END,
        AccessFailedCount = CASE WHEN LockoutEnabled = 1 AND AccessFailedCount + 1 >= @MaxFailedAttempts THEN 0 ELSE AccessFailedCount + 1 END
    WHERE AccountId = @AccountId;
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_Account_ResetFailedAccess
    @AccountId NVARCHAR(450)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Account SET AccessFailedCount = 0, LockoutEnd = NULL
    WHERE AccountId = @AccountId AND (AccessFailedCount <> 0 OR LockoutEnd IS NOT NULL);
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_Account_BumpTokenVersion
    @AccountId NVARCHAR(450)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Account SET TokenVersion = TokenVersion + 1, ModifiedAtUtc = SYSUTCDATETIME() WHERE AccountId = @AccountId;
    SELECT TokenVersion FROM dbo.Account WHERE AccountId = @AccountId;
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_AccountTenant_GetByAccount
    @AccountId NVARCHAR(450)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT at.AccountTenantId, at.AccountId, at.TenantId, t.Code AS TenantCode, t.Name AS TenantName, at.UserProfileId, at.IsDefault, at.IsActive
    FROM dbo.AccountTenant at
    INNER JOIN dbo.Tenant t ON t.TenantId = at.TenantId AND t.IsDeleted = 0 AND t.IsActive = 1
    WHERE at.AccountId = @AccountId AND at.IsDeleted = 0
    ORDER BY at.IsDefault DESC, t.Code;
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_AccountTenant_Upsert
    @AccountId NVARCHAR(450), @TenantId INT, @UserProfileId BIGINT, @IsDefault BIT = 0, @IsActive BIT = 1, @ActorUserProfileId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @IsDefault = 1
        UPDATE dbo.AccountTenant SET IsDefault = 0 WHERE AccountId = @AccountId AND IsDefault = 1;

    MERGE dbo.AccountTenant AS t
    USING (SELECT @AccountId AS AccountId, @TenantId AS TenantId) AS s ON t.AccountId = s.AccountId AND t.TenantId = s.TenantId
    WHEN MATCHED THEN
        UPDATE SET UserProfileId = @UserProfileId, IsDefault = @IsDefault, IsActive = @IsActive, IsDeleted = 0,
                   ModifiedAtUtc = SYSUTCDATETIME(), ModifiedBy = @ActorUserProfileId
    WHEN NOT MATCHED THEN
        INSERT (AccountId, TenantId, UserProfileId, IsDefault, IsActive, CreatedBy)
        VALUES (@AccountId, @TenantId, @UserProfileId, @IsDefault, @IsActive, @ActorUserProfileId);
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_RefreshToken_Save
    @AccountId NVARCHAR(450), @TenantId INT, @UserProfileId BIGINT, @TokenHash CHAR(64),
    @ExpiresAtUtc DATETIME2(0), @CreatedAtUtc DATETIME2(0), @CreatedByIp NVARCHAR(64) = NULL, @UserAgent NVARCHAR(400) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.AccountRefreshToken (AccountId, TenantId, UserProfileId, TokenHash, ExpiresAtUtc, CreatedAtUtc, CreatedByIp, UserAgent)
    VALUES (@AccountId, @TenantId, @UserProfileId, @TokenHash, @ExpiresAtUtc, @CreatedAtUtc, @CreatedByIp, @UserAgent);
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_RefreshToken_Get
    @TokenHash CHAR(64)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP 1 Id, AccountId, TenantId, UserProfileId, TokenHash, ExpiresAtUtc, CreatedAtUtc, RevokedAtUtc, ReplacedByTokenHash, CreatedByIp, UserAgent
    FROM dbo.AccountRefreshToken WHERE TokenHash = @TokenHash;
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_RefreshToken_Revoke
    @TokenHash CHAR(64), @ReplacedByTokenHash CHAR(64) = NULL, @RevokedAtUtc DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.AccountRefreshToken SET RevokedAtUtc = @RevokedAtUtc, ReplacedByTokenHash = @ReplacedByTokenHash
    WHERE TokenHash = @TokenHash AND RevokedAtUtc IS NULL;
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_RefreshToken_RevokeAll
    @AccountId NVARCHAR(450), @RevokedAtUtc DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.AccountRefreshToken SET RevokedAtUtc = @RevokedAtUtc WHERE AccountId = @AccountId AND RevokedAtUtc IS NULL;
END
GO

CREATE OR ALTER PROCEDURE dbo.Catalog_AuthAudit_Write
    @Email NVARCHAR(256) = NULL, @AccountId NVARCHAR(450) = NULL, @TenantId INT = NULL, @Event NVARCHAR(50), @Success BIT,
    @Detail NVARCHAR(400) = NULL, @Ip NVARCHAR(64) = NULL, @UserAgent NVARCHAR(400) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.AuthAuditLog (Email, AccountId, TenantId, Event, Success, Detail, Ip, UserAgent)
    VALUES (@Email, @AccountId, @TenantId, @Event, @Success, @Detail, @Ip, @UserAgent);
END
GO

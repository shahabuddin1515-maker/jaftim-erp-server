/*
    v2 - multiple roles per user, optionally time-bound. TENANT database script. Additive, idempotent.

    UserProfile.RoleId stays the PRIMARY role and is what every legacy procedure that branches on RoleId sees
    (69 procedures: visibility scoping, approval routing). Rows in UserRole widen the API permission set (union of
    the primary role and every UserRole row that is active and inside its validity window).

    fn_GetUserEffectiveRoles is provided for future _V2 procedures; no legacy procedure is changed here.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.UserRole', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserRole
    (
        UserRoleId     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_UserRole PRIMARY KEY CLUSTERED,
        UserProfileId  BIGINT        NOT NULL CONSTRAINT FK_UserRole_UserProfile REFERENCES dbo.UserProfile (UserProfileId),
        RoleId         BIGINT        NOT NULL CONSTRAINT FK_UserRole_Role REFERENCES dbo.Role (RoleId),
        ValidFromUtc   DATETIME2(0)  NULL,   -- NULL = immediately
        ValidToUtc     DATETIME2(0)  NULL,   -- NULL = permanent
        Reason         NVARCHAR(400) NULL,
        IsActive       BIT           NOT NULL CONSTRAINT DF_UserRole_IsActive DEFAULT (1),
        CreatedAtUtc   DATETIME2(3)  NOT NULL CONSTRAINT DF_UserRole_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy      BIGINT        NULL,
        ModifiedAtUtc  DATETIME2(3)  NULL,
        ModifiedBy     BIGINT        NULL,
        IsDeleted      BIT           NOT NULL CONSTRAINT DF_UserRole_IsDeleted DEFAULT (0),
        DeletedAtUtc   DATETIME2(3)  NULL,
        DeletedBy      BIGINT        NULL,
        RowVersion     ROWVERSION    NOT NULL,
        CONSTRAINT CK_UserRole_Validity CHECK (ValidFromUtc IS NULL OR ValidToUtc IS NULL OR ValidToUtc > ValidFromUtc)
    );
    CREATE UNIQUE INDEX UX_UserRole_User_Role ON dbo.UserRole (UserProfileId, RoleId) WHERE IsDeleted = 0;
END
GO

-- Backfill: every profile gets a permanent row for its primary role.
INSERT INTO dbo.UserRole (UserProfileId, RoleId, Reason)
SELECT up.UserProfileId, up.RoleId, N'Backfill from UserProfile.RoleId'
FROM dbo.UserProfile up
INNER JOIN dbo.Role r ON r.RoleId = up.RoleId
WHERE ISNULL(up.IsDeleted, 0) = 0
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRole ur WHERE ur.UserProfileId = up.UserProfileId AND ur.RoleId = up.RoleId AND ur.IsDeleted = 0);
GO

CREATE OR ALTER FUNCTION dbo.fn_GetUserEffectiveRoles (@UserProfileId BIGINT, @AsOfUtc DATETIME2(0))
RETURNS TABLE
AS
RETURN
(
    SELECT up.RoleId, CAST(1 AS BIT) AS IsPrimary, CAST(NULL AS DATETIME2(0)) AS ValidFromUtc, CAST(NULL AS DATETIME2(0)) AS ValidToUtc
    FROM dbo.UserProfile up
    WHERE up.UserProfileId = @UserProfileId
    UNION
    SELECT ur.RoleId, CAST(0 AS BIT), ur.ValidFromUtc, ur.ValidToUtc
    FROM dbo.UserRole ur
    INNER JOIN dbo.Role r ON r.RoleId = ur.RoleId AND ISNULL(r.IsDeleted, 0) = 0
    WHERE ur.UserProfileId = @UserProfileId
      AND ur.IsDeleted = 0 AND ur.IsActive = 1
      AND (ur.ValidFromUtc IS NULL OR ur.ValidFromUtc <= @AsOfUtc)
      AND (ur.ValidToUtc IS NULL OR ur.ValidToUtc > @AsOfUtc)
      AND ur.RoleId <> (SELECT RoleId FROM dbo.UserProfile WHERE UserProfileId = @UserProfileId)
);
GO

CREATE OR ALTER PROCEDURE dbo.UserRole_GetByUser
    @UserProfileId BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ur.UserRoleId, ur.UserProfileId, ur.RoleId, r.RoleName, ur.ValidFromUtc, ur.ValidToUtc, ur.Reason, ur.IsActive,
           CAST(CASE WHEN ur.RoleId = up.RoleId THEN 1 ELSE 0 END AS BIT) AS IsPrimary,
           ur.CreatedAtUtc, ur.CreatedBy, ur.ModifiedAtUtc, ur.ModifiedBy
    FROM dbo.UserRole ur
    INNER JOIN dbo.Role r ON r.RoleId = ur.RoleId
    INNER JOIN dbo.UserProfile up ON up.UserProfileId = ur.UserProfileId
    WHERE ur.UserProfileId = @UserProfileId AND ur.IsDeleted = 0
    ORDER BY IsPrimary DESC, ur.CreatedAtUtc;
END
GO

CREATE OR ALTER PROCEDURE dbo.UserRole_GetEffective
    @UserProfileId BIGINT, @AsOfUtc DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT e.RoleId, r.RoleName, e.IsPrimary, e.ValidFromUtc, e.ValidToUtc
    FROM dbo.fn_GetUserEffectiveRoles(@UserProfileId, @AsOfUtc) e
    INNER JOIN dbo.Role r ON r.RoleId = e.RoleId
    ORDER BY e.IsPrimary DESC, r.RoleName;
END
GO

CREATE OR ALTER PROCEDURE dbo.UserRole_Save
    @UserProfileId BIGINT, @RoleId BIGINT, @ValidFromUtc DATETIME2(0) = NULL, @ValidToUtc DATETIME2(0) = NULL,
    @Reason NVARCHAR(400) = NULL, @ActorUserProfileId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.UserRole AS t
    USING (SELECT @UserProfileId AS UserProfileId, @RoleId AS RoleId) AS s
        ON t.UserProfileId = s.UserProfileId AND t.RoleId = s.RoleId AND t.IsDeleted = 0
    WHEN MATCHED THEN
        UPDATE SET ValidFromUtc = @ValidFromUtc, ValidToUtc = @ValidToUtc, Reason = @Reason, IsActive = 1,
                   ModifiedAtUtc = SYSUTCDATETIME(), ModifiedBy = @ActorUserProfileId
    WHEN NOT MATCHED THEN
        INSERT (UserProfileId, RoleId, ValidFromUtc, ValidToUtc, Reason, CreatedBy)
        VALUES (@UserProfileId, @RoleId, @ValidFromUtc, @ValidToUtc, @Reason, @ActorUserProfileId);

    SELECT UserRoleId FROM dbo.UserRole WHERE UserProfileId = @UserProfileId AND RoleId = @RoleId AND IsDeleted = 0;
END
GO

CREATE OR ALTER PROCEDURE dbo.UserRole_Revoke
    @UserProfileId BIGINT, @RoleId BIGINT, @ActorUserProfileId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.UserRole
    SET IsActive = 0, IsDeleted = 1, DeletedAtUtc = SYSUTCDATETIME(), DeletedBy = @ActorUserProfileId
    WHERE UserProfileId = @UserProfileId AND RoleId = @RoleId AND IsDeleted = 0;
    SELECT @@ROWCOUNT AS Affected;
END
GO

-- Changes the primary role (what the legacy procedures see) and guarantees a permanent UserRole row for it.
CREATE OR ALTER PROCEDURE dbo.UserProfile_SetPrimaryRole
    @UserProfileId BIGINT, @RoleId BIGINT, @ActorUserProfileId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.UserProfile SET RoleId = @RoleId, ModifiedAt = GETUTCDATE(), ModifiedBy = @ActorUserProfileId
    WHERE UserProfileId = @UserProfileId;

    EXEC dbo.UserRole_Save @UserProfileId = @UserProfileId, @RoleId = @RoleId, @ValidFromUtc = NULL, @ValidToUtc = NULL,
                           @Reason = N'Primary role', @ActorUserProfileId = @ActorUserProfileId;
END
GO

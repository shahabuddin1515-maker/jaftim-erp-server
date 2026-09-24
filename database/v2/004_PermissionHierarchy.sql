/*
    v2 - permission hierarchy completion + role/permission management procedures. TENANT database script.
    Additive, idempotent. Nothing the legacy app reads changes shape; it only sees a few extra RoleAction rows
    (ids 900+, which no legacy view references) and their RoleActionMapping rows.

    Hierarchy: MODULE (ActionParentId = 0) -> SCREEN (navbar leaf) -> ACTION (button/tab). Every NavigationItem,
    modules included, is gated by exactly one RoleAction; the legacy sidebar's RoleId branches are replaced by
    permissions seeded to the same roles.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ---------------------------------------------------------------- v2 permissions (900+ range)
DECLARE @p TABLE (ActionId INT, ActionName NVARCHAR(200), ParentId INT);
INSERT INTO @p VALUES
(900, 'Operations',             0),
(901, 'Finance',                0),
(902, 'People',                 0),
(903, 'Settings',               0),
(904, 'Notification Settings',  903),
(905, 'Master Data',            903),
(906, 'Navigation Settings',    903),
(907, 'My Tagging (Agent)',     526);

MERGE dbo.RoleAction AS t
USING @p AS s ON t.ActionId = s.ActionId
WHEN MATCHED AND (t.ActionName <> s.ActionName OR ISNULL(t.ActionParentId, 0) <> s.ParentId) THEN
    UPDATE SET ActionName = s.ActionName, ActionParentId = s.ParentId
WHEN NOT MATCHED THEN
    INSERT (ActionId, ActionName, ActionCssClass, ActionMvcPermission, ActionParentId, ActionIdentification, DependantId)
    VALUES (s.ActionId, s.ActionName, '_CSS_' + CAST(s.ActionId AS NVARCHAR(10)), '/', s.ParentId, 1, 0);
GO

-- ---------------------------------------------------------------- seed grants to the roles that saw these in the legacy sidebar
-- Group modules: any role that holds at least one child screen gets the module.
DECLARE @grants TABLE (RoleId BIGINT, ActionId INT);
INSERT INTO @grants
SELECT DISTINCT m.RoleId, 900 FROM dbo.RoleActionMapping m WHERE ISNULL(m.IsDeleted,0)=0 AND m.ActionId IN (611, 590, 603, 605)
UNION SELECT DISTINCT m.RoleId, 901 FROM dbo.RoleActionMapping m WHERE ISNULL(m.IsDeleted,0)=0 AND m.ActionId IN (501, 521)
UNION SELECT DISTINCT m.RoleId, 902 FROM dbo.RoleActionMapping m WHERE ISNULL(m.IsDeleted,0)=0 AND m.ActionId IN (201, 301)
UNION SELECT r.RoleId, a.ActionId FROM dbo.Role r CROSS JOIN (VALUES (903), (904), (906)) a(ActionId) WHERE r.RoleId IN (1, 10)   -- Super Admin, System Admin
UNION SELECT 1, 905                                                                                                              -- Master Data: Super Admin only (legacy RoleId == 1)
UNION SELECT 2, 907;                                                                                                             -- My Tagging: Sales Executive (legacy RoleId == 2)

INSERT INTO dbo.RoleActionMapping (RoleId, ActionId, CreatedAt, CreatedBy, IsDeleted)
SELECT g.RoleId, g.ActionId, GETUTCDATE(), '1', 0
FROM @grants g
WHERE NOT EXISTS (SELECT 1 FROM dbo.RoleActionMapping m WHERE m.RoleId = g.RoleId AND m.ActionId = g.ActionId AND ISNULL(m.IsDeleted,0)=0);
GO

-- ---------------------------------------------------------------- navigation nodes now carry their permission; role lists retired
UPDATE dbo.NavigationItem SET ActionId = 900, RequiredRoleIdsCsv = NULL WHERE Code = 'operations'             AND IsDeleted = 0;
UPDATE dbo.NavigationItem SET ActionId = 901, RequiredRoleIdsCsv = NULL WHERE Code = 'finance'                AND IsDeleted = 0;
UPDATE dbo.NavigationItem SET ActionId = 902, RequiredRoleIdsCsv = NULL WHERE Code = 'people'                 AND IsDeleted = 0;
UPDATE dbo.NavigationItem SET ActionId = 903, RequiredRoleIdsCsv = NULL WHERE Code = 'settings'               AND IsDeleted = 0;
UPDATE dbo.NavigationItem SET ActionId = 904, RequiredRoleIdsCsv = NULL WHERE Code = 'settings.notifications' AND IsDeleted = 0;
UPDATE dbo.NavigationItem SET ActionId = 905, RequiredRoleIdsCsv = NULL WHERE Code = 'settings.master-data'   AND IsDeleted = 0;
UPDATE dbo.NavigationItem SET ActionId = 906, RequiredRoleIdsCsv = NULL WHERE Code = 'settings.navigation'    AND IsDeleted = 0;
UPDATE dbo.NavigationItem SET ActionId = 907, RequiredRoleIdsCsv = NULL WHERE Code = 'customers.my-tagging'   AND IsDeleted = 0;
GO

-- ---------------------------------------------------------------- procedures
-- Create/rename/re-parent a permission. New ids are allocated from 900 upward so they never collide with legacy ids.
CREATE OR ALTER PROCEDURE dbo.RoleAction_Save
    @ActionId INT = NULL, @ActionName NVARCHAR(200), @ActionParentId INT = 0, @ActorUserProfileId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @ActionId IS NULL OR NOT EXISTS (SELECT 1 FROM dbo.RoleAction WHERE ActionId = @ActionId)
    BEGIN
        DECLARE @next INT = (SELECT ISNULL(MAX(ActionId), 899) + 1 FROM dbo.RoleAction WHERE ActionId >= 900);
        INSERT INTO dbo.RoleAction (ActionId, ActionName, ActionCssClass, ActionMvcPermission, ActionParentId, ActionIdentification, DependantId)
        VALUES (@next, @ActionName, '_CSS_' + CAST(@next AS NVARCHAR(10)), '/', ISNULL(@ActionParentId, 0), 1, 0);
        SELECT @next AS ActionId;
        RETURN;
    END
    UPDATE dbo.RoleAction SET ActionName = @ActionName, ActionParentId = ISNULL(@ActionParentId, 0) WHERE ActionId = @ActionId;
    SELECT @ActionId AS ActionId;
END
GO

-- Replace a role's whole permission set. Rows dropped are soft-deleted (legacy IsDeleted bit), rows added inserted,
-- rows kept untouched - so RoleActionMapping history stays meaningful. Caller sends the closed set (ancestors included).
CREATE OR ALTER PROCEDURE dbo.RoleActionMapping_Replace
    @RoleId BIGINT, @ActionIdsCsv NVARCHAR(MAX), @ActorUserProfileId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @wanted TABLE (ActionId INT PRIMARY KEY);
    INSERT INTO @wanted SELECT DISTINCT TRY_CAST(value AS INT) FROM STRING_SPLIT(ISNULL(@ActionIdsCsv, ''), ',') WHERE TRY_CAST(value AS INT) IS NOT NULL;

    BEGIN TRAN;
    UPDATE m SET IsDeleted = 1, ModifiedAt = GETUTCDATE(), ModifiedBy = CAST(@ActorUserProfileId AS NVARCHAR(50))
    FROM dbo.RoleActionMapping m
    WHERE m.RoleId = @RoleId AND ISNULL(m.IsDeleted, 0) = 0 AND m.ActionId NOT IN (SELECT ActionId FROM @wanted);

    INSERT INTO dbo.RoleActionMapping (RoleId, ActionId, CreatedAt, CreatedBy, IsDeleted)
    SELECT @RoleId, w.ActionId, GETUTCDATE(), CAST(@ActorUserProfileId AS NVARCHAR(50)), 0
    FROM @wanted w
    INNER JOIN dbo.RoleAction a ON a.ActionId = w.ActionId
    WHERE NOT EXISTS (SELECT 1 FROM dbo.RoleActionMapping m WHERE m.RoleId = @RoleId AND m.ActionId = w.ActionId AND ISNULL(m.IsDeleted, 0) = 0);
    COMMIT;

    SELECT COUNT(*) AS Granted FROM dbo.RoleActionMapping WHERE RoleId = @RoleId AND ISNULL(IsDeleted, 0) = 0;
END
GO

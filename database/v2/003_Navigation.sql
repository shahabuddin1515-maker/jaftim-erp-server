/*
    v2 - navigation tree. TENANT database script. Additive, idempotent.

    The frontend navbar is data: modules (ParentId NULL) -> items -> sub-items. Each item is gated by a RoleAction
    (ActionId) and/or a role list; GET /api/navigation/me returns the tree already filtered for the caller.
    Code is the stable key the frontend maps to a route component; Route is the proposed frontend path.
    Seeded from the legacy _SidebarPartial.cshtml, regrouped by module. Re-running updates titles/order/routes by Code
    and never deletes rows an admin added.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.NavigationItem', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.NavigationItem
    (
        NavigationItemId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_NavigationItem PRIMARY KEY CLUSTERED,
        ParentId           INT            NULL CONSTRAINT FK_NavigationItem_Parent REFERENCES dbo.NavigationItem (NavigationItemId),
        Code               NVARCHAR(100)  NOT NULL,   -- stable key, e.g. stock.list
        Title              NVARCHAR(100)  NOT NULL,
        Icon               NVARCHAR(100)  NULL,       -- icon name the frontend resolves (Font Awesome class today)
        Route              NVARCHAR(200)  NULL,       -- frontend path; NULL for a pure group
        SortOrder          INT            NOT NULL CONSTRAINT DF_NavigationItem_SortOrder DEFAULT (0),
        ActionId           INT            NULL CONSTRAINT FK_NavigationItem_RoleAction REFERENCES dbo.RoleAction (ActionId),
        RequiredRoleIdsCsv NVARCHAR(200)  NULL,       -- if set, one of the caller's effective roles must be listed
        IsActive           BIT            NOT NULL CONSTRAINT DF_NavigationItem_IsActive DEFAULT (1),
        CreatedAtUtc       DATETIME2(3)   NOT NULL CONSTRAINT DF_NavigationItem_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy          BIGINT         NULL,
        ModifiedAtUtc      DATETIME2(3)   NULL,
        ModifiedBy         BIGINT         NULL,
        IsDeleted          BIT            NOT NULL CONSTRAINT DF_NavigationItem_IsDeleted DEFAULT (0),
        DeletedAtUtc       DATETIME2(3)   NULL,
        DeletedBy          BIGINT         NULL,
        RowVersion         ROWVERSION     NOT NULL
    );
    CREATE UNIQUE INDEX UX_NavigationItem_Code ON dbo.NavigationItem (Code) WHERE IsDeleted = 0;
END
GO

-- ---------------------------------------------------------------- seed (upsert by Code; parents by ParentCode)
DECLARE @seed TABLE (Code NVARCHAR(100), ParentCode NVARCHAR(100), Title NVARCHAR(100), Icon NVARCHAR(100), Route NVARCHAR(200), SortOrder INT, ActionId INT, Roles NVARCHAR(200));
INSERT INTO @seed VALUES
-- modules (ParentCode NULL)
('stock',        NULL,        'Stock',          'fa-car',            NULL,                     10, 400, NULL),
('customers',    NULL,        'Customers',      'fa-user-check',     NULL,                     20, 100, NULL),
('operations',   NULL,        'Operations',     'fa-ship',           NULL,                     30, NULL, NULL),
('finance',      NULL,        'Finance',        'fa-money-bill',     NULL,                     40, NULL, NULL),
('tasks',        NULL,        'My Task',        'fa-tasks',          '/tasks',                 50, 524, NULL),
('people',       NULL,        'People',         'fa-users',          NULL,                     60, NULL, NULL),
('reports',      NULL,        'Reports',        'fa-chart-line',     '/reports',               70, 549, NULL),
('transport',    NULL,        'Transport',      'fa-truck',          NULL,                     80, 630, NULL),
('settings',     NULL,        'Settings',       'fa-cogs',           NULL,                     90, NULL, '1,10'),
-- stock
('stock.list',            'stock',      'Stocks',                    'fa-car',            '/stocks',                 10, 417, NULL),
('stock.pricing',         'stock',      'Pricing',                   'fa-tags',           '/pricing',                20, 577, NULL),
('stock.sales',           'stock',      'Sales Module',              'fa-receipt',        '/sales',                  30, 540, NULL),
-- customers
('customers.list',        'customers',  'Customers',                 'fa-user-check',     '/customers',              10, 101, NULL),
('customers.inquiries',   'customers',  'Inquiries',                 'fa-sticky-note',    '/inquiries',              20, 537, NULL),
('customers.tagging',     'customers',  'Customer Tagging',          'fa-street-view',    '/tagging',                30, 526, NULL),
('customers.my-tagging',  'customers',  'My Tagging',                'fa-street-view',    '/tagging/me',             31, NULL, '2'),
-- operations
('operations.shipping',           'operations',          'Shipping',                  'fa-ship',            NULL,                    10, 611, NULL),
('operations.shipping.schedules', 'operations.shipping', 'View Shipping Schedules',   'fa-eye',             '/shipping/schedules',   10, 611, NULL),
('operations.shipping.manage',    'operations.shipping', 'Manage Shipping Schedules', 'fa-ship',            '/shipping',             20, 611, NULL),
('operations.documents',          'operations',          'Documents',                 'fa-folder-open',     '/documents',            20, 590, NULL),
('operations.inspection',         'operations',          'Inspection',                'fa-clipboard-check', '/inspections',          30, 603, NULL),
('operations.vendors',            'operations',          'Vendors',                   'fa-tools',           '/vendors',              40, 605, NULL),
-- finance
('finance.bank-statements',       'finance',   'Bank Statements',            'fa-money-bill',     '/finance/bank-statements',      10, 501, NULL),
('finance.income',                'finance',   'Income Reconciliation',      'fa-money-bill',     '/finance/income',               20, 521, NULL),
-- people
('people.employees',              'people',    'Employees',                  'fa-users',          '/employees',                    10, 201, NULL),
('people.roles',                  'people',    'Roles & Rights',             'fa-cogs',           '/roles',                        20, 301, NULL),
-- transport
('transport.quotes',              'transport', 'Transport quotations',       'fa-truck',          '/transport',                    10, 631, NULL),
('transport.admin',               'transport', 'Transport administration',   'fa-sliders-h',      '/transport/admin',              20, 632, NULL),
-- settings (legacy "Master Setting", Super Admin / System Admin only)
('settings.notifications',        'settings',  'Notification Settings',      'fa-bell',           '/settings/notifications',       10, NULL, '1,10'),
('settings.master-data',          'settings',  'Master Data',                'fa-database',       '/settings/master-data',         20, NULL, '1'),
('settings.navigation',           'settings',  'Navigation',                 'fa-bars',           '/settings/navigation',          30, 304, '1');

-- parents first (3 passes cover the 3 levels seeded)
DECLARE @pass INT = 0;
WHILE @pass < 3
BEGIN
    MERGE dbo.NavigationItem AS t
    USING (
        SELECT s.Code, p.NavigationItemId AS ParentId, s.Title, s.Icon, s.Route, s.SortOrder, s.ActionId, s.Roles
        FROM @seed s
        LEFT JOIN dbo.NavigationItem p ON p.Code = s.ParentCode AND p.IsDeleted = 0
        WHERE s.ParentCode IS NULL OR p.NavigationItemId IS NOT NULL
    ) AS s ON t.Code = s.Code AND t.IsDeleted = 0
    WHEN MATCHED AND (t.Title <> s.Title OR ISNULL(t.Icon,'') <> ISNULL(s.Icon,'') OR ISNULL(t.Route,'') <> ISNULL(s.Route,'')
                      OR t.SortOrder <> s.SortOrder OR ISNULL(t.ActionId,0) <> ISNULL(s.ActionId,0) OR ISNULL(t.RequiredRoleIdsCsv,'') <> ISNULL(s.Roles,'')
                      OR ISNULL(t.ParentId,0) <> ISNULL(s.ParentId,0)) THEN
        UPDATE SET Title = s.Title, Icon = s.Icon, Route = s.Route, SortOrder = s.SortOrder, ActionId = s.ActionId,
                   RequiredRoleIdsCsv = s.Roles, ParentId = s.ParentId, ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (ParentId, Code, Title, Icon, Route, SortOrder, ActionId, RequiredRoleIdsCsv)
        VALUES (s.ParentId, s.Code, s.Title, s.Icon, s.Route, s.SortOrder, s.ActionId, s.Roles);
    SET @pass += 1;
END
GO

CREATE OR ALTER PROCEDURE dbo.Navigation_GetAll
AS
BEGIN
    SET NOCOUNT ON;
    SELECT NavigationItemId, ParentId, Code, Title, Icon, Route, SortOrder, ActionId, RequiredRoleIdsCsv, IsActive
    FROM dbo.NavigationItem WHERE IsDeleted = 0
    ORDER BY ISNULL(ParentId, 0), SortOrder, Title;
END
GO

CREATE OR ALTER PROCEDURE dbo.Navigation_Save
    @NavigationItemId INT = NULL, @ParentId INT = NULL, @Code NVARCHAR(100), @Title NVARCHAR(100), @Icon NVARCHAR(100) = NULL,
    @Route NVARCHAR(200) = NULL, @SortOrder INT = 0, @ActionId INT = NULL, @RequiredRoleIdsCsv NVARCHAR(200) = NULL,
    @IsActive BIT = 1, @ActorUserProfileId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @NavigationItemId IS NULL OR NOT EXISTS (SELECT 1 FROM dbo.NavigationItem WHERE NavigationItemId = @NavigationItemId AND IsDeleted = 0)
    BEGIN
        INSERT INTO dbo.NavigationItem (ParentId, Code, Title, Icon, Route, SortOrder, ActionId, RequiredRoleIdsCsv, IsActive, CreatedBy)
        VALUES (@ParentId, @Code, @Title, @Icon, @Route, @SortOrder, @ActionId, @RequiredRoleIdsCsv, @IsActive, @ActorUserProfileId);
        SELECT SCOPE_IDENTITY() AS NavigationItemId;
        RETURN;
    END
    UPDATE dbo.NavigationItem
    SET ParentId = @ParentId, Code = @Code, Title = @Title, Icon = @Icon, Route = @Route, SortOrder = @SortOrder,
        ActionId = @ActionId, RequiredRoleIdsCsv = @RequiredRoleIdsCsv, IsActive = @IsActive,
        ModifiedAtUtc = SYSUTCDATETIME(), ModifiedBy = @ActorUserProfileId
    WHERE NavigationItemId = @NavigationItemId;
    SELECT @NavigationItemId AS NavigationItemId;
END
GO

CREATE OR ALTER PROCEDURE dbo.Navigation_Delete
    @NavigationItemId INT, @ActorUserProfileId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    -- soft-delete the item and everything under it
    ;WITH tree AS (
        SELECT NavigationItemId FROM dbo.NavigationItem WHERE NavigationItemId = @NavigationItemId
        UNION ALL
        SELECT c.NavigationItemId FROM dbo.NavigationItem c INNER JOIN tree t ON c.ParentId = t.NavigationItemId
    )
    UPDATE n SET IsDeleted = 1, DeletedAtUtc = SYSUTCDATETIME(), DeletedBy = @ActorUserProfileId
    FROM dbo.NavigationItem n INNER JOIN tree t ON t.NavigationItemId = n.NavigationItemId
    WHERE n.IsDeleted = 0;
    SELECT @@ROWCOUNT AS Affected;
END
GO

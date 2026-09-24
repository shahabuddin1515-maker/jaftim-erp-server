/*
    v2 - central audit log. TENANT database script. Additive, idempotent.
    Written by the API (IAuditWriter) for every business write it performs: who, what, when, before/after JSON.
    The legacy trigger-fed *_History tables and the ActionURlTbl request audit are untouched and still apply.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.AuditLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuditLog
    (
        AuditId            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY CLUSTERED,
        AtUtc              DATETIME2(3)     NOT NULL CONSTRAINT DF_AuditLog_AtUtc DEFAULT (SYSUTCDATETIME()),
        ActorUserProfileId BIGINT           NULL,
        ActorAccountId     NVARCHAR(450)    NULL,
        Action             NVARCHAR(100)    NOT NULL,   -- dotted verb, e.g. UserRole.Assigned, Stock.StatusChanged
        EntityType         NVARCHAR(100)    NOT NULL,   -- e.g. UserProfile, Stock
        EntityId           BIGINT           NULL,
        Before             NVARCHAR(MAX)    NULL,       -- JSON snapshot (validated with ISJSON)
        After              NVARCHAR(MAX)    NULL,
        CorrelationId      UNIQUEIDENTIFIER NULL,       -- request trace id
        Ip                 NVARCHAR(64)     NULL,
        UserAgent          NVARCHAR(400)    NULL,
        CONSTRAINT CK_AuditLog_Before_Json CHECK (Before IS NULL OR ISJSON(Before) = 1),
        CONSTRAINT CK_AuditLog_After_Json  CHECK (After  IS NULL OR ISJSON(After)  = 1)
    );
    CREATE INDEX IX_AuditLog_Entity ON dbo.AuditLog (EntityType, EntityId, AtUtc DESC);
    CREATE INDEX IX_AuditLog_At ON dbo.AuditLog (AtUtc DESC);
    CREATE INDEX IX_AuditLog_Actor ON dbo.AuditLog (ActorUserProfileId, AtUtc DESC);
END
GO

CREATE OR ALTER PROCEDURE dbo.AuditLog_Write
    @AtUtc DATETIME2(3), @ActorUserProfileId BIGINT = NULL, @ActorAccountId NVARCHAR(450) = NULL,
    @Action NVARCHAR(100), @EntityType NVARCHAR(100), @EntityId BIGINT = NULL,
    @Before NVARCHAR(MAX) = NULL, @After NVARCHAR(MAX) = NULL,
    @CorrelationId UNIQUEIDENTIFIER = NULL, @Ip NVARCHAR(64) = NULL, @UserAgent NVARCHAR(400) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.AuditLog (AtUtc, ActorUserProfileId, ActorAccountId, Action, EntityType, EntityId, Before, After, CorrelationId, Ip, UserAgent)
    VALUES (@AtUtc, @ActorUserProfileId, @ActorAccountId, @Action, @EntityType, @EntityId, @Before, @After, @CorrelationId, @Ip, @UserAgent);
END
GO

CREATE OR ALTER PROCEDURE dbo.AuditLog_GetByEntity
    @EntityType NVARCHAR(100), @EntityId BIGINT = NULL, @Skip INT = 0, @Take INT = 50
AS
BEGIN
    SET NOCOUNT ON;
    SELECT COUNT(*) AS TotalRecords FROM dbo.AuditLog WHERE EntityType = @EntityType AND (@EntityId IS NULL OR EntityId = @EntityId);

    SELECT a.AuditId, a.AtUtc, a.ActorUserProfileId, up.FullName AS ActorName, a.ActorAccountId, a.Action, a.EntityType, a.EntityId,
           a.Before, a.After, a.CorrelationId, a.Ip, a.UserAgent
    FROM dbo.AuditLog a
    LEFT JOIN dbo.UserProfile up ON up.UserProfileId = a.ActorUserProfileId
    WHERE a.EntityType = @EntityType AND (@EntityId IS NULL OR a.EntityId = @EntityId)
    ORDER BY a.AtUtc DESC, a.AuditId DESC
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END
GO

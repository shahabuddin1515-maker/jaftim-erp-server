SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO
/*
    v2/007 - Messaging outboxes: the durable queues of the notification and email pipelines.

    Additive only: two new tables and their procedures; nothing the legacy app uses is touched. Apply to EVERY tenant
    database. Idempotent (IF OBJECT_ID ... IS NULL / CREATE OR ALTER).

    One row = one message. Lifecycle (docs/ARCHITECTURE.md "Messaging and job pipelines"):

        _Enqueue      Status 0 (Pending), NextAttemptAtUtc = now          <- the API / a job, audit trio injected
        _ClaimById    lease one row (immediate path) or confirm a lease   <- the per-message Hangfire job
        _ClaimDue     lease a batch of due rows (safety net)              <- the per-tenant sweep, every minute
        NotificationOutbox_MarkPersisted  in ONE transaction with Notification_Create: a retry never duplicates
        _MarkSucceeded  Status 1                                          <- lease-guarded
        _MarkFailed     Status 0 + NextAttemptAtUtc = now + delay, or Status 2 (dead letter)
        _Purge        hard-delete succeeded rows past retention (dead letters are kept)

    AttemptCount is incremented by _ClaimById, i.e. when processing actually starts - so a run that crashes
    mid-delivery still counts as an attempt (its lease expires and the sweep re-claims it), but a sweep lease that
    expires because the consumer host is down does not. "Processing" is Status 0 with a live lease.

    Status values: 0 Pending, 1 Succeeded, 2 DeadLettered.
    Indexes are deliberately non-filtered (docs/DATABASE.md - QUOTED_IDENTIFIER OFF modules).
*/

---------------------------------------------------------------------------------------------------------------------
-- NotificationOutbox
---------------------------------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.NotificationOutbox', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.NotificationOutbox
    (
        OutboxId         BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_NotificationOutbox PRIMARY KEY,
        TypeCode         VARCHAR(80)       NOT NULL,
        EntityType       NVARCHAR(120)     NULL,
        EntityId         BIGINT            NULL,
        PayloadJson      NVARCHAR(MAX)     NOT NULL,   -- the full NotificationRequest
        CompanyId        BIGINT            NULL,
        Status           TINYINT           NOT NULL CONSTRAINT DF_NotificationOutbox_Status DEFAULT (0),
        AttemptCount     INT               NOT NULL CONSTRAINT DF_NotificationOutbox_AttemptCount DEFAULT (0),
        MaxAttempts      INT               NOT NULL,
        NextAttemptAtUtc DATETIME2(3)      NOT NULL,
        LeaseId          UNIQUEIDENTIFIER  NULL,
        LeasedUntilUtc   DATETIME2(3)      NULL,
        LastError        NVARCHAR(2000)    NULL,
        ProcessedAtUtc   DATETIME2(3)      NULL,
        NotificationId   BIGINT            NULL,       -- the Notification row the delivery created (set by _MarkPersisted)
        -- standard v2 audit columns; CreatedBy/CreatedAtUtc are the actor and time of the raising request, and are
        -- what Notification_Create is later called with as @CreatedBy/@CreatedAt
        CreatedAtUtc     DATETIME2(3)      NOT NULL,
        CreatedBy        BIGINT            NULL,
        ModifiedAtUtc    DATETIME2(3)      NULL,
        ModifiedBy       BIGINT            NULL,
        IsDeleted        BIT               NOT NULL CONSTRAINT DF_NotificationOutbox_IsDeleted DEFAULT (0),
        DeletedAtUtc     DATETIME2(3)      NULL,
        DeletedBy        BIGINT            NULL,
        RowVersion       ROWVERSION        NOT NULL,
        CONSTRAINT CK_NotificationOutbox_Status CHECK (Status IN (0, 1, 2))
    );

    CREATE INDEX IX_NotificationOutbox_Due ON dbo.NotificationOutbox (Status, NextAttemptAtUtc) INCLUDE (LeasedUntilUtc, IsDeleted);
    CREATE INDEX IX_NotificationOutbox_Processed ON dbo.NotificationOutbox (Status, ProcessedAtUtc);
END
GO

CREATE OR ALTER PROCEDURE dbo.NotificationOutbox_Enqueue
    @TypeCode    VARCHAR(80),
    @EntityType  NVARCHAR(120) = NULL,
    @EntityId    BIGINT = NULL,
    @PayloadJson NVARCHAR(MAX),
    @MaxAttempts INT,
    @CreatedBy   BIGINT,
    @CreatedAt   DATETIME,
    @CompanyId   BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.NotificationOutbox
        (TypeCode, EntityType, EntityId, PayloadJson, CompanyId, MaxAttempts, NextAttemptAtUtc, CreatedAtUtc, CreatedBy)
    VALUES
        (@TypeCode, @EntityType, @EntityId, @PayloadJson, @CompanyId,
         CASE WHEN @MaxAttempts BETWEEN 1 AND 50 THEN @MaxAttempts ELSE 5 END,
         SYSUTCDATETIME(), @CreatedAt, @CreatedBy);

    SELECT CAST(SCOPE_IDENTITY() AS BIGINT) AS OutboxId;
END
GO

CREATE OR ALTER PROCEDURE dbo.NotificationOutbox_ClaimById
    @OutboxId     BIGINT,
    @LeaseId      UNIQUEIDENTIFIER = NULL,
    @LeaseSeconds INT = 300
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();

    IF @LeaseId IS NOT NULL
    BEGIN
        -- Already claimed by the sweep: hand the row over only while that lease still owns it. The attempt is
        -- counted here, when processing starts - not by the sweep, so leases that expire while the consumer is down
        -- do not burn the retry budget.
        UPDATE dbo.NotificationOutbox
           SET AttemptCount = AttemptCount + 1,
               ModifiedAtUtc = @NowUtc
        OUTPUT inserted.OutboxId, inserted.LeaseId, inserted.AttemptCount, inserted.MaxAttempts, inserted.TypeCode,
               inserted.PayloadJson, inserted.CreatedBy, inserted.CompanyId, inserted.CreatedAtUtc, inserted.NotificationId
         WHERE OutboxId = @OutboxId AND Status = 0 AND IsDeleted = 0
           AND LeaseId = @LeaseId AND LeasedUntilUtc > @NowUtc;
        RETURN;
    END

    SET @LeaseSeconds = CASE WHEN @LeaseSeconds BETWEEN 30 AND 3600 THEN @LeaseSeconds ELSE 300 END;
    DECLARE @NewLeaseId UNIQUEIDENTIFIER = NEWID();

    UPDATE dbo.NotificationOutbox WITH (ROWLOCK, READPAST)
       SET LeaseId = @NewLeaseId,
           LeasedUntilUtc = DATEADD(SECOND, @LeaseSeconds, @NowUtc),
           AttemptCount = AttemptCount + 1,
           ModifiedAtUtc = @NowUtc
    OUTPUT inserted.OutboxId, inserted.LeaseId, inserted.AttemptCount, inserted.MaxAttempts, inserted.TypeCode,
           inserted.PayloadJson, inserted.CreatedBy, inserted.CompanyId, inserted.CreatedAtUtc, inserted.NotificationId
     WHERE OutboxId = @OutboxId AND Status = 0 AND IsDeleted = 0
       AND NextAttemptAtUtc <= @NowUtc
       AND (LeasedUntilUtc IS NULL OR LeasedUntilUtc <= @NowUtc);
END
GO

CREATE OR ALTER PROCEDURE dbo.NotificationOutbox_ClaimDue
    @BatchSize     INT = 50,
    @LeaseSeconds  INT = 600,
    @MinAgeSeconds INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @BatchSize = CASE WHEN @BatchSize BETWEEN 1 AND 500 THEN @BatchSize ELSE 50 END;
    SET @LeaseSeconds = CASE WHEN @LeaseSeconds BETWEEN 30 AND 3600 THEN @LeaseSeconds ELSE 600 END;
    SET @MinAgeSeconds = CASE WHEN @MinAgeSeconds BETWEEN 0 AND 3600 THEN @MinAgeSeconds ELSE 30 END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @DueBefore DATETIME2(3) = DATEADD(SECOND, -@MinAgeSeconds, @NowUtc);
    DECLARE @LeaseId UNIQUEIDENTIFIER = NEWID();

    ;WITH Claimable AS
    (
        SELECT TOP (@BatchSize) *
        FROM dbo.NotificationOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE Status = 0 AND IsDeleted = 0
          AND NextAttemptAtUtc <= @DueBefore
          AND (LeasedUntilUtc IS NULL OR LeasedUntilUtc <= @NowUtc)
        ORDER BY NextAttemptAtUtc, OutboxId
    )
    UPDATE Claimable
       SET LeaseId = @LeaseId,
           LeasedUntilUtc = DATEADD(SECOND, @LeaseSeconds, @NowUtc),
           ModifiedAtUtc = @NowUtc
    OUTPUT inserted.OutboxId, inserted.LeaseId;
END
GO

-- Called in the SAME transaction as Notification_Create (NotificationOutboxRepository.PersistAsync): stamps the
-- notification on the row so a retry never creates it twice. 0 rows = the lease was lost -> the caller rolls back.
CREATE OR ALTER PROCEDURE dbo.NotificationOutbox_MarkPersisted
    @OutboxId       BIGINT,
    @LeaseId        UNIQUEIDENTIFIER,
    @NotificationId BIGINT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.NotificationOutbox
       SET NotificationId = @NotificationId,
           ModifiedAtUtc = SYSUTCDATETIME()
     WHERE OutboxId = @OutboxId AND LeaseId = @LeaseId AND Status = 0
       AND LeasedUntilUtc > SYSUTCDATETIME() AND NotificationId IS NULL;

    SELECT @@ROWCOUNT AS Affected;
END
GO

CREATE OR ALTER PROCEDURE dbo.NotificationOutbox_MarkSucceeded
    @OutboxId       BIGINT,
    @LeaseId        UNIQUEIDENTIFIER,
    @NotificationId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();

    UPDATE dbo.NotificationOutbox
       SET Status = 1,
           ProcessedAtUtc = @NowUtc,
           NotificationId = @NotificationId,
           LeaseId = NULL,
           LeasedUntilUtc = NULL,
           LastError = NULL,
           ModifiedAtUtc = @NowUtc
     WHERE OutboxId = @OutboxId AND LeaseId = @LeaseId AND Status = 0;

    SELECT @@ROWCOUNT AS Affected;
END
GO

CREATE OR ALTER PROCEDURE dbo.NotificationOutbox_MarkFailed
    @OutboxId          BIGINT,
    @LeaseId           UNIQUEIDENTIFIER,
    @LastError         NVARCHAR(2000),
    @DeadLetter        BIT,
    @RetryDelaySeconds INT = 60
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();

    UPDATE dbo.NotificationOutbox
       SET Status = CASE WHEN @DeadLetter = 1 THEN 2 ELSE 0 END,
           NextAttemptAtUtc = CASE WHEN @DeadLetter = 1 THEN NextAttemptAtUtc
                                   ELSE DATEADD(SECOND, CASE WHEN @RetryDelaySeconds > 0 THEN @RetryDelaySeconds ELSE 60 END, @NowUtc) END,
           ProcessedAtUtc = CASE WHEN @DeadLetter = 1 THEN @NowUtc ELSE NULL END,
           LeaseId = NULL,
           LeasedUntilUtc = NULL,
           LastError = LEFT(@LastError, 2000),
           ModifiedAtUtc = @NowUtc
     WHERE OutboxId = @OutboxId AND LeaseId = @LeaseId AND Status = 0;

    SELECT @@ROWCOUNT AS Affected;
END
GO

CREATE OR ALTER PROCEDURE dbo.NotificationOutbox_Purge
    @RetainDays INT = 14,
    @BatchSize  INT = 1000
AS
BEGIN
    SET NOCOUNT ON;
    SET @RetainDays = CASE WHEN @RetainDays >= 1 THEN @RetainDays ELSE 14 END;
    SET @BatchSize = CASE WHEN @BatchSize BETWEEN 1 AND 10000 THEN @BatchSize ELSE 1000 END;

    DELETE TOP (@BatchSize) FROM dbo.NotificationOutbox
     WHERE Status = 1 AND ProcessedAtUtc < DATEADD(DAY, -@RetainDays, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS Deleted;
END
GO

---------------------------------------------------------------------------------------------------------------------
-- EmailOutbox (a separate pipeline: own table, own queue, own retry budget)
---------------------------------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.EmailOutbox', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmailOutbox
    (
        OutboxId         BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_EmailOutbox PRIMARY KEY,
        ToAddress        NVARCHAR(320)     NOT NULL,
        Subject          NVARCHAR(400)     NOT NULL,
        HtmlBody         NVARCHAR(MAX)     NOT NULL,
        Category         NVARCHAR(100)     NULL,
        CompanyId        BIGINT            NULL,
        Status           TINYINT           NOT NULL CONSTRAINT DF_EmailOutbox_Status DEFAULT (0),
        AttemptCount     INT               NOT NULL CONSTRAINT DF_EmailOutbox_AttemptCount DEFAULT (0),
        MaxAttempts      INT               NOT NULL,
        NextAttemptAtUtc DATETIME2(3)      NOT NULL,
        LeaseId          UNIQUEIDENTIFIER  NULL,
        LeasedUntilUtc   DATETIME2(3)      NULL,
        LastError        NVARCHAR(2000)    NULL,
        ProcessedAtUtc   DATETIME2(3)      NULL,
        CreatedAtUtc     DATETIME2(3)      NOT NULL,
        CreatedBy        BIGINT            NULL,
        ModifiedAtUtc    DATETIME2(3)      NULL,
        ModifiedBy       BIGINT            NULL,
        IsDeleted        BIT               NOT NULL CONSTRAINT DF_EmailOutbox_IsDeleted DEFAULT (0),
        DeletedAtUtc     DATETIME2(3)      NULL,
        DeletedBy        BIGINT            NULL,
        RowVersion       ROWVERSION        NOT NULL,
        CONSTRAINT CK_EmailOutbox_Status CHECK (Status IN (0, 1, 2))
    );

    CREATE INDEX IX_EmailOutbox_Due ON dbo.EmailOutbox (Status, NextAttemptAtUtc) INCLUDE (LeasedUntilUtc, IsDeleted);
    CREATE INDEX IX_EmailOutbox_Processed ON dbo.EmailOutbox (Status, ProcessedAtUtc);
END
GO

CREATE OR ALTER PROCEDURE dbo.EmailOutbox_Enqueue
    @ToAddress   NVARCHAR(320),
    @Subject     NVARCHAR(400),
    @HtmlBody    NVARCHAR(MAX),
    @Category    NVARCHAR(100) = NULL,
    @MaxAttempts INT,
    @CreatedBy   BIGINT,
    @CreatedAt   DATETIME,
    @CompanyId   BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.EmailOutbox
        (ToAddress, Subject, HtmlBody, Category, CompanyId, MaxAttempts, NextAttemptAtUtc, CreatedAtUtc, CreatedBy)
    VALUES
        (@ToAddress, @Subject, @HtmlBody, @Category, @CompanyId,
         CASE WHEN @MaxAttempts BETWEEN 1 AND 50 THEN @MaxAttempts ELSE 8 END,
         SYSUTCDATETIME(), @CreatedAt, @CreatedBy);

    SELECT CAST(SCOPE_IDENTITY() AS BIGINT) AS OutboxId;
END
GO

CREATE OR ALTER PROCEDURE dbo.EmailOutbox_ClaimById
    @OutboxId     BIGINT,
    @LeaseId      UNIQUEIDENTIFIER = NULL,
    @LeaseSeconds INT = 300
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();

    IF @LeaseId IS NOT NULL
    BEGIN
        -- Handed over from the sweep; the attempt is counted here (see NotificationOutbox_ClaimById).
        UPDATE dbo.EmailOutbox
           SET AttemptCount = AttemptCount + 1,
               ModifiedAtUtc = @NowUtc
        OUTPUT inserted.OutboxId, inserted.LeaseId, inserted.AttemptCount, inserted.MaxAttempts,
               inserted.ToAddress, inserted.Subject, inserted.HtmlBody, inserted.Category
         WHERE OutboxId = @OutboxId AND Status = 0 AND IsDeleted = 0
           AND LeaseId = @LeaseId AND LeasedUntilUtc > @NowUtc;
        RETURN;
    END

    SET @LeaseSeconds = CASE WHEN @LeaseSeconds BETWEEN 30 AND 3600 THEN @LeaseSeconds ELSE 300 END;
    DECLARE @NewLeaseId UNIQUEIDENTIFIER = NEWID();

    UPDATE dbo.EmailOutbox WITH (ROWLOCK, READPAST)
       SET LeaseId = @NewLeaseId,
           LeasedUntilUtc = DATEADD(SECOND, @LeaseSeconds, @NowUtc),
           AttemptCount = AttemptCount + 1,
           ModifiedAtUtc = @NowUtc
    OUTPUT inserted.OutboxId, inserted.LeaseId, inserted.AttemptCount, inserted.MaxAttempts,
           inserted.ToAddress, inserted.Subject, inserted.HtmlBody, inserted.Category
     WHERE OutboxId = @OutboxId AND Status = 0 AND IsDeleted = 0
       AND NextAttemptAtUtc <= @NowUtc
       AND (LeasedUntilUtc IS NULL OR LeasedUntilUtc <= @NowUtc);
END
GO

CREATE OR ALTER PROCEDURE dbo.EmailOutbox_ClaimDue
    @BatchSize     INT = 50,
    @LeaseSeconds  INT = 600,
    @MinAgeSeconds INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @BatchSize = CASE WHEN @BatchSize BETWEEN 1 AND 500 THEN @BatchSize ELSE 50 END;
    SET @LeaseSeconds = CASE WHEN @LeaseSeconds BETWEEN 30 AND 3600 THEN @LeaseSeconds ELSE 600 END;
    SET @MinAgeSeconds = CASE WHEN @MinAgeSeconds BETWEEN 0 AND 3600 THEN @MinAgeSeconds ELSE 30 END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @DueBefore DATETIME2(3) = DATEADD(SECOND, -@MinAgeSeconds, @NowUtc);
    DECLARE @LeaseId UNIQUEIDENTIFIER = NEWID();

    ;WITH Claimable AS
    (
        SELECT TOP (@BatchSize) *
        FROM dbo.EmailOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE Status = 0 AND IsDeleted = 0
          AND NextAttemptAtUtc <= @DueBefore
          AND (LeasedUntilUtc IS NULL OR LeasedUntilUtc <= @NowUtc)
        ORDER BY NextAttemptAtUtc, OutboxId
    )
    UPDATE Claimable
       SET LeaseId = @LeaseId,
           LeasedUntilUtc = DATEADD(SECOND, @LeaseSeconds, @NowUtc),
           ModifiedAtUtc = @NowUtc
    OUTPUT inserted.OutboxId, inserted.LeaseId;
END
GO

CREATE OR ALTER PROCEDURE dbo.EmailOutbox_MarkSucceeded
    @OutboxId BIGINT,
    @LeaseId  UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();

    UPDATE dbo.EmailOutbox
       SET Status = 1,
           ProcessedAtUtc = @NowUtc,
           LeaseId = NULL,
           LeasedUntilUtc = NULL,
           LastError = NULL,
           ModifiedAtUtc = @NowUtc
     WHERE OutboxId = @OutboxId AND LeaseId = @LeaseId AND Status = 0;

    SELECT @@ROWCOUNT AS Affected;
END
GO

CREATE OR ALTER PROCEDURE dbo.EmailOutbox_MarkFailed
    @OutboxId          BIGINT,
    @LeaseId           UNIQUEIDENTIFIER,
    @LastError         NVARCHAR(2000),
    @DeadLetter        BIT,
    @RetryDelaySeconds INT = 60
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();

    UPDATE dbo.EmailOutbox
       SET Status = CASE WHEN @DeadLetter = 1 THEN 2 ELSE 0 END,
           NextAttemptAtUtc = CASE WHEN @DeadLetter = 1 THEN NextAttemptAtUtc
                                   ELSE DATEADD(SECOND, CASE WHEN @RetryDelaySeconds > 0 THEN @RetryDelaySeconds ELSE 60 END, @NowUtc) END,
           ProcessedAtUtc = CASE WHEN @DeadLetter = 1 THEN @NowUtc ELSE NULL END,
           LeaseId = NULL,
           LeasedUntilUtc = NULL,
           LastError = LEFT(@LastError, 2000),
           ModifiedAtUtc = @NowUtc
     WHERE OutboxId = @OutboxId AND LeaseId = @LeaseId AND Status = 0;

    SELECT @@ROWCOUNT AS Affected;
END
GO

CREATE OR ALTER PROCEDURE dbo.EmailOutbox_Purge
    @RetainDays INT = 14,
    @BatchSize  INT = 1000
AS
BEGIN
    SET NOCOUNT ON;
    SET @RetainDays = CASE WHEN @RetainDays >= 1 THEN @RetainDays ELSE 14 END;
    SET @BatchSize = CASE WHEN @BatchSize BETWEEN 1 AND 10000 THEN @BatchSize ELSE 1000 END;

    DELETE TOP (@BatchSize) FROM dbo.EmailOutbox
     WHERE Status = 1 AND ProcessedAtUtc < DATEADD(DAY, -@RetainDays, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS Deleted;
END
GO

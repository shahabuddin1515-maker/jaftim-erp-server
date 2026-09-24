/*
    v2 - normalize the Customer/Contact distinction and the Inquiry -> party link. TENANT database. Additive, idempotent.

    WHY
    ---
    Today "contact vs customer" is a data-quality test re-evaluated inside CustomerProfileGetById,
    GetCustomerAllNew and GetInquiryAll (identical CASE in all three):

        CASE WHEN FullName = 'N/A' OR Email IS NULL OR Email NOT LIKE '%_@_%._%'
               OR Phone IS NULL OR LTRIM(RTRIM(Phone)) = '' THEN 1 ELSE 0 END AS IsContact

    i.e. a CONTACT is an unqualified, lead-stage party (typically created by the lead / Respond.io pipeline with a
    phone-derived pseudo-email) and a CUSTOMER is a qualified one. GetCustomerAllNew hides IsContact = 1, which is why
    leads never appear in the Customers list. (The legacy README's "secondary contact person under a business
    customer" reading is wrong - no IsContact column exists anywhere in this database.)

    That expression is non-indexable, repeated in three places, and silently reclassifies a party whenever someone
    edits an e-mail. Here it becomes UserProfile.PartyKind: stored, indexed, one definition, human-overridable, with
    qualification recorded as an event. The legacy CASE expressions are left untouched, so every existing screen and
    report keeps its current numbers.

    Inquiry is linked to its party by AspNetUserId (nvarchar(900)); this adds the real UserProfileId FK next to it.

    NOT DONE ON PURPOSE
    -------------------
    * No FILTERED indexes on UserProfile / Inquiry: a filtered index forces QUOTED_IDENTIFIER ON for every writer and
      this database still has ~58 modules compiled with it OFF (legacy README section 6.5).
    * No triggers and no changes to the ingestion chain (InsertLead -> InquiryImport_FromLead -> InquirySave_FromLead
      -> CustomerSaveInternal). The Azure Function keeps working untouched; the v2 API recomputes on its own writes
      and PartyKindSyncJob reconciles whatever the legacy paths write.
    * Inquiry.LeadId is still never written by the ingestion chain (a known legacy defect); this script does not
      invent the link. See docs/MIGRATION_INVENTORY.md.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================ 1. Party kind (Customer vs Contact)
IF COL_LENGTH('dbo.UserProfile', 'PartyKind') IS NULL
    ALTER TABLE dbo.UserProfile ADD
        PartyKind              TINYINT      NULL,   -- 1 = Contact (unqualified), 2 = Customer (qualified)
        PartyKindIsManual      BIT          NOT NULL CONSTRAINT DF_UserProfile_PartyKindIsManual DEFAULT (0),
        PartyQualifiedAtUtc    DATETIME2(3) NULL,   -- first time it became a Customer
        PartyKindModifiedAtUtc DATETIME2(3) NULL;
GO

-- Deliberately NOT filtered (see header).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_UserProfile_PartyKind' AND object_id = OBJECT_ID('dbo.UserProfile'))
    CREATE INDEX IX_UserProfile_PartyKind ON dbo.UserProfile (PartyKind, RoleId) INCLUDE (IsDeleted, StatusId);
GO

/*
    The single definition of "qualified". Inline TVF rather than a scalar function so it stays set-based and never
    becomes a per-row RBAR cost. Mirrors the legacy CASE exactly, including its quirks:
      - FullName = 'N/A' is the placeholder the lead pipeline writes when no name was supplied;
      - the e-mail test is a shape test, so the phone-derived pseudo-email (digits, no '@') counts as unqualified;
      - the phone lives on UserProfileDetail, which lead-created profiles do have.
*/
CREATE OR ALTER FUNCTION dbo.fn_PartyKind (@UserProfileId BIGINT)
RETURNS TABLE
AS
RETURN
(
    -- Only the customer role carries this distinction; staff profiles have no PartyKind.
    SELECT CASE WHEN up.RoleId <> 3 THEN NULL ELSE CAST(CASE
               WHEN up.FullName = 'N/A' OR up.Email IS NULL OR up.Email NOT LIKE '%_@_%._%'
                 OR upd.Phone IS NULL OR LTRIM(RTRIM(upd.Phone)) = ''
               THEN 1 ELSE 2
           END AS TINYINT) END AS PartyKind
    FROM dbo.UserProfile up
    LEFT JOIN dbo.UserProfileDetail upd ON upd.UserProfileId = up.UserProfileId
    WHERE up.UserProfileId = @UserProfileId
);
GO

/*
    Recompute one party after a write. A manual override (PartyKindIsManual = 1) is never overwritten.
    Returns the row so the caller can tell whether this write qualified the party (and raise the audit event).
*/
CREATE OR ALTER PROCEDURE dbo.Party_RecomputeKind
    @UserProfileId BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @computed TINYINT = (SELECT TOP 1 PartyKind FROM dbo.fn_PartyKind(@UserProfileId));

    UPDATE up
    SET PartyKind              = @computed,
        PartyQualifiedAtUtc    = CASE WHEN @computed = 2 AND up.PartyQualifiedAtUtc IS NULL THEN @now ELSE up.PartyQualifiedAtUtc END,
        PartyKindModifiedAtUtc = @now
    FROM dbo.UserProfile up
    WHERE up.UserProfileId = @UserProfileId
      AND up.PartyKindIsManual = 0
      AND (up.PartyKind IS NULL OR up.PartyKind <> @computed);

    SELECT UserProfileId, PartyKind, PartyKindIsManual, PartyQualifiedAtUtc,
           CAST(CASE WHEN @@ROWCOUNT > 0 THEN 1 ELSE 0 END AS BIT) AS Changed
    FROM dbo.UserProfile WHERE UserProfileId = @UserProfileId;
END
GO

-- A human decision ("this IS a customer, we simply have no e-mail for them") - pins the kind against the rule.
CREATE OR ALTER PROCEDURE dbo.Party_SetKindManual
    @UserProfileId BIGINT, @PartyKind TINYINT, @IsManual BIT = 1, @ActorUserProfileId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
    UPDATE dbo.UserProfile
    SET PartyKind              = @PartyKind,
        PartyKindIsManual      = @IsManual,
        PartyQualifiedAtUtc    = CASE WHEN @PartyKind = 2 AND PartyQualifiedAtUtc IS NULL THEN @now ELSE PartyQualifiedAtUtc END,
        PartyKindModifiedAtUtc = @now,
        ModifiedBy             = ISNULL(@ActorUserProfileId, ModifiedBy),
        ModifiedAt             = GETUTCDATE()
    WHERE UserProfileId = @UserProfileId;

    -- Releasing a manual pin re-applies the rule immediately.
    IF @IsManual = 0 EXEC dbo.Party_RecomputeKind @UserProfileId = @UserProfileId;
    ELSE SELECT UserProfileId, PartyKind, PartyKindIsManual, PartyQualifiedAtUtc, CAST(1 AS BIT) AS Changed
         FROM dbo.UserProfile WHERE UserProfileId = @UserProfileId;
END
GO

/*
    Bulk reconciliation for PartyKindSyncJob: picks up everything the legacy app and the ingestion chain wrote.
    Set-based and cheap (about 1k customer rows). Manual overrides are skipped.
*/
CREATE OR ALTER PROCEDURE dbo.Party_ReconcileKinds
    @BatchSize INT = 5000
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();

    ;WITH computed AS
    (
        SELECT TOP (@BatchSize)
               up.UserProfileId, up.PartyKind AS CurrentKind, up.PartyQualifiedAtUtc,
               CAST(CASE
                   WHEN up.FullName = 'N/A' OR up.Email IS NULL OR up.Email NOT LIKE '%_@_%._%'
                     OR upd.Phone IS NULL OR LTRIM(RTRIM(upd.Phone)) = ''
                   THEN 1 ELSE 2
               END AS TINYINT) AS NewKind
        FROM dbo.UserProfile up
        LEFT JOIN dbo.UserProfileDetail upd ON upd.UserProfileId = up.UserProfileId
        WHERE up.PartyKindIsManual = 0
          AND up.RoleId = 3          -- only the customer role carries this distinction
    )
    UPDATE up
    SET PartyKind              = c.NewKind,
        PartyQualifiedAtUtc    = CASE WHEN c.NewKind = 2 AND up.PartyQualifiedAtUtc IS NULL THEN @now ELSE up.PartyQualifiedAtUtc END,
        PartyKindModifiedAtUtc = @now
    FROM dbo.UserProfile up
    INNER JOIN computed c ON c.UserProfileId = up.UserProfileId
    WHERE c.CurrentKind IS NULL OR c.CurrentKind <> c.NewKind;

    DECLARE @changed INT = @@ROWCOUNT;

    -- A profile that stopped being a customer (or was stamped before this rule was scoped) loses its kind.
    UPDATE dbo.UserProfile
    SET PartyKind = NULL, PartyQualifiedAtUtc = NULL, PartyKindModifiedAtUtc = @now
    WHERE RoleId <> 3 AND PartyKind IS NOT NULL AND PartyKindIsManual = 0;

    SELECT @changed + @@ROWCOUNT AS Changed;
END
GO

-- Backfill on first run (and whenever this script is re-run).
EXEC dbo.Party_ReconcileKinds @BatchSize = 1000000;
GO

-- ============================================================ 2. Inquiry -> party, as a real FK
IF COL_LENGTH('dbo.Inquiry', 'UserProfileId') IS NULL
    ALTER TABLE dbo.Inquiry ADD UserProfileId BIGINT NULL;
GO

/*
    Populates Inquiry.UserProfileId from the legacy AspNetUserId link. Also used by PartyKindSyncJob, because the
    ingestion chain (untouched) only ever sets AspNetUserId. Inquiries with no matched party stay NULL - that is a
    real state (a brand-new lead before CustomerSaveInternal created anybody).
*/
CREATE OR ALTER PROCEDURE dbo.Inquiry_ReconcileUserProfileId
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE i
    SET UserProfileId = up.UserProfileId
    FROM dbo.Inquiry i
    INNER JOIN dbo.UserProfile up ON up.AspNetUserId = i.AspNetUserId
    WHERE i.AspNetUserId IS NOT NULL
      AND (i.UserProfileId IS NULL OR i.UserProfileId <> up.UserProfileId);

    SELECT @@ROWCOUNT AS Changed;
END
GO

EXEC dbo.Inquiry_ReconcileUserProfileId;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Inquiry_UserProfile')
    ALTER TABLE dbo.Inquiry ADD CONSTRAINT FK_Inquiry_UserProfile
        FOREIGN KEY (UserProfileId) REFERENCES dbo.UserProfile (UserProfileId);
GO

-- ============================================================ 3. Indexes GetInquiryAll actually needs
-- Inquiry had exactly two indexes (PK + RI_ContactId) while the list screen filters and sorts on these columns.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Inquiry_UserProfileId' AND object_id = OBJECT_ID('dbo.Inquiry'))
    CREATE INDEX IX_Inquiry_UserProfileId ON dbo.Inquiry (UserProfileId) INCLUDE (IsDeleted, CreatedAt);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Inquiry_AspNetUserId' AND object_id = OBJECT_ID('dbo.Inquiry'))
    CREATE INDEX IX_Inquiry_AspNetUserId ON dbo.Inquiry (AspNetUserId) INCLUDE (IsDeleted);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Inquiry_CreatedAt' AND object_id = OBJECT_ID('dbo.Inquiry'))
    CREATE INDEX IX_Inquiry_CreatedAt ON dbo.Inquiry (CreatedAt DESC) INCLUDE (IsDeleted, SourceId, CountryId, LeadStatusId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CustomerRemarks_Inquiry_Created' AND object_id = OBJECT_ID('dbo.CustomerRemarks'))
    CREATE INDEX IX_CustomerRemarks_Inquiry_Created ON dbo.CustomerRemarks (InquiryId, CreatedAt DESC);
GO

/*
    006_CustomerSave_InquiryGuard.sql
    ---------------------------------
    Repairs a live defect in dbo.CustomerSave that makes "add inquiry" impossible to complete for a NEW enquirer.

    CustomerSave carries this guard (added on the server, last modified on UAT 2026-09-08):

        -- Prevent a customer phone from duplicating another active inquiry.
        -- Exclude @InquiryId when converting that same inquiry into a customer.
        IF EXISTS (SELECT 1 FROM dbo.Inquiry I
                   WHERE ISNULL(I.IsDeleted, 0) = 0
                     AND (ISNULL(@UserProfileId, 0) = 0)
                     AND ((@Phone ... IN (I.Phone, I.SecondaryPhone, I.WhatsAppNumber)) OR ...))
            RAISERROR('Duplicate inquiry exists with same Phone.', 16, 1)

    The comment promises to exclude @InquiryId; the predicate never does. The add-inquiry flow is
    InquirySave (INSERTs the Inquiry row) -> create the login -> CustomerSave (@InquiryId = the row just
    inserted), so the guard always finds that very row and aborts. Net effect on UAT/Live today: the Inquiry row
    and the AspNetUsers login are committed, CustomerSave raises, and NO UserProfile (party) is ever created.
    The legacy screen hides it - InquiryController.InquirySave ends in `catch (Exception ex) { return null; }`.

    The lead-ingestion chain is unaffected: it uses CustomerSaveInternal, which has no such guard. The Azure
    Function keeps working untouched.

    The fix adds the exclusion the comment already describes. The guard still fires for a phone belonging to a
    DIFFERENT active inquiry, so the protection it was added for is kept intact. (Source control's copy at
    Jaftim/Database/Database Scripts/CustomerSave.sql instead comments the whole block out, dropping the
    protection entirely; that version was never deployed - UAT runs the guard active.)

    Self-adapting and idempotent: it patches whatever definition is deployed rather than restating the 180-line
    body, so it cannot silently revert an unrelated change made to the procedure in the meantime.
    Additive in spirit: one predicate narrows an existing guard; no schema, no signature, no behaviour change for
    any other caller.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @def      nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'dbo.CustomerSave')),
        @needle   nvarchar(200) = N'(ISNULL(@UserProfileId, 0) = 0)',
        @already  nvarchar(200) = N'I.InquiryId <> ISNULL(@InquiryId, 0)';

IF @def IS NULL
BEGIN
    RAISERROR('dbo.CustomerSave does not exist - nothing to patch.', 16, 1);
    RETURN;
END

IF CHARINDEX(@already, @def) > 0
BEGIN
    PRINT 'CustomerSave already excludes @InquiryId from the duplicate-phone guard - no change.';
    RETURN;
END

IF CHARINDEX(N'Duplicate inquiry exists with same Phone.', @def) = 0
BEGIN
    PRINT 'CustomerSave has no active duplicate-phone guard (it is commented out or absent) - no change.';
    RETURN;
END

-- The guard's own predicate appears exactly once; refuse to guess if that ever stops being true.
IF (LEN(@def) - LEN(REPLACE(@def, @needle, N''))) / LEN(@needle) <> 1
BEGIN
    RAISERROR('Expected exactly one occurrence of the duplicate-phone guard predicate in CustomerSave; found a different number. Patch not applied - inspect the procedure by hand.', 16, 1);
    RETURN;
END

SET @def = REPLACE(@def, @needle, @needle + N'
			AND I.InquiryId <> ISNULL(@InquiryId, 0)   -- v2/006: the inquiry being converted is not its own duplicate');

-- OBJECT_DEFINITION returns the text as submitted, so the header may read CREATE or ALTER (and may be preceded
-- by whitespace). Rebuild it as ALTER, refusing to run if the header is not the shape we expect.
DECLARE @proc int = CHARINDEX(N'PROC', @def);
-- LTRIM/RTRIM strip spaces only, and the stored text starts with CR/LF - flatten the whitespace first.
DECLARE @head nvarchar(100) = LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(
    LEFT(@def, CASE WHEN @proc > 0 THEN @proc - 1 ELSE 0 END), CHAR(13), N' '), CHAR(10), N' '), CHAR(9), N' ')));

IF @proc = 0 OR @head NOT IN (N'CREATE', N'ALTER')
BEGIN
    RAISERROR('CustomerSave does not begin with a plain CREATE/ALTER PROCEDURE header; patch not applied.', 16, 1);
    RETURN;
END

SET @def = N'ALTER ' + SUBSTRING(@def, @proc, LEN(@def));

EXEC sys.sp_executesql @def;
PRINT 'CustomerSave patched: the duplicate-phone guard now excludes @InquiryId.';
GO

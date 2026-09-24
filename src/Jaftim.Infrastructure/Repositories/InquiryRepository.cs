using System.Data;
using Dapper;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Inquiries;
using Jaftim.Domain.Entities.Inquiries;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>
/// Inquiry procedures, parameter lists copied from the legacy InquiryController/UserRepository call sites, plus the
/// v2 party-classification procedures (database/v2/005). The ingestion chain (InsertLead -> InquiryImport_FromLead
/// -> InquirySave_FromLead -> CustomerSaveInternal) is NOT called from here - the Azure Function owns it untouched.
/// </summary>
public sealed class InquiryRepository(IDbExecutor db) : IInquiryRepository
{
    public Task<PagedResult<InquiryListItem>> GetAllAsync(InquiryListRequest request, CancellationToken ct = default)
    {
        SpCall call = SpCall.Procedure("GetInquiryAll")
            .With("@InquiryId", request.InquiryId)
            .With("@CMID", request.UserProfileId)
            .With("@Name", request.Name, DbType.String, 200)
            .With("@Email", request.Email, DbType.String, 200)
            .With("@Number", request.Number, DbType.String, 50)
            .With("@CreatedByName", request.CreatedByName, DbType.String, 200)
            .With("@CountryId", request.CountryId)
            .With("@SourceId", request.SourceId)
            .With("@DateFrom", request.DateFrom?.ToDateTime(TimeOnly.MinValue), DbType.Date)
            .With("@DateTo", request.DateTo?.ToDateTime(TimeOnly.MinValue), DbType.Date)
            .With("@LeadStatusId", request.LeadStatusId)
            .With("@IsContacted", request.IsContacted)
            // The procedure's @IsContact is 1 = unqualified party, which is exactly PartyKind.Contact.
            .With("@IsContact", request.PartyKind is null ? null : request.PartyKind == PartyKind.Contact ? 1 : 0)
            .With("@IsTagged", request.IsTagged)
            .With("@AgentId", request.AgentId)
            .With("@followUpStatus", request.FollowUpStatus)
            .With("@PageNumber", request.Page)
            .With("@PageSize", request.PageSize);

        return db.QueryMultipleAsync(call, async grid =>
        {
            IReadOnlyList<InquiryListItem> rows = (await grid.ReadAsync<InquiryListItem>()).AsList();
            int total = await grid.ReadFirstOrDefaultAsync<int>();
            return new PagedResult<InquiryListItem>(rows, request.Page, request.PageSize, total);
        }, ct);
    }

    public Task<IReadOnlyList<InquirySectionValue>> GetSectionsAsync(long inquiryId, CancellationToken ct = default) =>
        db.QueryAsync<InquirySectionValue>(SpCall.Procedure("Inquiry_GetSectionById").With("@Inquiry_Id", inquiryId), ct);

    public Task<IReadOnlyList<InquirySectionValue>> GetPartySectionsAsync(long userProfileId, CancellationToken ct = default) =>
        db.QueryAsync<InquirySectionValue>(SpCall.Procedure("Contact_GetSectionById").With("@UserProfile_Id", userProfileId), ct);

    public Task<IReadOnlyList<InquiryListItem>> GetByPartyAsync(long userProfileId, CancellationToken ct = default) =>
        db.QueryAsync<InquiryListItem>(SpCall.Procedure("CustomerInquiries").With("@CustomerId", userProfileId), ct);

    public Task SaveContactStatusAsync(long inquiryId, long userProfileId, int statusId, string? remarks, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("Inquiry_ContactStatusSave")
                .With("@customerId", userProfileId)
                .With("@inquiryId", inquiryId)
                .With("@status", statusId)
                .With("@remarks", remarks, DbType.String), ct);

    public Task TagToAgentAsync(long inquiryId, long agentId, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("Inquiry_TaggedFromInquiries")
                .With("@agentId", agentId)
                .With("@inquiryId", inquiryId), ct);

    public Task UntagCustomerFromAgentAsync(long customerId, long agentId, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("CustomerTagging_UnTagCustomerFromAgent")
                .With("@customerId", customerId)
                .With("@agentId", agentId), ct);

    public Task<IReadOnlyList<CustomerInteraction>> GetInteractionsAsync(long userProfileId, CancellationToken ct = default) =>
        db.QueryAsync<CustomerInteraction>(SpCall.Procedure("CustomerContact_GetByCustomerId").With("@CustomerId", userProfileId), ct);

    public Task LogInteractionAsync(long userProfileId, LogInteractionRequest request, DateTime contactedTimeUtc, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("CustomerContact_Save")
                .With("@ContactType", request.ContactType, DbType.String, 100)
                .With("@ContactMessage", request.ContactMessage, DbType.String)
                .With("@ContactedTime", contactedTimeUtc, DbType.DateTime)
                .With("@CustomerId", userProfileId), ct);

    // ----- party classification (v2) -----

    public Task<PartyClassification?> GetClassificationAsync(long userProfileId, CancellationToken ct = default) =>
        db.QueryFirstOrDefaultAsync<PartyClassification>(
            SpCall.Text(
                """
                SELECT TOP 1 UserProfileId, PartyKind, PartyKindIsManual, PartyQualifiedAtUtc, CAST(0 AS BIT) AS Changed
                FROM dbo.UserProfile WHERE UserProfileId = @Id
                """).With("@Id", userProfileId).WithoutAudit(), ct);

    public Task<PartyClassification> RecomputeClassificationAsync(long userProfileId, CancellationToken ct = default) =>
        db.QuerySingleAsync<PartyClassification>(
            SpCall.Procedure("Party_RecomputeKind").With("@UserProfileId", userProfileId).WithoutAudit(), ct);

    public Task<PartyClassification> SetClassificationAsync(long userProfileId, PartyKind kind, bool isManual, long actorUserProfileId, CancellationToken ct = default) =>
        db.QuerySingleAsync<PartyClassification>(
            SpCall.Procedure("Party_SetKindManual")
                .With("@UserProfileId", userProfileId)
                .With("@PartyKind", (byte)kind, DbType.Byte)
                .With("@IsManual", isManual)
                .With("@ActorUserProfileId", actorUserProfileId)
                .WithoutAudit(), ct);
}

/// <summary>
/// Write half of the inquiry module. The parameter lists are copied verbatim from the legacy
/// UserRepository.InquirySave / CustomerSave call sites; the procedures' own RAISERRORs (duplicate inquiry,
/// "Email and Phone found in different customers") reach the client as HTTP 422 via ApiExceptionHandler.
/// </summary>
public sealed class InquirySaveRepository(IDbExecutor db) : IInquirySaveRepository
{
    public Task<InquirySaveOutcome> SaveAsync(InquirySaveArgs a, NewPartyRegistration? newParty, CancellationToken ct = default) =>
        db.InTransactionAsync(async scope =>
        {
            InquirySaveResponse saved =
                await scope.QueryFirstOrDefaultAsync<InquirySaveResponse>(InquirySave(a), ct)
                ?? throw new InvalidOperationException("InquirySave returned no row.");

            long partyId = saved.ExistingUserProfileId;
            bool partyCreated = false;

            // Only a brand-new enquirer needs a login and a party; an existing one the procedure already matched.
            if (newParty is not null && partyId == 0)
            {
                await scope.ExecuteAsync(InsertAspNetUser(newParty), ct);
                await scope.ExecuteAsync(CustomerSave(newParty.Party with { InquiryId = saved.InquiryId }), ct);
                partyId = await scope.QueryFirstOrDefaultAsync<long?>(PartyIdByAccount(newParty.AccountId), ct) ?? 0;
                partyCreated = partyId > 0;
            }

            // v2: keep the normalised link current at write time. Inquiry_ReconcileUserProfileId (PartyKindSyncJob)
            // then only has the rows the legacy writers and the lead pipeline leave behind to backfill.
            if (partyId > 0)
                await scope.ExecuteAsync(LinkInquiryToParty(saved.InquiryId, partyId), ct);

            return new InquirySaveOutcome(saved.InquiryId, partyId, partyCreated);
        }, ct: ct);

    private static SpCall PartyIdByAccount(string accountId) =>
        SpCall.Text("SELECT TOP 1 UserProfileId FROM dbo.UserProfile WHERE AspNetUserId = @Id")
            .With("@Id", accountId, DbType.String, 450)
            .WithoutAudit();

    private static SpCall LinkInquiryToParty(long inquiryId, long userProfileId) =>
        SpCall.Text("UPDATE dbo.Inquiry SET UserProfileId = @PartyId WHERE InquiryId = @InquiryId AND UserProfileId IS NULL")
            .With("@PartyId", userProfileId)
            .With("@InquiryId", inquiryId)
            .WithoutAudit();

    private static SpCall InquirySave(InquirySaveArgs a) =>
            SpCall.Procedure("InquirySave")
                .With("@InquiryId", a.InquiryId)
                .With("@UserProfileId", a.UserProfileId)
                .With("@FullName", a.FullName, DbType.String)
                .With("@UserName", a.UserName, DbType.String)
                .With("@Email", a.Email, DbType.String)
                .With("@Phone", a.Phone, DbType.String)
                .With("@SecondaryPhone", a.SecondaryPhone, DbType.String)
                .With("@WhatsAppNumber", a.WhatsAppNumber, DbType.String)
                .With("@CustomerTypeId", a.CustomerTypeId)
                .With("@SourceId", a.SourceId)
                .With("@PortOfDischargeId", a.PortOfDischargeId)
                .With("@StatusId", a.StatusId)
                .With("@Message", a.Message, DbType.String)
                .With("@AspNetUserId", a.AspNetUserId, DbType.String, 450)
                .With("@RoleId", a.RoleId)
                .With("@GenderId", a.GenderId)
                .With("@CountryId", a.CountryId)
                .With("@AgentEmail", a.AgentEmail, DbType.String)
                .With("@JfCountry", a.JfCountry, DbType.String)
                .With("@JfPort", a.JfPort, DbType.String)
                .With("@StockID", a.StockId, DbType.String)
                .With("@StockURL", a.StockUrl, DbType.String)
                .With("@AdLink", a.AdLink, DbType.String);

    /// <summary>Same row IUserRepository.InsertAspNetUserAsync writes, issued here so it joins the transaction.</summary>
    private static SpCall InsertAspNetUser(NewPartyRegistration p) =>
        SpCall.Text(
            """
            INSERT INTO dbo.AspNetUsers (Id, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, PasswordHash,
                                         SecurityStamp, ConcurrencyStamp, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
            VALUES (@Id, @Email, UPPER(@Email), @Email, UPPER(@Email), 1, @Hash, @Stamp, @Stamp, 0, 0, 1, 0)
            """)
            .With("@Id", p.AccountId, DbType.String, 450)
            .With("@Email", p.Email, DbType.String, 256)
            .With("@Hash", p.PasswordHash)
            .With("@Stamp", Guid.NewGuid().ToString())
            .WithoutAudit();

    private static SpCall CustomerSave(CustomerSaveArgs a) =>
            SpCall.Procedure("CustomerSave")
                .With("@InquiryId", a.InquiryId)
                .With("@UserProfileId", a.UserProfileId)
                .With("@FullName", a.FullName, DbType.String)
                .With("@BusinessName", a.BusinessName, DbType.AnsiString, 200)
                .With("@UserName", a.UserName, DbType.String)
                .With("@Email", a.Email, DbType.String)
                .With("@LoginEmail", a.LoginEmail, DbType.AnsiString, 200)
                .With("@Phone", a.Phone, DbType.String)
                .With("@SecondaryPhone", a.SecondaryPhone, DbType.String)
                .With("@WhatsAppNumber", a.WhatsAppNumber, DbType.String)
                .With("@CustomerTypeId", a.CustomerTypeId)
                .With("@SourceId", a.SourceId)
                .With("@PortOfDischargeId", a.PortOfDischargeId)
                .With("@StatusId", a.StatusId)
                .With("@MinDepositRatioId", a.MinDepositRatioId)
                .With("@AspNetUserId", a.AspNetUserId, DbType.String, 450)
                .With("@RoleId", a.RoleId)
                .With("@GenderId", a.GenderId)
                .With("@PreferedEmail", a.PreferredEmail, DbType.Byte)
                .With("@CountryId", a.CountryId)
                .With("@AgentEmail", a.AgentEmail, DbType.String)
                .With("@IsGeneralEmail", a.IsGeneralEmail)
                .With("@IsMarketingEmail", a.IsMarketingEmail);

    public Task<PartyMatch?> FindPartyByEmailAsync(string email, CancellationToken ct = default) =>
        db.QueryFirstOrDefaultAsync<PartyMatch>(
            SpCall.Procedure("GetUserIdByEmail").With("@Email", email, DbType.String, 200), ct);

    public Task<PartyMatch?> FindPartyByPhoneAsync(string countryCode, string phone, CancellationToken ct = default) =>
        db.QueryFirstOrDefaultAsync<PartyMatch>(
            SpCall.Procedure("GetUserIdByPhone")
                .With("@CountryCode", countryCode, DbType.String, 20)
                .With("@Phone", phone, DbType.String, 100), ct);
}

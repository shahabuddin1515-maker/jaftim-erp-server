using FluentValidation;
using Jaftim.Domain.Entities.Inquiries;

namespace Jaftim.Application.Modules.Inquiries;

/// <summary>
/// Create or update an inquiry (legacy InquiryController.InquirySave form). Country codes are sent separately and
/// concatenated onto the numbers exactly as the legacy screen did.
///
/// <para><b>Email is optional on purpose.</b> When it is omitted the enquirer's phone digits become the e-mail and
/// username - the legacy "EmailNotRequired" rule. Such a party fails the qualification test and is therefore a
/// CONTACT, not a customer (see docs/INQUIRIES.md). Supplying a real e-mail creates a qualified CUSTOMER.</para>
/// </summary>
public sealed record SaveInquiryRequest(
    string? FullName,
    string PhoneCountryCode,
    string Phone,
    string? Email = null,
    string? SecondaryPhoneCountryCode = null,
    string? SecondaryPhone = null,
    string? WhatsAppNumberCountryCode = null,
    string? WhatsAppNumber = null,
    int? CustomerTypeId = null,
    int? SourceId = null,
    int? PortOfDischargeId = null,
    int? CountryId = null,
    int GenderId = 0,
    string? Message = null,
    string? AgentEmail = null,
    string? JfCountry = null,
    string? JfPort = null,
    string? StockId = null,
    string? StockUrl = null,
    string? AdLink = null,
    string? BusinessName = null);

public sealed class SaveInquiryRequestValidator : AbstractValidator<SaveInquiryRequest>
{
    public SaveInquiryRequestValidator()
    {
        RuleFor(x => x.Phone).NotEmpty().MaximumLength(50);
        RuleFor(x => x.PhoneCountryCode).NotEmpty().MaximumLength(10).Matches(@"^\+?\d+$")
            .WithMessage("phoneCountryCode must be digits, optionally prefixed with '+'.");
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.FullName).MaximumLength(200);
        RuleFor(x => x.Message).MaximumLength(4000);
        RuleFor(x => x.AgentEmail).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.AgentEmail));
        RuleFor(x => x.AdLink).MaximumLength(1000);
    }
}

/// <summary>What EXEC InquirySave returns: the inquiry, and the party it matched (0 when none existed).</summary>
public sealed class InquirySaveResponse
{
    public long InquiryId { get; set; }
    public long ExistingUserProfileId { get; set; }
}

/// <summary>
/// Result of the whole transactional create: the inquiry, and the party it now belongs to - matched by the
/// procedure or created here. <paramref name="UserProfileId"/> is 0 only when an update matched nothing.
/// </summary>
public sealed record InquirySaveOutcome(long InquiryId, long UserProfileId, bool PartyCreated);

/// <summary>The parameter set of EXEC InquirySave, copied from the legacy UserRepository.InquirySave call site.</summary>
public sealed record InquirySaveArgs(
    long InquiryId, long UserProfileId, string? FullName, string? UserName, string? Email, string Phone,
    string? SecondaryPhone, string? WhatsAppNumber, int? CustomerTypeId, int? SourceId, int? PortOfDischargeId,
    int StatusId, string? Message, string? AspNetUserId, long RoleId, int GenderId, int? CountryId,
    string? AgentEmail, string? JfCountry, string? JfPort, string? StockId, string? StockUrl, string? AdLink);

/// <summary>The parameter set of EXEC CustomerSave - the party the inquiry belongs to.</summary>
public sealed record CustomerSaveArgs(
    long InquiryId, long UserProfileId, string? FullName, string? BusinessName, string? UserName, string? Email,
    string? LoginEmail, string Phone, string? SecondaryPhone, string? WhatsAppNumber, int? CustomerTypeId,
    int? SourceId, int? PortOfDischargeId, int StatusId, int? MinDepositRatioId, string AspNetUserId, long RoleId,
    int GenderId, byte PreferredEmail, int? CountryId, string? AgentEmail, bool IsGeneralEmail, bool IsMarketingEmail);

/// <summary>Outcome of saving an inquiry.</summary>
public sealed record InquirySaveResult(
    long InquiryId,
    long? UserProfileId,
    PartyKind? PartyKind,
    bool PartyCreated);

/// <summary>A party matched by e-mail or phone - what the legacy CheckEmail/CheckPhone probes returned.</summary>
public sealed class PartyMatch
{
    public long UserProfileId { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
}

/// <summary>
/// The login and party to create if the inquiry turns out to belong to nobody yet. It is handed over up front so
/// the whole create can run in one transaction; the repository fills in the InquiryId that InquirySave allocates.
/// </summary>
public sealed record NewPartyRegistration(string AccountId, string Email, string PasswordHash, CustomerSaveArgs Party);

/// <summary>Save/lookup half of the inquiry module (kept apart from the read contracts for clarity).</summary>
public interface IInquirySaveRepository
{
    /// <summary>
    /// EXEC InquirySave and - only when that created a NEW inquiry which matched no existing party - the
    /// AspNetUsers login and EXEC CustomerSave, all inside ONE transaction, so a rejection from any step leaves
    /// nothing behind. The legacy screen ran the three unwrapped and swallowed the error, which is why the live
    /// database holds inquiries and logins with no party (see database/v2/006_CustomerSave_InquiryGuard.sql).
    /// It also sets the v2 Inquiry.UserProfileId link, so a new row is normalised the moment it is written rather
    /// than when Inquiry_ReconcileUserProfileId next sweeps.
    /// Raises 'Duplicate Inquiry...' / 'Email and Phone found in different customers.' as SQL errors (-> HTTP 422).
    /// </summary>
    Task<InquirySaveOutcome> SaveAsync(InquirySaveArgs args, NewPartyRegistration? newParty, CancellationToken ct = default);
    /// <summary>EXEC GetUserIdByEmail.</summary>
    Task<PartyMatch?> FindPartyByEmailAsync(string email, CancellationToken ct = default);
    /// <summary>EXEC GetUserIdByPhone.</summary>
    Task<PartyMatch?> FindPartyByPhoneAsync(string countryCode, string phone, CancellationToken ct = default);
}

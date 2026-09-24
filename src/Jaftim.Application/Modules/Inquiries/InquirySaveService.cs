using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Inquiries;
using Jaftim.Domain.Enums;
using Jaftim.Domain.Exceptions;
using Jaftim.Domain.Security;

namespace Jaftim.Application.Modules.Inquiries;

public interface IInquirySaveService
{
    Task<InquirySaveResult> CreateAsync(SaveInquiryRequest request, CancellationToken ct = default);
    Task<InquirySaveResult> UpdateAsync(long inquiryId, SaveInquiryRequest request, CancellationToken ct = default);
    /// <summary>Does a party already exist with this e-mail? (legacy CheckEmail probe on the add form)</summary>
    Task<PartyMatch?> FindByEmailAsync(string email, CancellationToken ct = default);
    /// <summary>Does a party already exist with this phone? (legacy CheckPhone probe)</summary>
    Task<PartyMatch?> FindByPhoneAsync(string countryCode, string phone, CancellationToken ct = default);
}

/// <summary>
/// Adding an inquiry, in the legacy order so the behaviour is identical:
///
///   1. force RoleId = Customer, StatusId = Active;
///   2. phone := countryCode + phone;
///   3. e-mail := the supplied address, or the phone digits when none was given (this is what makes the party a
///      CONTACT rather than a CUSTOMER - see docs/INQUIRIES.md); username := e-mail;
///   4. EXEC InquirySave - matches an existing party by e-mail / username / any phone field, rejects an enquiry whose
///      e-mail and phone belong to two different customers, then inserts the Inquiry row (tagging the agent and
///      recording CustomerPhoneNumbers when a party matched);
///   5. only when this created a NEW inquiry AND no party matched: create the login and EXEC CustomerSave;
///   6. re-apply the party qualification rule, so a real e-mail produces a Customer and a phone-only enquiry a Contact.
///
/// Deliberate departures from the legacy action, all approved in docs/REWRITE_PLAN.md section 7:
///   * the hardcoded shared password ("2342343&amp;", in source control, same for every customer ever created) is
///     replaced by a cryptographically random one per party;
///   * the legacy action swallowed every exception and returned null; here the procedure's RAISERRORs surface as 422;
///   * steps 4 and 5 run in ONE transaction. The legacy action ran them unwrapped, so a rejection from CustomerSave
///     left the inquiry and the login committed with no party behind them - the state a good number of live rows are
///     in, because CustomerSave's duplicate-phone guard rejects every new enquirer. See
///     database/v2/006_CustomerSave_InquiryGuard.sql for that defect and its repair.
/// The catalog account for the new party is created by AccountSyncJob on its next pass - customers do not sign in
/// through this back-office API, so nothing waits on it.
/// </summary>
public sealed class InquirySaveService(
    IInquirySaveRepository repository,
    IPartyService parties,
    IPasswordHasher hasher,
    IAuditWriter audit,
    IValidator<SaveInquiryRequest> validator) : IInquirySaveService
{
    public Task<InquirySaveResult> CreateAsync(SaveInquiryRequest request, CancellationToken ct = default) =>
        SaveAsync(0, request, ct);

    public Task<InquirySaveResult> UpdateAsync(long inquiryId, SaveInquiryRequest request, CancellationToken ct = default) =>
        inquiryId > 0 ? SaveAsync(inquiryId, request, ct) : throw new NotFoundException("Inquiry", inquiryId);

    public async Task<PartyMatch?> FindByEmailAsync(string email, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(email)
            ? throw new BusinessRuleException("email is required.")
            : await repository.FindPartyByEmailAsync(email.Trim(), ct);

    public async Task<PartyMatch?> FindByPhoneAsync(string countryCode, string phone, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(phone)
            ? throw new BusinessRuleException("phone is required.")
            : await repository.FindPartyByPhoneAsync(countryCode?.Trim() ?? string.Empty, phone.Trim(), ct);

    private async Task<InquirySaveResult> SaveAsync(long inquiryId, SaveInquiryRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAppAsync(request, ct);

        string phone = Combine(request.PhoneCountryCode, request.Phone);
        string email = string.IsNullOrWhiteSpace(request.Email) ? phone.Replace("+", string.Empty) : request.Email.Trim();
        string? secondaryPhone = Combine(request.SecondaryPhoneCountryCode, request.SecondaryPhone);
        string? whatsApp = Combine(request.WhatsAppNumberCountryCode, request.WhatsAppNumber);

        // Prepared up front so the repository can run the inquiry, the login and the party in one transaction. It is
        // only used when InquirySave reports a new inquiry that matched no existing party; an update never creates one.
        string accountId = Guid.NewGuid().ToString();
        NewPartyRegistration? registration = inquiryId != 0 ? null : new NewPartyRegistration(
            accountId, email, hasher.Hash(GenerateInitialPassword()),
            // CustomerSave declares no parameter defaults, so every one is supplied. The values not on the inquiry
            // form take the same defaults the legacy customer screen posted for a brand-new enquirer. InquiryId is
            // filled in by the repository once InquirySave has allocated it.
            new CustomerSaveArgs(
                InquiryId: 0, UserProfileId: 0, request.FullName, request.BusinessName, UserName: email, email,
                LoginEmail: email, phone, secondaryPhone, whatsApp, request.CustomerTypeId, request.SourceId,
                request.PortOfDischargeId, (int)UserStatus.Active, MinDepositRatioId: null, accountId,
                RoleIds.Customer, request.GenderId, PreferredEmail: 0, request.CountryId, request.AgentEmail,
                IsGeneralEmail: false, IsMarketingEmail: false));

        InquirySaveOutcome saved = await repository.SaveAsync(new InquirySaveArgs(
            inquiryId, UserProfileId: 0, request.FullName, UserName: email, email, phone,
            secondaryPhone, whatsApp, request.CustomerTypeId, request.SourceId, request.PortOfDischargeId,
            (int)UserStatus.Active, request.Message, AspNetUserId: null, RoleIds.Customer, request.GenderId,
            request.CountryId, request.AgentEmail, request.JfCountry, request.JfPort,
            request.StockId, request.StockUrl, request.AdLink), registration, ct);

        long? partyId = saved.UserProfileId > 0 ? saved.UserProfileId : null;
        bool partyCreated = saved.PartyCreated;

        PartyKind? kind = partyId is { } id ? (await parties.RefreshClassificationAsync(id, ct)).PartyKind : null;

        await audit.RecordAsync(inquiryId == 0 ? "Inquiry.Created" : "Inquiry.Updated", "Inquiry", saved.InquiryId,
            inquiryId == 0 ? null : new { InquiryId = inquiryId },
            new { saved.InquiryId, UserProfileId = partyId, PartyCreated = partyCreated, request.FullName, Email = email, Phone = phone, request.SourceId }, ct);

        return new InquirySaveResult(saved.InquiryId, partyId, kind, partyCreated);
    }

    /// <summary>Country code and number are stored concatenated, as the legacy screen posted them.</summary>
    private static string Combine(string? countryCode, string? number) =>
        string.IsNullOrWhiteSpace(number) ? string.Empty : $"{countryCode?.Trim()}{number.Trim()}";

    /// <summary>
    /// Replaces the legacy shared constant. The value is never returned or e-mailed: a customer who needs access
    /// uses the password-reset flow, exactly as one created by the lead pipeline does.
    /// </summary>
    private static string GenerateInitialPassword() => UserService.GenerateTemporaryPassword();
}

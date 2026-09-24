using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Inquiries;
using Jaftim.Domain.Entities.Inquiries;
using Jaftim.Domain.Enums;
using Jaftim.Domain.Security;
using Jaftim.Infrastructure.Security;

namespace Jaftim.Application.Tests;

/// <summary>
/// The add-inquiry rules carried over from the legacy InquiryController.InquirySave action: the phone becomes the
/// e-mail when none is supplied (which is what makes the party a Contact), country codes are concatenated, the role
/// and status are forced, and a party is only ever offered for creation on a CREATE that matched nobody.
/// </summary>
public sealed class InquirySaveServiceTests
{
    [Fact]
    public async Task Without_an_email_the_phone_digits_become_the_email_and_username()
    {
        var repo = new SaveRepoFake { Response = new InquirySaveOutcome(10, 500, PartyCreated: true) };
        IInquirySaveService service = Build(repo, out _, out PartyStub party);
        party.Kind = PartyKind.Contact;

        InquirySaveResult result = await service.CreateAsync(Request(phone: "501234567"));

        // '+' is stripped from the e-mail but kept on the phone, exactly as the legacy action did.
        Assert.Equal("971501234567", repo.LastArgs!.Email);
        Assert.Equal("971501234567", repo.LastArgs.UserName);
        Assert.Equal("+971501234567", repo.LastArgs.Phone);
        Assert.Equal(PartyKind.Contact, result.PartyKind);
    }

    [Fact]
    public async Task A_supplied_email_is_used_verbatim_and_trimmed()
    {
        var repo = new SaveRepoFake { Response = new InquirySaveOutcome(11, 501, PartyCreated: true) };
        IInquirySaveService service = Build(repo, out _, out PartyStub party);
        party.Kind = PartyKind.Customer;

        InquirySaveResult result = await service.CreateAsync(Request(email: "  Buyer@Example.com  "));

        Assert.Equal("Buyer@Example.com", repo.LastArgs!.Email);
        Assert.Equal("Buyer@Example.com", repo.LastArgs.UserName);
        Assert.Equal(PartyKind.Customer, result.PartyKind);
    }

    [Fact]
    public async Task Create_forces_the_customer_role_and_active_status()
    {
        var repo = new SaveRepoFake { Response = new InquirySaveOutcome(12, 502, PartyCreated: true) };
        IInquirySaveService service = Build(repo, out _, out _);

        await service.CreateAsync(Request());

        Assert.Equal(RoleIds.Customer, repo.LastArgs!.RoleId);
        Assert.Equal((int)UserStatus.Active, repo.LastArgs.StatusId);
        Assert.Equal(0, repo.LastArgs.InquiryId);
    }

    [Fact]
    public async Task Create_offers_a_party_registration_that_mirrors_the_inquiry()
    {
        var repo = new SaveRepoFake { Response = new InquirySaveOutcome(13, 503, PartyCreated: true) };
        IInquirySaveService service = Build(repo, out _, out _);

        await service.CreateAsync(Request(email: "buyer@example.com", businessName: "Acme Ltd"));

        NewPartyRegistration registration = Assert.IsType<NewPartyRegistration>(repo.LastRegistration);
        Assert.Equal("buyer@example.com", registration.Email);
        Assert.Equal("Acme Ltd", registration.Party.BusinessName);
        Assert.Equal(registration.AccountId, registration.Party.AspNetUserId);
        Assert.Equal(RoleIds.Customer, registration.Party.RoleId);
        // The password is generated, never the legacy shared constant, and only ever reaches the database hashed.
        Assert.NotEqual("2342343&", registration.PasswordHash);
        Assert.NotEmpty(registration.PasswordHash);
    }

    [Fact]
    public async Task An_update_never_offers_to_create_a_party()
    {
        var repo = new SaveRepoFake { Response = new InquirySaveOutcome(14, 504, PartyCreated: false) };
        IInquirySaveService service = Build(repo, out _, out _);

        InquirySaveResult result = await service.UpdateAsync(14, Request());

        Assert.Null(repo.LastRegistration);
        Assert.Equal(14, repo.LastArgs!.InquiryId);
        Assert.False(result.PartyCreated);
    }

    [Fact]
    public async Task Saving_reclassifies_the_party_and_audits_the_right_action()
    {
        var repo = new SaveRepoFake { Response = new InquirySaveOutcome(15, 505, PartyCreated: true) };
        IInquirySaveService service = Build(repo, out AuditSpy audit, out PartyStub party);

        await service.CreateAsync(Request());
        Assert.Contains(505, party.Refreshed);
        Assert.Equal(["Inquiry.Created"], audit.Actions);

        await service.UpdateAsync(15, Request());
        Assert.Equal(["Inquiry.Created", "Inquiry.Updated"], audit.Actions);
    }

    [Fact]
    public async Task A_save_that_matched_no_party_reports_no_classification()
    {
        var repo = new SaveRepoFake { Response = new InquirySaveOutcome(16, 0, PartyCreated: false) };
        IInquirySaveService service = Build(repo, out _, out PartyStub party);

        InquirySaveResult result = await service.CreateAsync(Request());

        Assert.Null(result.UserProfileId);
        Assert.Null(result.PartyKind);
        Assert.Empty(party.Refreshed);
    }

    [Theory]
    [InlineData("", "+971")]          // no phone
    [InlineData("501234567", "")]     // no country code
    [InlineData("501234567", "97a")]  // country code is not digits
    public async Task Invalid_phone_details_are_rejected_before_the_database_is_touched(string phone, string code)
    {
        var repo = new SaveRepoFake { Response = new InquirySaveOutcome(17, 0, PartyCreated: false) };
        IInquirySaveService service = Build(repo, out _, out _);

        await Assert.ThrowsAsync<Domain.Exceptions.ValidationException>(
            () => service.CreateAsync(Request(phone: phone, countryCode: code)));
        Assert.Null(repo.LastArgs);
    }

    [Fact]
    public async Task Lookups_require_something_to_look_up()
    {
        var repo = new SaveRepoFake { Response = new InquirySaveOutcome(18, 0, PartyCreated: false) };
        IInquirySaveService service = Build(repo, out _, out _);

        await Assert.ThrowsAsync<Domain.Exceptions.BusinessRuleException>(() => service.FindByEmailAsync("  "));
        await Assert.ThrowsAsync<Domain.Exceptions.BusinessRuleException>(() => service.FindByPhoneAsync("+971", ""));
    }

    private static SaveInquiryRequest Request(
        string phone = "501234567", string countryCode = "+971", string? email = null, string? businessName = null) =>
        new(FullName: "Test Enquirer", PhoneCountryCode: countryCode, Phone: phone, Email: email,
            SourceId: 1, CountryId: 174, BusinessName: businessName);

    private static IInquirySaveService Build(SaveRepoFake repo, out AuditSpy audit, out PartyStub party)
    {
        audit = new AuditSpy();
        party = new PartyStub();
        return new InquirySaveService(repo, party, new IdentityCompatiblePasswordHasher(), audit,
            new SaveInquiryRequestValidator());
    }

    private sealed class AuditSpy : IAuditWriter
    {
        public List<string> Actions { get; } = [];
        public ValueTask RecordAsync(string action, string entityType, long? entityId, object? before, object? after, CancellationToken ct = default)
        { Actions.Add(action); return ValueTask.CompletedTask; }
    }

    private sealed class PartyStub : IPartyService
    {
        public List<long> Refreshed { get; } = [];
        public PartyKind Kind { get; set; } = PartyKind.Contact;
        public Task<PartyClassification> GetClassificationAsync(long userProfileId, CancellationToken ct = default) =>
            Task.FromResult(new PartyClassification { UserProfileId = userProfileId, PartyKind = Kind });
        public Task<PartyClassification> RefreshClassificationAsync(long userProfileId, CancellationToken ct = default)
        { Refreshed.Add(userProfileId); return Task.FromResult(new PartyClassification { UserProfileId = userProfileId, PartyKind = Kind }); }
        public Task<PartyClassification> SetClassificationAsync(long userProfileId, SetPartyKindRequest request, CancellationToken ct = default) =>
            Task.FromResult(new PartyClassification());
    }

    private sealed class SaveRepoFake : IInquirySaveRepository
    {
        public required InquirySaveOutcome Response { get; init; }
        public InquirySaveArgs? LastArgs { get; private set; }
        public NewPartyRegistration? LastRegistration { get; private set; }

        public Task<InquirySaveOutcome> SaveAsync(InquirySaveArgs args, NewPartyRegistration? newParty, CancellationToken ct = default)
        {
            LastArgs = args;
            LastRegistration = newParty;
            return Task.FromResult(Response);
        }

        public Task<PartyMatch?> FindPartyByEmailAsync(string email, CancellationToken ct = default) => Task.FromResult<PartyMatch?>(null);
        public Task<PartyMatch?> FindPartyByPhoneAsync(string countryCode, string phone, CancellationToken ct = default) => Task.FromResult<PartyMatch?>(null);
    }
}

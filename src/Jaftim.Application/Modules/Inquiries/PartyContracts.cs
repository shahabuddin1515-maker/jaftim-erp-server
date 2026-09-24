using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Domain.Entities.Inquiries;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Modules.Inquiries;

/// <summary>Pin a party's kind against the qualification rule, or release the pin (`isManual = false`).</summary>
public sealed record SetPartyKindRequest(PartyKind PartyKind, bool IsManual = true, string? Reason = null);

public sealed class SetPartyKindRequestValidator : AbstractValidator<SetPartyKindRequest>
{
    public SetPartyKindRequestValidator()
    {
        RuleFor(x => x.PartyKind).IsInEnum();
        RuleFor(x => x.Reason).MaximumLength(400);
    }
}

/// <summary>
/// The Customer/Contact classification of a party. "Party" is the neutral word for a UserProfile with the customer
/// role: it is a CONTACT while unqualified (lead-stage) and a CUSTOMER once it has a real name, e-mail and phone.
/// </summary>
public interface IPartyService
{
    Task<PartyClassification> GetClassificationAsync(long userProfileId, CancellationToken ct = default);
    /// <summary>Re-applies the rule after a write; raises Customer.Qualified when the party crosses over.</summary>
    Task<PartyClassification> RefreshClassificationAsync(long userProfileId, CancellationToken ct = default);
    Task<PartyClassification> SetClassificationAsync(long userProfileId, SetPartyKindRequest request, CancellationToken ct = default);
}

public sealed class PartyService(
    IInquiryRepository repository,
    IAuditWriter audit,
    ICurrentUser actor,
    IValidator<SetPartyKindRequest> validator) : IPartyService
{
    public async Task<PartyClassification> GetClassificationAsync(long userProfileId, CancellationToken ct = default) =>
        await repository.GetClassificationAsync(userProfileId, ct) ?? throw new NotFoundException("Party", userProfileId);

    public async Task<PartyClassification> RefreshClassificationAsync(long userProfileId, CancellationToken ct = default)
    {
        PartyClassification before = await GetClassificationAsync(userProfileId, ct);
        PartyClassification after = await repository.RecomputeClassificationAsync(userProfileId, ct);

        // Crossing from Contact to Customer is a business event, not just a column change - record it as one.
        if (after.Changed && after.PartyKind == PartyKind.Customer && before.PartyKind != PartyKind.Customer)
            await audit.RecordAsync("Customer.Qualified", "UserProfile", userProfileId,
                new { before.PartyKind }, new { after.PartyKind, after.PartyQualifiedAtUtc }, ct);
        else if (after.Changed)
            await audit.RecordAsync("Party.KindRecomputed", "UserProfile", userProfileId,
                new { before.PartyKind }, new { after.PartyKind }, ct);

        return after;
    }

    public async Task<PartyClassification> SetClassificationAsync(long userProfileId, SetPartyKindRequest request, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAppAsync(request, ct);
        PartyClassification before = await GetClassificationAsync(userProfileId, ct);
        if (before.PartyKind is null)
            throw new BusinessRuleException("Only customer-role profiles carry a Customer/Contact classification.");

        PartyClassification after = await repository.SetClassificationAsync(userProfileId, request.PartyKind, request.IsManual, actor.UserProfileId, ct);
        await audit.RecordAsync("Party.KindSet", "UserProfile", userProfileId,
            new { before.PartyKind, before.PartyKindIsManual },
            new { after.PartyKind, after.PartyKindIsManual, request.Reason }, ct);
        return after;
    }
}

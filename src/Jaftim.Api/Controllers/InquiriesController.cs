using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Inquiries;
using Jaftim.Domain.Entities.Inquiries;
using Jaftim.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>
/// Inquiries (legacy InquiryController). An inquiry is an enquiry EVENT; the person it resolves to is a PARTY
/// (see /api/parties) which is a CONTACT while unqualified and a CUSTOMER once qualified. The inquiry's own
/// FullName/Email/Phone are a snapshot taken when it arrived and can differ from the party's current details.
///
/// Inbound leads are NOT handled here: the Azure Function (JaftimWebhooks) keeps posting to its own endpoint and
/// calling InsertLead directly, unchanged.
/// </summary>
public sealed class InquiriesController(IInquiryService inquiries, IInquirySaveService save) : ApiControllerBase
{
    /// <summary>
    /// Adds an inquiry. Matches an existing party by e-mail / username / any phone field; when none matches it also
    /// creates the party and its login. <b>e-mail is optional</b> - without one the phone digits become the e-mail,
    /// which is exactly what makes the party a CONTACT rather than a CUSTOMER (docs/INQUIRIES.md).
    /// 422 when the procedure rejects it ("Email and Phone found in different customers").
    /// </summary>
    [HttpPost]
    [HasPermission(Permissions.InquirySave)]
    public async Task<ActionResult<ApiResponse<InquirySaveResult>>> Create([FromBody] SaveInquiryRequest request, CancellationToken ct)
    {
        InquirySaveResult result = await save.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { inquiryId = result.InquiryId },
            ApiResponse.Ok(result, result.PartyCreated ? "Inquiry and party created." : "Inquiry added to the existing party."));
    }

    /// <summary>Updates an inquiry. The procedure only changes name, status, role, gender, country, customer type and ad link.</summary>
    [HttpPut("{inquiryId:long}")]
    [HasPermission(Permissions.InquirySave)]
    public async Task<ActionResult<ApiResponse<InquirySaveResult>>> Update(long inquiryId, [FromBody] SaveInquiryRequest request, CancellationToken ct) =>
        Ok(await save.UpdateAsync(inquiryId, request, ct), "Inquiry updated.");

    /// <summary>Is this e-mail already known? Used by the add form before submitting (legacy CheckEmail).</summary>
    [HttpGet("check-email")]
    [HasPermission(Permissions.InquirySave)]
    public async Task<ActionResult<ApiResponse<PartyMatch?>>> CheckEmail([FromQuery] string email, CancellationToken ct) =>
        Ok(await save.FindByEmailAsync(email, ct));

    /// <summary>Is this phone already known? (legacy CheckPhone)</summary>
    [HttpGet("check-phone")]
    [HasPermission(Permissions.InquirySave)]
    public async Task<ActionResult<ApiResponse<PartyMatch?>>> CheckPhone([FromQuery] string countryCode, [FromQuery] string phone, CancellationToken ct) =>
        Ok(await save.FindByPhoneAsync(countryCode, phone, ct));

    /// <summary>Paged inquiry list. Row visibility follows the caller's primary role inside the procedure, as before.</summary>
    [HttpGet]
    [HasPermission(Permissions.InquiryListing)]
    public async Task<ActionResult<ApiResponse<PagedResult<InquiryListItem>>>> GetAll([FromQuery] InquiryListRequest request, CancellationToken ct) =>
        Ok(await inquiries.GetAllAsync(request, ct));

    /// <summary>One inquiry, fetched through the same visibility rules (404 if the caller may not see it).</summary>
    [HttpGet("{inquiryId:long}")]
    [HasPermission(Permissions.Inquiry)]
    public async Task<ActionResult<ApiResponse<InquiryListItem>>> GetById(long inquiryId, CancellationToken ct) =>
        Ok(await inquiries.GetByIdAsync(inquiryId, ct));

    /// <summary>
    /// Records a contact attempt: appends a history row to CustomerRemarks, refreshes the inquiry's cached latest
    /// status/remark, and re-evaluates whether the party now qualifies as a customer.
    /// </summary>
    [HttpPost("{inquiryId:long}/contact-status")]
    [HasPermission(Permissions.InquiryContactEditSave)]
    public async Task<ActionResult<ApiResponse<InquiryListItem>>> SaveContactStatus(long inquiryId, [FromBody] InquiryContactStatusRequest request, CancellationToken ct) =>
        Ok(await inquiries.SaveContactStatusAsync(inquiryId, request, ct), "Contact status saved.");

    /// <summary>
    /// Tags the parties of several inquiries to one agent. Partially successful by design: 200 with
    /// succeeded/failed counts; 422 only when none could be tagged. Unlinked or invisible inquiries count as failed.
    /// </summary>
    [HttpPost("tag-bulk")]
    [HasPermission(Permissions.CustomerTagging)]
    public async Task<ActionResult<ApiResponse<BulkTagResult>>> TagBulk([FromBody] InquiryBulkTagRequest request, CancellationToken ct)
    {
        BulkTagResult result = await inquiries.TagBulkAsync(request, ct);
        return Ok(result, result.Message);
    }

    /// <summary>
    /// Untags (customer, agent) pairs - keyed on the party, not the inquiry, so rows sharing a customer collapse into
    /// one call. Same partial-success contract as tag-bulk.
    /// </summary>
    [HttpPost("untag-bulk")]
    [HasPermission(Permissions.CustomerTagging)]
    public async Task<ActionResult<ApiResponse<BulkTagResult>>> UntagBulk([FromBody] InquiryBulkUntagRequest request, CancellationToken ct)
    {
        BulkTagResult result = await inquiries.UntagBulkAsync(request, ct);
        return Ok(result, result.Message);
    }

    /// <summary>Tags the inquiry's party to an agent (`Inquiry_TaggedFromInquiries`). 422 if the inquiry has no party yet.</summary>
    [HttpPost("{inquiryId:long}/tag")]
    [HasPermission(Permissions.CustomerTagging)]
    public async Task<ActionResult<ApiResponse<object?>>> TagToAgent(long inquiryId, [FromBody] TagInquiryRequest request, CancellationToken ct)
    {
        await inquiries.TagToAgentAsync(inquiryId, request.AgentId, ct);
        return Ok("Tagged.");
    }
}

public sealed record TagInquiryRequest(long AgentId);

/// <summary>
/// A party is a customer-role profile: a CONTACT while unqualified (lead-stage: no real name, no valid e-mail or no
/// phone - typically created by the lead/Respond.io pipeline) and a CUSTOMER once qualified. The classification is
/// stored on the profile, re-applied automatically on writes, and can be pinned by a human.
/// Interactions here are the contact LOG (we called / messaged them) - a different thing from the party itself.
/// </summary>
[Route("api/parties")]
public sealed class PartiesController(IPartyService parties, IInquiryService inquiries) : ApiControllerBase
{
    /// <summary>Current classification, whether it is pinned, and when the party first qualified.</summary>
    [HttpGet("{userProfileId:long}/kind")]
    [HasPermission(Permissions.CustomerDetail)]
    public async Task<ActionResult<ApiResponse<PartyClassification>>> GetKind(long userProfileId, CancellationToken ct) =>
        Ok(await parties.GetClassificationAsync(userProfileId, ct));

    /// <summary>Pins the kind against the rule, or releases the pin with `isManual: false` so the rule applies again.</summary>
    [HttpPut("{userProfileId:long}/kind")]
    [HasPermission(Permissions.CustomerEdit)]
    public async Task<ActionResult<ApiResponse<PartyClassification>>> SetKind(long userProfileId, [FromBody] SetPartyKindRequest request, CancellationToken ct) =>
        Ok(await parties.SetClassificationAsync(userProfileId, request, ct), "Classification updated.");

    /// <summary>Re-applies the qualification rule now (normally automatic after a write).</summary>
    [HttpPost("{userProfileId:long}/kind/refresh")]
    [HasPermission(Permissions.CustomerEdit)]
    public async Task<ActionResult<ApiResponse<PartyClassification>>> RefreshKind(long userProfileId, CancellationToken ct) =>
        Ok(await parties.RefreshClassificationAsync(userProfileId, ct));

    /// <summary>
    /// Party detail tabs as key/value pairs grouped by section (`Contact_GetSectionById`). The inquiry's own
    /// sections are deliberately not exposed: `Inquiry_GetSectionById` references `Base_InquiryType`, a table that
    /// does not exist in this database (confirmed on UAT), so it throws on every call - the inquiry's fields are
    /// served in full by `GET /api/inquiries/{id}` instead.
    /// </summary>
    [HttpGet("{userProfileId:long}/sections")]
    [HasPermission(Permissions.CustomerSection)]
    public async Task<ActionResult<ApiResponse<IReadOnlyDictionary<string, IReadOnlyList<InquirySectionValue>>>>> GetSections(long userProfileId, CancellationToken ct) =>
        Ok(await inquiries.GetPartySectionsAsync(userProfileId, ct));

    /// <summary>Every inquiry this party has raised.</summary>
    [HttpGet("{userProfileId:long}/inquiries")]
    [HasPermission(Permissions.CustomerTabInquiries)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<InquiryListItem>>>> GetInquiries(long userProfileId, CancellationToken ct) =>
        Ok(await inquiries.GetByPartyAsync(userProfileId, ct));

    /// <summary>The interaction log (calls, WhatsApp, e-mails) recorded against this party.</summary>
    [HttpGet("{userProfileId:long}/interactions")]
    [HasPermission(Permissions.CustomerDetail)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<CustomerInteraction>>>> GetInteractions(long userProfileId, CancellationToken ct) =>
        Ok(await inquiries.GetInteractionsAsync(userProfileId, ct));

    /// <summary>Logs an interaction (`CustomerContact_Save`).</summary>
    [HttpPost("{userProfileId:long}/interactions")]
    [HasPermission(Permissions.InquiryContactEditSave)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<CustomerInteraction>>>> LogInteraction(long userProfileId, [FromBody] LogInteractionRequest request, CancellationToken ct) =>
        Ok(await inquiries.LogInteractionAsync(userProfileId, request, ct), "Interaction logged.");
}

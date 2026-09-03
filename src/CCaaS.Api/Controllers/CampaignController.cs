using CCaaS.Application.Campaign;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

[Authorize]
[Route("api/campaigns")]
public class CampaignController : ApiControllerBase
{
    private readonly ICampaignService _campaignService;

    public CampaignController(ICampaignService campaignService) => _campaignService = campaignService;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCampaignRequest request, CancellationToken ct)
        => Ok(await _campaignService.CreateCampaignAsync(TenantId, request, ct));

    [HttpPost("leads")]
    public async Task<IActionResult> AddLeads([FromBody] AddLeadsToCampaignRequest request, CancellationToken ct)
    {
        await _campaignService.AddLeadsAsync(TenantId, request, ct);
        return NoContent();
    }

    [HttpGet("{campaignId:guid}/next-lead")]
    public async Task<IActionResult> GetNextLead(Guid campaignId, CancellationToken ct)
    {
        var lead = await _campaignService.GetNextLeadForDialAsync(TenantId, campaignId, ct);
        return lead is null ? NoContent() : Ok(lead);
    }

    [HttpPost("leads/{campaignLeadId:guid}/outcome")]
    public async Task<IActionResult> RecordOutcome(Guid campaignLeadId, [FromQuery] string outcome, CancellationToken ct)
    {
        await _campaignService.RecordAttemptOutcomeAsync(TenantId, campaignLeadId, outcome, ct);
        return NoContent();
    }
}

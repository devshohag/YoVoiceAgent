using CCaaS.Application.Organization;
using CCaaS.Domain.Organization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

[Authorize]
[Route("api/organization")]
public class OrganizationController : ApiControllerBase
{
    private readonly IOrganizationService _organizationService;

    public OrganizationController(IOrganizationService organizationService) => _organizationService = organizationService;

    [HttpPost("branches")]
    public async Task<IActionResult> CreateBranch([FromBody] CreateBranchRequest request, CancellationToken ct)
        => Ok(await _organizationService.CreateBranchAsync(TenantId, request, ct));

    [HttpPost("teams")]
    public async Task<IActionResult> CreateTeam([FromBody] CreateTeamRequest request, CancellationToken ct)
        => Ok(await _organizationService.CreateTeamAsync(TenantId, request, ct));

    [HttpPost("agents")]
    public async Task<IActionResult> CreateAgent([FromBody] CreateAgentRequest request, CancellationToken ct)
        => Ok(await _organizationService.CreateAgentAsync(TenantId, request, ct));

    [HttpGet("agents")]
    public async Task<IActionResult> ListAgents(CancellationToken ct)
        => Ok(await _organizationService.ListAgentsAsync(TenantId, ct));

    [HttpPut("agents/{agentId:guid}/presence")]
    public async Task<IActionResult> SetPresence(Guid agentId, [FromQuery] AgentPresence presence, CancellationToken ct)
    {
        await _organizationService.SetAgentPresenceAsync(TenantId, agentId, presence, ct);
        return NoContent();
    }
}

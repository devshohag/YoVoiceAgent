using CCaaS.Application.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

[Authorize(Roles = "Supervisor,TenantAdmin,PlatformAdmin")]
[Route("api/reporting")]
public class ReportingController : ApiControllerBase
{
    private readonly IReportingService _reportingService;

    public ReportingController(IReportingService reportingService) => _reportingService = reportingService;

    [HttpGet("agents/daily")]
    public async Task<IActionResult> GetAgentMetrics([FromQuery] DateOnly date, CancellationToken ct)
        => Ok(await _reportingService.GetAgentMetricsAsync(TenantId, date, ct));

    [HttpGet("queues/{queueId:guid}/intervals")]
    public async Task<IActionResult> GetQueueIntervals(Guid queueId, [FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken ct)
        => Ok(await _reportingService.GetQueueIntervalsAsync(TenantId, queueId, from, to, ct));
}

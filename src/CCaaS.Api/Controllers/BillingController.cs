using CCaaS.Application.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

[Authorize(Roles = "TenantAdmin,PlatformAdmin")]
[Route("api/billing")]
public class BillingController : ApiControllerBase
{
    private readonly IBillingService _billingService;

    public BillingController(IBillingService billingService) => _billingService = billingService;

    [HttpPost("subscriptions")]
    public async Task<IActionResult> Subscribe([FromBody] CreateSubscriptionRequest request, CancellationToken ct)
        => Ok(await _billingService.SubscribeAsync(TenantId, request, ct));

    [HttpPost("usage")]
    public async Task<IActionResult> RecordUsage([FromBody] RecordUsageRequest request, CancellationToken ct)
    {
        await _billingService.RecordUsageAsync(TenantId, request, ct);
        return NoContent();
    }

    [HttpPost("subscriptions/{subscriptionId:guid}/invoices")]
    public async Task<IActionResult> GenerateInvoice(Guid subscriptionId, CancellationToken ct)
        => Ok(await _billingService.GenerateInvoiceAsync(TenantId, subscriptionId, ct));
}

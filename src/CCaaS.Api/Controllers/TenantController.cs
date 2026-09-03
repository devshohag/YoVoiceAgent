using CCaaS.Application.Tenant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

/// <summary>
/// Section 1 target business outcome: "A tenant can be provisioned ... and become
/// operational without code changes." Provisioning itself is a platform-admin action
/// (no tenant JWT exists yet), so it deliberately does NOT inherit ApiControllerBase.
/// </summary>
[ApiController]
[Route("api/tenants")]
[Authorize(Roles = "PlatformAdmin")]
public class TenantController : ControllerBase
{
    private readonly ITenantService _tenantService;

    public TenantController(ITenantService tenantService) => _tenantService = tenantService;

    [HttpPost]
    public async Task<IActionResult> Provision([FromBody] ProvisionTenantRequest request, CancellationToken ct)
    {
        var tenant = await _tenantService.ProvisionTenantAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { tenantId = tenant.Id }, tenant);
    }

    [HttpGet("{tenantId:guid}")]
    public async Task<IActionResult> GetById(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _tenantService.GetByIdAsync(tenantId, ct);
        return tenant is null ? NotFound() : Ok(tenant);
    }

    [HttpPut("{tenantId:guid}/entitlements/{featureKey}")]
    public async Task<IActionResult> SetEntitlement(Guid tenantId, string featureKey, [FromQuery] bool enabled, [FromQuery] int? quota, CancellationToken ct)
    {
        await _tenantService.SetFeatureEntitlementAsync(tenantId, featureKey, enabled, quota, ct);
        return NoContent();
    }
}

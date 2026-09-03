using CCaaS.Application.Common;
using TenantEntity = CCaaS.Domain.Tenant.Tenant;
using CCaaS.Domain.Tenant;

namespace CCaaS.Application.Tenant;

// Section 1 target business outcome: "A tenant can be provisioned, create branches/teams/agents,
// connect a SIP trunk and digital channels, and become operational without code changes."

public record ProvisionTenantRequest(string Name, string Slug, TenantEdition Edition);
public record TenantDto(Guid Id, string Name, string Slug, TenantEdition Edition, TenantStatus Status);

public interface ITenantService
{
    Task<TenantDto> ProvisionTenantAsync(ProvisionTenantRequest request, CancellationToken ct = default);
    Task<TenantDto?> GetByIdAsync(Guid tenantId, CancellationToken ct = default);
    Task SetFeatureEntitlementAsync(Guid tenantId, string featureKey, bool enabled, int? quota, CancellationToken ct = default);
}

public class TenantService : ITenantService
{
    private readonly IRepository<TenantEntity> _tenants;
    private readonly IRepository<TenantSettings> _tenantSettings;
    private readonly IRepository<FeatureEntitlement> _featureEntitlements;
    private readonly IUnitOfWork _unitOfWork;

    public TenantService(
        IRepository<TenantEntity> tenants,
        IRepository<TenantSettings> tenantSettings,
        IRepository<FeatureEntitlement> featureEntitlements,
        IUnitOfWork unitOfWork)
    {
        _tenants = tenants;
        _tenantSettings = tenantSettings;
        _featureEntitlements = featureEntitlements;
        _unitOfWork = unitOfWork;
    }

    public async Task<TenantDto> ProvisionTenantAsync(ProvisionTenantRequest request, CancellationToken ct = default)
    {
        var slugTaken = await _tenants.AnyAsync(t => t.Slug == request.Slug, ct);
        if (slugTaken)
            throw new InvalidOperationException($"Tenant slug '{request.Slug}' is already in use.");

        var tenant = new TenantEntity
        {
            Name = request.Name,
            Slug = request.Slug,
            Edition = request.Edition,
            Status = TenantStatus.Trial
        };
        await _tenants.AddAsync(tenant, ct);

        await _tenantSettings.AddAsync(new TenantSettings { TenantId = tenant.Id }, ct);

        // Seed baseline entitlements matching Section 3 (Commercial Product Editions).
        var defaults = request.Edition switch
        {
            TenantEdition.Voice => new[] { "voice.inbound", "voice.outbound", "voice.ivr", "voice.recording" },
            TenantEdition.Omnichannel => new[] { "voice.inbound", "voice.outbound", "channel.whatsapp", "channel.webchat" },
            _ => new[] { "voice.inbound", "voice.outbound", "channel.whatsapp", "channel.webchat", "sso.enabled" }
        };
        foreach (var key in defaults)
        {
            await _featureEntitlements.AddAsync(new FeatureEntitlement { TenantId = tenant.Id, FeatureKey = key, Enabled = true }, ct);
        }

        await _unitOfWork.SaveChangesAsync(ct);
        return new TenantDto(tenant.Id, tenant.Name, tenant.Slug, tenant.Edition, tenant.Status);
    }

    public async Task<TenantDto?> GetByIdAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await _tenants.GetByIdAsync(tenantId, ct);
        return tenant is null ? null : new TenantDto(tenant.Id, tenant.Name, tenant.Slug, tenant.Edition, tenant.Status);
    }

    public async Task SetFeatureEntitlementAsync(Guid tenantId, string featureKey, bool enabled, int? quota, CancellationToken ct = default)
    {
        var entitlement = await _featureEntitlements.FirstOrDefaultAsync(
            f => f.TenantId == tenantId && f.FeatureKey == featureKey, ct);

        if (entitlement is null)
        {
            await _featureEntitlements.AddAsync(new FeatureEntitlement
            {
                TenantId = tenantId,
                FeatureKey = featureKey,
                Enabled = enabled,
                Quota = quota
            }, ct);
        }
        else
        {
            entitlement.Enabled = enabled;
            entitlement.Quota = quota;
            entitlement.UpdatedAt = DateTime.UtcNow;
            _featureEntitlements.Update(entitlement);
        }

        await _unitOfWork.SaveChangesAsync(ct);
    }
}

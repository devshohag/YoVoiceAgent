using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCaaS.Workers.Telephony;

public interface IInboundTenantResolver
{
    Task<Guid> ResolveAsync(string calledNumber, CancellationToken ct = default);
}

/// <summary>
/// Production-ready boundary: a real DID resolves its owning tenant from SQL. Local extension
/// 7000 falls back to the explicitly configured development tenant.
/// </summary>
public sealed class InboundTenantResolver : IInboundTenantResolver
{
    private readonly CcaasDbContext _db;
    private readonly IConfiguration _configuration;

    public InboundTenantResolver(CcaasDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    public async Task<Guid> ResolveAsync(string calledNumber, CancellationToken ct = default)
    {
        var didTenant = await _db.DidNumbers.IgnoreQueryFilters()
            .Where(x => !x.IsDeleted && x.Number == calledNumber)
            .Select(x => (Guid?)x.TenantId)
            .SingleOrDefaultAsync(ct);
        if (didTenant is { } tenantId && tenantId != Guid.Empty) return tenantId;

        var configured = _configuration["Telephony:DefaultTenantId"];
        if (Guid.TryParse(configured, out tenantId) && tenantId != Guid.Empty) return tenantId;
        throw new InvalidOperationException(
            $"No tenant mapping exists for called number '{calledNumber}', and Telephony:DefaultTenantId is missing.");
    }
}

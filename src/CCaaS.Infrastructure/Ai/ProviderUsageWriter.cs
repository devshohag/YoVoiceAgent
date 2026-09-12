using CCaaS.Application.Ai;
using CCaaS.Domain.Ai;
using CCaaS.Application.Common;

namespace CCaaS.Infrastructure.Ai;

public sealed class ProviderUsageWriter : IProviderUsageWriter
{
    private readonly IRepository<ProviderUsage> _usage;
    private readonly IUnitOfWork _unitOfWork;

    public ProviderUsageWriter(IRepository<ProviderUsage> usage, IUnitOfWork unitOfWork)
    {
        _usage = usage;
        _unitOfWork = unitOfWork;
    }

    public async Task RecordAsync(Guid tenantId, string provider, string operation, decimal units,
        string unitType, long costMicros, int durationMilliseconds, CancellationToken ct = default)
    {
        await _usage.AddAsync(new ProviderUsage
        {
            TenantId = tenantId,
            Provider = provider,
            Operation = operation,
            Units = Math.Max(0, units),
            UnitType = unitType,
            CostMicros = Math.Max(0, costMicros),
            DurationMilliseconds = Math.Max(0, durationMilliseconds)
        }, ct);
        await _unitOfWork.SaveChangesAsync(ct);
    }
}
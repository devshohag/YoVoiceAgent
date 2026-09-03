using CCaaS.Application.Common;
using CCaaS.Domain.Reporting;

namespace CCaaS.Application.Reporting;

public record SupervisorDashboardDto(int AgentsOnline, int CallsWaiting, double AverageWaitSeconds, double ServiceLevelPercent);

public interface IReportingService
{
    Task<List<AgentDailyMetric>> GetAgentMetricsAsync(Guid tenantId, DateOnly date, CancellationToken ct = default);
    Task<List<QueueInterval>> GetQueueIntervalsAsync(Guid tenantId, Guid queueId, DateTime from, DateTime to, CancellationToken ct = default);
}

public class ReportingService : IReportingService
{
    private readonly IRepository<AgentDailyMetric> _agentDailyMetrics;
    private readonly IRepository<QueueInterval> _queueIntervals;

    public ReportingService(IRepository<AgentDailyMetric> agentDailyMetrics, IRepository<QueueInterval> queueIntervals)
    {
        _agentDailyMetrics = agentDailyMetrics;
        _queueIntervals = queueIntervals;
    }

    public async Task<List<AgentDailyMetric>> GetAgentMetricsAsync(Guid tenantId, DateOnly date, CancellationToken ct = default)
        => await _agentDailyMetrics.ToListAsync(_agentDailyMetrics.Query().Where(m => m.TenantId == tenantId && m.MetricDate == date), ct);

    public async Task<List<QueueInterval>> GetQueueIntervalsAsync(Guid tenantId, Guid queueId, DateTime from, DateTime to, CancellationToken ct = default)
        => await _queueIntervals.ToListAsync(
            _queueIntervals.Query().Where(q => q.TenantId == tenantId && q.QueueId == queueId && q.IntervalStart >= from && q.IntervalStart <= to), ct);

    // Note: this reads pre-aggregated rows only (Section 12/13 - Hangfire "Periodic report
    // aggregation" job is what populates AgentDailyMetric/QueueInterval). Wire that Hangfire
    // job up in Infrastructure once real call/message volume exists to aggregate.
}

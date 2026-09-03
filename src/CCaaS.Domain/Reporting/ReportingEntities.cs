using CCaaS.Domain.Common;

namespace CCaaS.Domain.Reporting;

// Schema: reporting  (Section 12 - these are Hangfire "Periodic report aggregation" targets,
// pre-aggregated so the Supervisor Center / Reporting module never runs heavy ad-hoc queries.)

public class AgentDailyMetric : BaseEntity
{
    public Guid AgentId { get; set; }
    public DateOnly MetricDate { get; set; }
    public int CallsHandled { get; set; }
    public int MessagesHandled { get; set; }
    public int TotalTalkSeconds { get; set; }
    public int TotalWrapUpSeconds { get; set; }
    public double? AverageQaScore { get; set; }
}

public class QueueInterval : BaseEntity
{
    public Guid QueueId { get; set; }
    public DateTime IntervalStart { get; set; }
    public int OfferedCount { get; set; }
    public int AnsweredCount { get; set; }
    public int AbandonedCount { get; set; }
    public double AverageWaitSeconds { get; set; }
    public double ServiceLevelPercent { get; set; }
}

public class CampaignMetric : BaseEntity
{
    public Guid CampaignId { get; set; }
    public DateOnly MetricDate { get; set; }
    public int AttemptsCount { get; set; }
    public int ConnectedCount { get; set; }
    public int ConversionCount { get; set; }
}

public class ChannelMetric : BaseEntity
{
    public string ChannelType { get; set; } = default!;
    public DateOnly MetricDate { get; set; }
    public int MessagesInbound { get; set; }
    public int MessagesOutbound { get; set; }
    public double AverageFirstResponseSeconds { get; set; }
}

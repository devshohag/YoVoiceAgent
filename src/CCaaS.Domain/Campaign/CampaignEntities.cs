using CCaaS.Domain.Common;

namespace CCaaS.Domain.Campaign;

// Schema: campaign  (Section 10 - CRM, Campaign & Dialer Architecture)
// Campaign is an "independent business object, not a hard parent of Team/Agent" - Section 7.

public class Campaign : BaseEntity
{
    public string Name { get; set; } = default!;
    public CampaignChannel Channel { get; set; } = CampaignChannel.Voice;
    public DialMode DialMode { get; set; } = DialMode.ManualDial;
    public string TimeZone { get; set; } = "Asia/Dhaka";
    public TimeSpan CallingWindowStart { get; set; } = new(9, 0, 0);
    public TimeSpan CallingWindowEnd { get; set; } = new(20, 0, 0);
    public int? PerCampaignDailyLimit { get; set; }
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;

    public Guid? ScriptId { get; set; }
    public Script? Script { get; set; }
    public Guid? RetryPolicyId { get; set; }
    public RetryPolicy? RetryPolicy { get; set; }

    public ICollection<CampaignLead> CampaignLeads { get; set; } = new List<CampaignLead>();
    public ICollection<CampaignAgent> CampaignAgents { get; set; } = new List<CampaignAgent>();
}

public enum CampaignChannel { Voice, WhatsApp, Sms, Email }

public enum DialMode
{
    ManualDial,
    ClickToCall,
    Preview,
    Progressive,
    // Predictive is a documented FUTURE capability (Section 10) - requires compliance
    // and abandon-rate controls before enabling. Kept in the enum so the data model
    // does not need to change later, but no dialer engine implements it yet.
    Predictive
}

public enum CampaignStatus { Draft, Scheduled, Running, Paused, Completed }

public class CampaignLead : BaseEntity
{
    public Guid CampaignId { get; set; }
    public Campaign? Campaign { get; set; }
    public Guid LeadId { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastOutcome { get; set; }
}

public class CampaignAgent : BaseEntity
{
    // Many-to-many: one team/agent can serve multiple campaigns (Section 7).
    public Guid CampaignId { get; set; }
    public Campaign? Campaign { get; set; }
    public Guid AgentId { get; set; }
}

public class Script : BaseEntity
{
    public string Name { get; set; } = default!;
    public string Content { get; set; } = default!; // markdown / rich text agent script
}

public class RetryPolicy : BaseEntity
{
    public string Name { get; set; } = default!;
    public int MaxAttempts { get; set; } = 3;
    public int BusyRetryMinutes { get; set; } = 30;
    public int NoAnswerRetryMinutes { get; set; } = 60;
    public int FailedRetryMinutes { get; set; } = 120;
}

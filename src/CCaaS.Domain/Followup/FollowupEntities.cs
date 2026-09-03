using CCaaS.Domain.Common;

namespace CCaaS.Domain.Followup;

// Schema: followup  (Section 11 - Agent Workspace: "Follow-up/callback scheduling")

public class FollowUpTask : BaseEntity
{
    public Guid? CustomerId { get; set; }
    public Guid AssignedAgentId { get; set; }
    public string Title { get; set; } = default!;
    public DateTime DueAt { get; set; }
    public bool IsCompleted { get; set; }
}

public class Callback : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Guid? CallSessionId { get; set; } // the call that generated this callback
    public Guid AssignedAgentId { get; set; }
    public DateTime ScheduledAt { get; set; }
    public bool IsCompleted { get; set; }
}

public class Reminder : BaseEntity
{
    // Hangfire "Scheduled callbacks and reminders" job reads from this table (Section 13).
    public Guid AgentId { get; set; }
    public string Message { get; set; } = default!;
    public DateTime RemindAt { get; set; }
    public bool IsSent { get; set; }
}

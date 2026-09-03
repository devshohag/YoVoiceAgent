using CCaaS.Domain.Common;

namespace CCaaS.Domain.Calls;

// Schema: calls  (Section 12 - "CallSession minimum fields", Section 8 - Voice & Telephony Architecture)

public class CallSession : BaseEntity
{
    // Field list copied verbatim from the proposal's "CallSession minimum fields" box (page 10)
    // so this stays a drop-in match for the spec, not a re-interpretation of it.
    public string? ExternalCallId { get; set; } // Asterisk Linkedid
    public CallDirection Direction { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? AgentId { get; set; }
    public Guid? QueueId { get; set; }
    public string FromNumber { get; set; } = default!;
    public string ToNumber { get; set; } = default!;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RingingAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public DateTime? EndedAt { get; set; }

    public int? RingDurationSeconds { get; set; }
    public int? TalkDurationSeconds { get; set; }
    public int? HoldDurationSeconds { get; set; }

    public CallStatus Status { get; set; } = CallStatus.Ringing;
    public string? HangupCause { get; set; }
    public Guid? DispositionId { get; set; }
    public Guid? RecordingId { get; set; }

    public ICollection<CallLeg> Legs { get; set; } = new List<CallLeg>();
    public ICollection<CallEvent> Events { get; set; } = new List<CallEvent>();
}

public enum CallDirection { Inbound, Outbound }

public enum CallStatus { Ringing, Answered, OnHold, Transferring, Ended, Failed, Abandoned }

public class CallLeg : BaseEntity
{
    // "Agent, trunk, transfer and conference legs"
    public Guid CallSessionId { get; set; }
    public CallSession? CallSession { get; set; }
    public string LegType { get; set; } = default!; // "agent" | "trunk" | "transfer" | "conference"
    public string Channel { get; set; } = default!;  // Asterisk channel identifier
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
}

public class CallEvent : BaseEntity
{
    // "Ringing, answered, hold, transfer, ended, recording events" - normalized from ARI/AMI.
    public Guid CallSessionId { get; set; }
    public CallSession? CallSession { get; set; }
    public string EventType { get; set; } = default!;
    public string? PayloadJson { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

public class Recording : BaseEntity
{
    // "Object storage metadata, retention and access"
    public Guid CallSessionId { get; set; }
    public string ObjectStorageKey { get; set; } = default!; // MinIO/S3 key; SQL never stores audio bytes
    public int DurationSeconds { get; set; }
    public long SizeBytes { get; set; }
    public DateTime RetainUntil { get; set; }
}

public class Disposition : BaseEntity
{
    public string Code { get; set; } = default!; // e.g. "sale", "callback", "not-interested"
    public string Description { get; set; } = default!;
    public bool RequiresFollowUp { get; set; }
}

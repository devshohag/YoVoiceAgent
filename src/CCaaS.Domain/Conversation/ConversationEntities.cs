using CCaaS.Domain.Common;

namespace CCaaS.Domain.Conversation;

// Schema: conversation  (Section 9 - Omnichannel Conversation Architecture)
// "The center of the omnichannel product is Conversation, not Call."

public class Conversation : BaseEntity
{
    public Guid CustomerId { get; set; }
    public ConversationChannelType ChannelType { get; set; }
    public Guid? AssignedAgentId { get; set; }
    public ConversationStatus Status { get; set; } = ConversationStatus.Open;
    public string? Subject { get; set; }
    public DateTime? SlaDueAt { get; set; }

    public ICollection<Participant> Participants { get; set; } = new List<Participant>();
    public ICollection<Interaction> Interactions { get; set; } = new List<Interaction>();
    public ICollection<Assignment> Assignments { get; set; } = new List<Assignment>();
    public ICollection<SlaEvent> SlaEvents { get; set; } = new List<SlaEvent>();
}

public enum ConversationChannelType { Voice, WhatsApp, Messenger, Instagram, Email, Sms, WebChat }

public enum ConversationStatus { Open, Pending, Resolved, Closed }

public class Participant : BaseEntity
{
    public Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }
    public string ParticipantType { get; set; } = default!; // "customer" | "agent" | "system"
    public Guid? AgentId { get; set; }
    public Guid? CustomerId { get; set; }
}

/// <summary>
/// A single channel-specific "interaction" within a conversation - e.g. one CallSession,
/// or one WhatsApp thread. This is what lets Figure 2 in the proposal show Voice/WhatsApp/
/// Messenger/Email all converging into one Conversation.
/// </summary>
public class Interaction : BaseEntity
{
    public Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }
    public ConversationChannelType ChannelType { get; set; }
    public Guid? CallSessionId { get; set; } // set when ChannelType == Voice
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();
}

public class Message : BaseEntity
{
    public Guid InteractionId { get; set; }
    public Interaction? Interaction { get; set; }
    public string Direction { get; set; } = default!; // "inbound" | "outbound"
    public string? SenderAgentId { get; set; }
    public string Body { get; set; } = default!;
    public string? ProviderMessageId { get; set; } // idempotency key from Meta/SMS/Email provider
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }

    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
}

public class Attachment : BaseEntity
{
    public Guid MessageId { get; set; }
    public Message? Message { get; set; }
    public string ObjectStorageKey { get; set; } = default!; // MinIO/S3 key, not the file itself
    public string ContentType { get; set; } = default!;
    public long SizeBytes { get; set; }
}

public class Assignment : BaseEntity
{
    // Routing & Assignment: Skill | Priority | SLA | Queue (Figure 2)
    public Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }
    public Guid AgentId { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UnassignedAt { get; set; }
    public string Reason { get; set; } = default!; // "routing-engine" | "manual-transfer" | ...
}

public class SlaEvent : BaseEntity
{
    public Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }
    public string EventType { get; set; } = default!; // "sla-warning" | "sla-breached" | "sla-met"
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

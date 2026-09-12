using CCaaS.Domain.Common;

namespace CCaaS.Domain.Ai;

public class AiAgent : BaseEntity
{
    public string Name { get; set; } = default!;
    public string Language { get; set; } = "en-US";
    public string VoiceName { get; set; } = "default";
    public string SystemPrompt { get; set; } = default!;
    public string WelcomeMessage { get; set; } = default!;
    public string FallbackMessage { get; set; } = "Let me transfer you to a person who can help.";
    public Guid? HumanQueueId { get; set; }
    public bool AllowInboundCalls { get; set; } = true;
    public bool AllowOutboundCalls { get; set; }
    public bool IsActive { get; set; } = true;
    public int MaxConversationSeconds { get; set; } = 900;
    public int SilenceTimeoutSeconds { get; set; } = 10;
    public int PublishedVersion { get; set; } = 1;
}

public class AiAgentVersion : BaseEntity
{
    public Guid AiAgentId { get; set; }
    public int Version { get; set; }
    public string ConfigurationJson { get; set; } = default!;
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
    public Guid? PublishedByUserId { get; set; }
}

public class AiConversation : BaseEntity
{
    public Guid AiAgentId { get; set; }
    public Guid? CallSessionId { get; set; }
    public Guid? CustomerId { get; set; }
    public AiConversationStatus Status { get; set; } = AiConversationStatus.Created;
    public string? DetectedLanguage { get; set; }
    public string? Transcript { get; set; }
    public string? RedactedTranscript { get; set; }
    public string? Summary { get; set; }
    public string? Intent { get; set; }
    public string? Outcome { get; set; }
    public string? Sentiment { get; set; }
    public int? QaScore { get; set; }
    public string? SuggestedDisposition { get; set; }
    public string? SuggestedFollowUp { get; set; }
    public string? HandoffReason { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public ICollection<AiConversationTurn> Turns { get; set; } = new List<AiConversationTurn>();
}

public class AiConversationTurn : BaseEntity
{
    public Guid AiConversationId { get; set; }
    public AiSpeaker Speaker { get; set; }
    public string Text { get; set; } = default!;
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public int Sequence { get; set; }
}

public class AiToolDefinition : BaseEntity
{
    public string Name { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string InputSchemaJson { get; set; } = "{}";
    public bool RequiresConfirmation { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public class AiToolExecution : BaseEntity
{
    public Guid AiConversationId { get; set; }
    public string ToolName { get; set; } = default!;
    public string ArgumentsJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public AiToolExecutionStatus Status { get; set; } = AiToolExecutionStatus.Requested;
    public bool WasConfirmed { get; set; }
    public int DurationMilliseconds { get; set; }
    public string? FailureReason { get; set; }
}

public class AiUsageRecord : BaseEntity
{
    public Guid AiConversationId { get; set; }
    public string Provider { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public int InputUnits { get; set; }
    public int OutputUnits { get; set; }
    public decimal EstimatedCost { get; set; }
}

public class ProviderUsage : BaseEntity
{
    public string Provider { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public decimal Units { get; set; }
    public string UnitType { get; set; } = default!;
    public long CostMicros { get; set; }
    public int DurationMilliseconds { get; set; }
}

public enum AiConversationStatus { Created, Processing, Completed, HandoffRequested, Failed }
public enum AiSpeaker { Customer, AiAgent, HumanAgent, System }
public enum AiToolExecutionStatus { Requested, Succeeded, Failed, Rejected }

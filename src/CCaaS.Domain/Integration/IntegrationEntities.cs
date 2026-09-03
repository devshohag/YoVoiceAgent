using CCaaS.Domain.Common;

namespace CCaaS.Domain.Integration;

// Schema: integration  (Section 13 - Messaging, Reliability & Background Processing)
// Implements the "Transactional Outbox" pattern described in the proposal:
// "Business data and the integration event are committed in the same SQL transaction.
//  A publisher worker sends pending outbox messages to RabbitMQ and marks them published.
//  Consumers maintain inbox/idempotency records so a redelivered event cannot cause
//  duplicate billing, duplicate messages, or duplicate follow-ups."

public class OutboxMessage : BaseEntity
{
    public string EventType { get; set; } = default!; // e.g. "CallEnded", "MessageReceived"
    public string PayloadJson { get; set; } = default!;
    public DateTime? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public class InboxMessage : BaseEntity
{
    // Consumer-side idempotency guard: a (ProviderMessageId/EventId) that has already been
    // recorded here is a redelivery and must be skipped, not re-applied.
    public string EventType { get; set; } = default!;
    public string DeduplicationKey { get; set; } = default!;
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}

public class IntegrationFailure : BaseEntity
{
    public string EventType { get; set; } = default!;
    public string PayloadJson { get; set; } = default!;
    public string ErrorMessage { get; set; } = default!;
    public int RetryCount { get; set; }
    public DateTime FailedAt { get; set; } = DateTime.UtcNow;
    public bool MovedToDeadLetter { get; set; }
}

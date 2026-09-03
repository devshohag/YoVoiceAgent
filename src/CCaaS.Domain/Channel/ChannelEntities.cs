using CCaaS.Domain.Common;

namespace CCaaS.Domain.Channel;

// Schema: channel  (Section 9 - Provider adapter pattern: IChannelProvider implementations)

public class ChannelAccount : BaseEntity
{
    // "Tenant-owned WhatsApp/Page/Instagram/Email/SMS account" - Section 7
    public ChannelKind Kind { get; set; }
    public string DisplayName { get; set; } = default!;
    public string ExternalAccountId { get; set; } = default!; // e.g. WABA phone number ID, Page ID
    public bool IsActive { get; set; } = true;

    public ICollection<ChannelCredentialRef> Credentials { get; set; } = new List<ChannelCredentialRef>();
}

public enum ChannelKind { WhatsApp, Messenger, Instagram, Email, Sms, WebChat }

public class ChannelCredentialRef : BaseEntity
{
    // Security control (Section 14): "No SIP/API secrets in source code; encrypted/secret-store references."
    // This table stores a REFERENCE (e.g. a key vault path or encrypted blob id), never the raw secret.
    public Guid ChannelAccountId { get; set; }
    public ChannelAccount? ChannelAccount { get; set; }
    public string SecretStoreReference { get; set; } = default!;
}

public class MessageTemplate : BaseEntity
{
    // WhatsApp Cloud API requires pre-approved templates for business-initiated messages.
    public Guid ChannelAccountId { get; set; }
    public string Name { get; set; } = default!;
    public string Language { get; set; } = "en";
    public string Body { get; set; } = default!;
    public string ApprovalStatus { get; set; } = "pending"; // pending | approved | rejected
}

public class WebhookEvent : BaseEntity
{
    // Inbound message flow (Section 9): Provider Webhook -> Signature Verification -> Idempotency Store -> ...
    public Guid ChannelAccountId { get; set; }
    public string ProviderEventId { get; set; } = default!; // used for idempotency de-duplication
    public string Payload { get; set; } = default!; // raw JSON, kept for replay/debugging
    public bool Processed { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}

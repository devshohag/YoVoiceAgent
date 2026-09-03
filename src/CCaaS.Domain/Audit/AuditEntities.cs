using CCaaS.Domain.Common;

namespace CCaaS.Domain.Audit;

// Schema: audit  (Section 14 - Security, Compliance & Audit)
// Audit requirement: "Recording download, data export, SIP changes, admin/billing changes, user/security events."

public class AuditLog : BaseEntity
{
    public Guid ActorUserId { get; set; }
    public string Action { get; set; } = default!; // e.g. "recording.download", "sip-trunk.update"
    public string EntityType { get; set; } = default!;
    public Guid EntityId { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public string IpAddress { get; set; } = default!;
}

public class SecurityEvent : BaseEntity
{
    public string EventType { get; set; } = default!; // "login-failed" | "mfa-challenge" | "session-revoked"
    public Guid? UserId { get; set; }
    public string IpAddress { get; set; } = default!;
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public string? DetailsJson { get; set; }
}

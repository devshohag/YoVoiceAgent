using CCaaS.Domain.Common;

namespace CCaaS.Domain.Compliance;

// Schema: compliance
//
// WHY THIS MODULE EXISTS AND WHY IT LANDS BEFORE THE DIALER
// ---------------------------------------------------------
// Inbound calls arrive because the customer chose to call. Outbound calls are placed by us,
// which makes them a regulated act rather than a technical one. The FCC has ruled that
// AI-generated voices in robocalls fall under the TCPA's "artificial voice" prohibition, and
// TCPA exposure is assessed PER CALL - so a single unattended campaign loop can create
// liability far larger than the product's revenue.
//
// The consequence for the data model is specific: consent is not a boolean on the customer
// row. Consent has a history - who granted it, through which channel, on what basis, when it
// expires, and whether it was later revoked. A boolean cannot answer "were we allowed to call
// this person on that date", which is exactly the question a dispute asks.
//
// Therefore:
//   * ContactConsent   - append-only record of permission. Never UPDATE, never DELETE.
//   * DoNotCallEntry   - the suppression list. Written the moment a caller opts out, mid-call.
//   * SuppressionCheck - append-only log of every pre-dial decision, including the ALLOWs.
//
// The last one is the least obvious and the most important. Logging only blocked calls proves
// nothing; logging every check is what demonstrates the gate ran at all.
//
// Retention: these three tables are a legal record, not business data. The retention jobs
// that trim recordings and transcripts must never touch them.

/// <summary>
/// Channel a consent grant applies to. Permission to call is not permission to text.
/// </summary>
public enum ConsentChannel
{
    Voice,
    Sms,
    WhatsApp,
    Email
}

/// <summary>
/// The legal basis being relied on. Stored rather than inferred, because the basis determines
/// what kinds of calls the consent actually covers.
/// </summary>
public enum ConsentType
{
    /// <summary>Signed or explicitly recorded opt-in. The strongest basis; required for marketing.</summary>
    ExpressWritten,

    /// <summary>An existing customer relationship, which supports transactional/service calls only.</summary>
    ExistingBusinessRelationship,

    /// <summary>The contact called us or submitted an enquiry, which supports a reply about that enquiry.</summary>
    InboundInquiry
}

/// <summary>
/// Append-only permission record. The effective consent for a phone number is the most recent
/// row that is not revoked and not expired - never "the" row, because there is no single row.
/// </summary>
public sealed class ContactConsent : BaseEntity
{
    /// <summary>Set when the consent is tied to a converted customer.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Set when the consent was captured against a lead that is not yet a customer.</summary>
    public Guid? LeadId { get; set; }

    /// <summary>
    /// Normalised E.164 number the consent applies to. Denormalised deliberately: the
    /// pre-dial gate must answer from the phone number alone, without resolving a customer
    /// first, and the consent must survive the contact record being merged or renamed.
    /// </summary>
    public string PhoneE164 { get; set; } = default!;

    public ConsentChannel Channel { get; set; } = ConsentChannel.Voice;

    public ConsentType ConsentType { get; set; } = ConsentType.ExistingBusinessRelationship;

    /// <summary>Where the grant came from, e.g. "inbound-call", "service-contract", "web-form", "import".</summary>
    public string Source { get; set; } = default!;

    /// <summary>
    /// Pointer to the evidence - a recording object key, a signed-document key, a form submission id.
    /// Object storage key only; this table never holds the artefact itself.
    /// </summary>
    public string? EvidenceUri { get; set; }

    /// <summary>Call session the consent was captured on, when it was captured by voice.</summary>
    public Guid? SourceCallSessionId { get; set; }

    public DateTime GrantedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Null means the grant does not self-expire.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>
    /// Set when the contact withdrew permission. The row stays; revocation is recorded, not erased,
    /// so the timeline remains reconstructible.
    /// </summary>
    public DateTime? RevokedAtUtc { get; set; }

    public string? RevokedReason { get; set; }

    /// <summary>Convenience predicate; the authoritative evaluation lives in the compliance rule engine.</summary>
    public bool IsEffectiveAt(DateTime atUtc) =>
        GrantedAtUtc <= atUtc
        && (RevokedAtUtc is null || RevokedAtUtc > atUtc)
        && (ExpiresAtUtc is null || ExpiresAtUtc > atUtc);
}

public enum DoNotCallScope
{
    /// <summary>Applies to this tenant only.</summary>
    Tenant,

    /// <summary>Applies platform-wide - regulator lists and platform-level blocks.</summary>
    Global
}

public enum DoNotCallSource
{
    /// <summary>The contact asked, usually mid-call. Must be honoured immediately.</summary>
    CustomerRequest,

    RegulatorList,
    ManualEntry,
    Import
}

/// <summary>
/// Suppression list. A hit here blocks the call regardless of what consent says - a later
/// opt-out always outranks an earlier opt-in.
/// </summary>
public sealed class DoNotCallEntry : BaseEntity
{
    public string PhoneE164 { get; set; } = default!;

    public DoNotCallScope Scope { get; set; } = DoNotCallScope.Tenant;

    public string Reason { get; set; } = default!;

    public DoNotCallSource Source { get; set; } = DoNotCallSource.CustomerRequest;

    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Null when the entry was created by the AI agent rather than a human operator.</summary>
    public Guid? AddedByUserId { get; set; }

    /// <summary>The call on which the opt-out was spoken, so the request can be re-listened to.</summary>
    public Guid? SourceCallSessionId { get; set; }

    /// <summary>Null means permanent. Regulator lists sometimes carry an expiry.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    public bool IsActiveAt(DateTime atUtc) =>
        AddedAtUtc <= atUtc && (ExpiresAtUtc is null || ExpiresAtUtc > atUtc);
}

public enum SuppressionResult
{
    Allow,
    Block
}

/// <summary>
/// Why the gate decided as it did. Stored as an enum rather than free text so campaign
/// reporting can aggregate "how many contacts were unreachable, and for which reason".
/// </summary>
public enum SuppressionReason
{
    /// <summary>All checks passed. Logged, not discarded - this is the proof the gate ran.</summary>
    Ok,

    OnDoNotCallList,
    NoValidConsent,
    ConsentExpired,
    OutsideCallingWindow,
    MaxAttemptsReached,
    CoolingOffPeriod,
    InvalidPhoneNumber,

    /// <summary>
    /// Blocked by the development/pilot allow-list. Kept as a first-class reason so an
    /// unexpectedly empty campaign is diagnosable instead of merely silent.
    /// </summary>
    NotOnAllowlist
}

/// <summary>
/// Append-only audit of every pre-dial compliance evaluation - allows as well as blocks.
///
/// One row is written per decision, before the channel is created. If no row exists for a
/// placed call, that call bypassed the gate, and that is a defect worth failing a release for.
/// </summary>
public sealed class SuppressionCheck : BaseEntity
{
    public string PhoneE164 { get; set; } = default!;

    public Guid? CustomerId { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? CampaignLeadId { get; set; }

    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;

    public SuppressionResult Result { get; set; }

    public SuppressionReason Reason { get; set; } = SuppressionReason.Ok;

    /// <summary>Which consent row the allow decision relied on, so the basis is reconstructible.</summary>
    public Guid? ConsentIdUsed { get; set; }

    /// <summary>
    /// Version of the rule set that produced this decision. When the rules change, historic
    /// decisions must still be explainable under the rules that were live at the time.
    /// </summary>
    public string RuleSetVersion { get; set; } = "1";

    /// <summary>
    /// Evaluation detail for diagnostics - local time used, window compared against, attempt
    /// count seen. Diagnostic only; never the authoritative field for reporting.
    /// </summary>
    public string? DetailJson { get; set; }
}

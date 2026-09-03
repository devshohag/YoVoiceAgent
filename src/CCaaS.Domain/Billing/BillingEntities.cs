using CCaaS.Domain.Common;

namespace CCaaS.Domain.Billing;

// Schema: billing  (Section 15 - Billing & Subscription Architecture)
// Billing boundary reminder: telecom/PSTN minutes are billed by the licensed SIP provider
// directly to the tenant - this module never invoices for call minutes, only the SaaS itself.

public class Plan : BaseEntity
{
    public string Name { get; set; } = default!; // "Voice" | "Omnichannel" | "Enterprise"
    public decimal MonthlyBasePrice { get; set; }
    public string Currency { get; set; } = "BDT";
    public int IncludedSeats { get; set; }
    public int IncludedStorageGb { get; set; }
}

public class Subscription : BaseEntity
{
    public Guid PlanId { get; set; }
    public Plan? Plan { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CancelledAt { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;

    public ICollection<Seat> Seats { get; set; } = new List<Seat>();
    public ICollection<UsageMeter> UsageMeters { get; set; } = new List<UsageMeter>();
}

public enum SubscriptionStatus { Trialing, Active, PastDue, Cancelled }

public class Seat : BaseEntity
{
    public Guid SubscriptionId { get; set; }
    public Guid AgentId { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReleasedAt { get; set; }
}

public class UsageMeter : BaseEntity
{
    // Tracks recording storage GB-months, AI transcription minutes, channel message counts, etc.
    public Guid SubscriptionId { get; set; }
    public string MeterKey { get; set; } = default!; // "storage.gb" | "ai.transcription.minutes" | ...
    public decimal Quantity { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
}

public class Invoice : BaseEntity
{
    public string InvoiceNumber { get; set; } = default!;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime DueAt { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "BDT";
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
}

public enum InvoiceStatus { Draft, Issued, Paid, Overdue, Void }

public class InvoiceLine : BaseEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public string Description { get; set; } = default!;
    public decimal Amount { get; set; }
}

public class Payment : BaseEntity
{
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "BDT";
    public string Method { get; set; } = default!; // "card" | "bkash" | "nagad" | "bank-transfer"
    public string? ExternalTransactionId { get; set; }
    public DateTime PaidAt { get; set; } = DateTime.UtcNow;
}

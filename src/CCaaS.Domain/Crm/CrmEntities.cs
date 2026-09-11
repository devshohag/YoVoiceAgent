using CCaaS.Domain.Common;

namespace CCaaS.Domain.Crm;

// Schema: crm  (Section 4 - Functional Scope: Leads, customers, contacts, notes, tags, customer 360, interaction timeline)

public class Lead : BaseEntity
{
    public string Name { get; set; } = default!;

    /// <summary>As typed or imported. Kept verbatim so an import mistake stays diagnosable.</summary>
    public string? Phone { get; set; }

    /// <summary>
    /// <see cref="Phone"/> normalised to E.164 by <see cref="CCaaS.Domain.Common.PhoneNumber"/>.
    /// Written once, at write time. Every Do-Not-Call match, consent lookup and de-duplication
    /// compares THIS column - never <see cref="Phone"/> - because "01712345678" and
    /// "+8801712345678" are the same subscriber and must not be treated as two.
    /// Null when the raw value could not be normalised; such rows are never dialled.
    /// </summary>
    public string? PhoneE164 { get; set; }

    public string? Email { get; set; }
    public string Source { get; set; } = default!; // e.g. "campaign-import", "web-form"
    public LeadStatus Status { get; set; } = LeadStatus.New;
    public Guid? ConvertedCustomerId { get; set; }
}

public enum LeadStatus
{
    New,
    Contacted,
    Qualified,
    Converted,
    Disqualified
}

public class Customer : BaseEntity
{
    // The "Customer 360" anchor entity - conversations/calls/campaigns all key off this.
    public string Name { get; set; } = default!;

    /// <summary>As typed or imported, kept verbatim.</summary>
    public string? Phone { get; set; }

    /// <summary>
    /// <see cref="Phone"/> normalised to E.164. See <see cref="Lead.PhoneE164"/> for why the
    /// raw and normalised forms are both stored.
    /// </summary>
    public string? PhoneE164 { get; set; }

    /// <summary>
    /// IANA time-zone id for this contact, e.g. "Asia/Dhaka", "Europe/London".
    ///
    /// Outbound calling windows are a per-person rule, not a per-campaign one: 9am in the
    /// campaign's zone can be 3am where the contact actually is. Null means "fall back to the
    /// campaign's TimeZone" - which is correct for a single-country tenant and wrong as soon
    /// as one contact sits in another zone, so importers should populate it when known.
    /// </summary>
    public string? TimeZoneId { get; set; }

    public string? Email { get; set; }
    public string? ExternalReferenceId { get; set; } // link to tenant's own ERP/e-commerce system

    public ICollection<Contact> Contacts { get; set; } = new List<Contact>();
    public ICollection<Note> Notes { get; set; } = new List<Note>();
    public ICollection<CustomerTag> CustomerTags { get; set; } = new List<CustomerTag>();
}

public class Contact : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string Channel { get; set; } = default!; // "phone" | "whatsapp" | "email" | ...
    public string Value { get; set; } = default!;   // e.g. the phone number / handle itself

    /// <summary>
    /// Canonical form of <see cref="Value"/> - E.164 when <see cref="Channel"/> is a dialable
    /// channel, lower-cased address for email. Null when the channel has no canonical form or
    /// the value failed normalisation. Lookups and de-duplication use this column.
    /// </summary>
    public string? ValueNormalized { get; set; }

    public bool IsPrimary { get; set; }
}

public class Note : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public Guid AuthorAgentId { get; set; }
    public string Body { get; set; } = default!;
}

public class Tag : BaseEntity
{
    public string Name { get; set; } = default!;
}

public class CustomerTag : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }
}

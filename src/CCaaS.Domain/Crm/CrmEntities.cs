using CCaaS.Domain.Common;

namespace CCaaS.Domain.Crm;

// Schema: crm  (Section 4 - Functional Scope: Leads, customers, contacts, notes, tags, customer 360, interaction timeline)

public class Lead : BaseEntity
{
    public string Name { get; set; } = default!;
    public string? Phone { get; set; }
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
    public string? Phone { get; set; }
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

namespace CCaaS.Domain.Common;

/// <summary>
/// Base class for every tenant-owned business entity in the system.
/// Mirrors the "standard audit columns" requirement from the project proposal
/// (Section 12, Data Architecture): every tenant-owned table carries TenantId + audit columns.
/// </summary>
public abstract class BaseEntity : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft delete flag - never hard-delete tenant business data.</summary>
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Marker interface used by the global EF Core query filter (see CcaasDbContext)
/// to guarantee that a query can never cross tenant boundaries - this is the
/// "Security invariant" from Section 7 of the proposal:
/// "Tenant A must never be able to read, modify, export, search, stream, or
/// download any object belonging to Tenant B."
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

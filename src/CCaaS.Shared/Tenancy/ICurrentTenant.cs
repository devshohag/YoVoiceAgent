namespace CCaaS.Shared.Tenancy;

/// <summary>
/// Server-resolved tenant context. Section 7 security invariant:
/// "Tenant identity is resolved from authenticated server-side context and never
///  trusted from request payloads." Implemented in Infrastructure by reading the
/// "tenant_id" claim off the validated JWT - never from a query string/body field.
/// </summary>
public interface ICurrentTenant
{
    Guid TenantId { get; }
    bool IsResolved { get; }
}

public interface ICurrentUser
{
    Guid UserId { get; }
    string Email { get; }
    IReadOnlyCollection<string> Roles { get; }
    IReadOnlyCollection<string> Permissions { get; }
    bool IsAuthenticated { get; }
}

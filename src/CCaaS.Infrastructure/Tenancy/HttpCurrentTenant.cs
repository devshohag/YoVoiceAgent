using CCaaS.Shared.Tenancy;
using Microsoft.AspNetCore.Http;

namespace CCaaS.Infrastructure.Tenancy;

/// <summary>
/// Section 7 security invariant: tenant identity is resolved from the authenticated,
/// server-validated JWT "tenant_id" claim - NEVER from a route/query/body parameter the
/// caller controls. Registered as Scoped so it reads the claim once per request.
/// </summary>
public class HttpCurrentTenant : ICurrentTenant
{
    public Guid TenantId { get; }
    public bool IsResolved { get; }

    public HttpCurrentTenant(IHttpContextAccessor httpContextAccessor)
    {
        var claim = httpContextAccessor.HttpContext?.User?.FindFirst("tenant_id")?.Value;
        if (claim is not null && Guid.TryParse(claim, out var tenantId))
        {
            TenantId = tenantId;
            IsResolved = true;
        }
    }
}

public class HttpCurrentUser : ICurrentUser
{
    public Guid UserId { get; }
    public string Email { get; } = string.Empty;
    public IReadOnlyCollection<string> Roles { get; }
    public IReadOnlyCollection<string> Permissions { get; }
    public bool IsAuthenticated { get; }

    public HttpCurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        var user = httpContextAccessor.HttpContext?.User;
        IsAuthenticated = user?.Identity?.IsAuthenticated ?? false;

        var sub = user?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        if (sub is not null && Guid.TryParse(sub, out var userId)) UserId = userId;

        Email = user?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)?.Value ?? string.Empty;
        Roles = user?.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToArray() ?? Array.Empty<string>();
        Permissions = user?.FindAll("permission").Select(c => c.Value).ToArray() ?? Array.Empty<string>();
    }
}

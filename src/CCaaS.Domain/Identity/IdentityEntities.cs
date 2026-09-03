using CCaaS.Domain.Common;

namespace CCaaS.Domain.Identity;

// Schema: identity  (Section 12 - Data Architecture / Section 14 - Security, Compliance & Audit)
// Auth model: ASP.NET Core Identity + JWT access token + rotating refresh token, MFA-ready.

public class User : BaseEntity
{
    public string Email { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public bool MfaEnabled { get; set; }
    public string? MfaSecret { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}

public class Role : BaseEntity
{
    // Example seed roles: PlatformAdmin, TenantAdmin, Supervisor, Agent
    public string Name { get; set; } = default!;
    public string? Description { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}

public class Permission : BaseEntity
{
    // Example: "calls.recording.download", "billing.invoice.view", "campaign.dialer.manage"
    public string Key { get; set; } = default!;
    public string Description { get; set; } = default!;
}

public class UserRole : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

public class RolePermission : BaseEntity
{
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public Guid PermissionId { get; set; }
    public Permission? Permission { get; set; }
}

public class RefreshToken : BaseEntity
{
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = default!;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string CreatedByIp { get; set; } = default!;
}

public class Session : BaseEntity
{
    // Supports "session revocation" requirement from Section 4 (Functional Scope - Identity & Access).
    public Guid UserId { get; set; }
    public string DeviceInfo { get; set; } = default!;
    public string IpAddress { get; set; } = default!;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public bool IsRevoked { get; set; }
}

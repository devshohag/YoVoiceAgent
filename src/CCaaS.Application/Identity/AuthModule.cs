using CCaaS.Application.Common;
using CCaaS.Domain.Identity;

namespace CCaaS.Application.Identity;

// ---- DTOs -------------------------------------------------------------

public record RegisterRequest(Guid TenantId, string Email, string Password, string DisplayName);
public record LoginRequest(string Email, string Password);
public record AuthResult(bool Succeeded, string? AccessToken, string? RefreshToken, string? Error);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword, string ConfirmPassword);
public record ChangePasswordResult(bool Succeeded, string? Error);

// ---- Abstractions -------------------------------------------------------

/// <summary>
/// Implemented in Infrastructure using System.IdentityModel.Tokens.Jwt.
/// Kept as an interface here so Application stays free of that NuGet dependency.
/// </summary>
public interface IJwtTokenGenerator
{
    string GenerateAccessToken(User user, Guid tenantId, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions);
    string GenerateRefreshToken();
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<AuthResult> LoginAsync(LoginRequest request, Guid tenantId, CancellationToken ct = default);
    Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task<ChangePasswordResult> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default);
    Task RevokeSessionAsync(Guid sessionId, CancellationToken ct = default);
}

// ---- Implementation -------------------------------------------------------

public class AuthService : IAuthService
{
    private readonly IRepository<User> _users;
    private readonly IRepository<UserRole> _userRoles;
    private readonly IRepository<Role> _roles;
    private readonly IRepository<RolePermission> _rolePermissions;
    private readonly IRepository<Permission> _permissions;
    private readonly IRepository<RefreshToken> _refreshTokens;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;

    public AuthService(
        IRepository<User> users,
        IRepository<UserRole> userRoles,
        IRepository<Role> roles,
        IRepository<RolePermission> rolePermissions,
        IRepository<Permission> permissions,
        IRepository<RefreshToken> refreshTokens,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator)
    {
        _users = users;
        _userRoles = userRoles;
        _roles = roles;
        _rolePermissions = rolePermissions;
        _permissions = permissions;
        _refreshTokens = refreshTokens;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _jwtTokenGenerator = jwtTokenGenerator;
    }

    /// <summary>
    /// Resolves the actual role names + permission keys assigned to a user, via
    /// UserRole -> Role and Role -> RolePermission -> Permission. Login/Refresh used to pass
    /// Array.Empty() for both, which meant [Authorize(Roles = "...")] could NEVER succeed for
    /// any user, no matter how they were seeded/assigned - this closes that gap.
    /// </summary>
    private async Task<(string[] Roles, string[] Permissions)> ResolveRolesAndPermissionsAsync(Guid userId, CancellationToken ct)
    {
        var userRoles = await _userRoles.ToListAsync(_userRoles.Query().Where(ur => ur.UserId == userId), ct);
        var roleIds = userRoles.Select(ur => ur.RoleId).Distinct().ToList();
        if (roleIds.Count == 0)
            return (Array.Empty<string>(), Array.Empty<string>());

        var roles = await _roles.ToListAsync(_roles.Query().Where(r => roleIds.Contains(r.Id)), ct);
        var roleNames = roles.Select(r => r.Name).ToArray();

        var rolePermissions = await _rolePermissions.ToListAsync(
            _rolePermissions.Query().Where(rp => roleIds.Contains(rp.RoleId)), ct);
        var permissionIds = rolePermissions.Select(rp => rp.PermissionId).Distinct().ToList();
        if (permissionIds.Count == 0)
            return (roleNames, Array.Empty<string>());

        var permissions = await _permissions.ToListAsync(_permissions.Query().Where(p => permissionIds.Contains(p.Id)), ct);
        return (roleNames, permissions.Select(p => p.Key).ToArray());
    }

    public async Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var exists = await _users.AnyAsync(u => u.Email == request.Email && u.TenantId == request.TenantId, ct);
        if (exists)
            return new AuthResult(false, null, null, "A user with this email already exists for this tenant.");

        var user = new User
        {
            TenantId = request.TenantId,
            Email = request.Email,
            DisplayName = request.DisplayName,
            PasswordHash = _passwordHasher.Hash(request.Password)
        };

        await _users.AddAsync(user, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        // TODO: assign default "Agent" role via UserRole - left for you to wire up
        // once the Organization module creates the matching Agent record.
        return new AuthResult(true, null, null, null);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, Guid tenantId, CancellationToken ct = default)
    {
        var user = await _users.FirstOrDefaultAsync(u => u.Email == request.Email && u.TenantId == tenantId, ct);
        if (user is null || !user.IsActive || !_passwordHasher.Verify(request.Password, user.PasswordHash))
            return new AuthResult(false, null, null, "Invalid credentials.");

        var (roleNames, permissionKeys) = await ResolveRolesAndPermissionsAsync(user.Id, ct);
        var accessToken = _jwtTokenGenerator.GenerateAccessToken(user, tenantId, roleNames, permissionKeys);
        var refreshTokenValue = _jwtTokenGenerator.GenerateRefreshToken();

        await _refreshTokens.AddAsync(new RefreshToken
        {
            TenantId = tenantId,
            UserId = user.Id,
            TokenHash = refreshTokenValue, // TODO: hash before storing in production
            ExpiresAt = DateTime.UtcNow.AddDays(14),
            CreatedByIp = "unknown" // set from HttpContext in the controller
        }, ct);

        user.LastLoginAt = DateTime.UtcNow;
        _users.Update(user);
        await _unitOfWork.SaveChangesAsync(ct);

        return new AuthResult(true, accessToken, refreshTokenValue, null);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var token = await _refreshTokens.FirstOrDefaultAsync(t => t.TokenHash == refreshToken && t.RevokedAt == null, ct);

        if (token is null || token.ExpiresAt < DateTime.UtcNow)
            return new AuthResult(false, null, null, "Refresh token is invalid or expired.");

        var user = await _users.GetByIdAsync(token.UserId, ct);
        if (user is null)
            return new AuthResult(false, null, null, "User not found.");

        var (roleNames, permissionKeys) = await ResolveRolesAndPermissionsAsync(user.Id, ct);
        var newAccessToken = _jwtTokenGenerator.GenerateAccessToken(user, user.TenantId, roleNames, permissionKeys);
        var newRefreshToken = _jwtTokenGenerator.GenerateRefreshToken();

        token.RevokedAt = DateTime.UtcNow;
        token.ReplacedByTokenHash = newRefreshToken;
        _refreshTokens.Update(token);

        await _refreshTokens.AddAsync(new RefreshToken
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            TokenHash = newRefreshToken,
            ExpiresAt = DateTime.UtcNow.AddDays(14),
            CreatedByIp = "unknown"
        }, ct);

        await _unitOfWork.SaveChangesAsync(ct);
        return new AuthResult(true, newAccessToken, newRefreshToken, null);
    }

    public async Task<ChangePasswordResult> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
            return new(false, "Current password is required.");
        if (request.NewPassword != request.ConfirmPassword)
            return new(false, "New password and confirmation do not match.");
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 12
            || !request.NewPassword.Any(char.IsUpper) || !request.NewPassword.Any(char.IsLower)
            || !request.NewPassword.Any(char.IsDigit) || !request.NewPassword.Any(ch => !char.IsLetterOrDigit(ch)))
            return new(false, "New password must be at least 12 characters and include uppercase, lowercase, number, and special characters.");

        var user = await _users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive) return new(false, "User account is unavailable.");
        if (!_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash)) return new(false, "Current password is incorrect.");
        if (_passwordHasher.Verify(request.NewPassword, user.PasswordHash)) return new(false, "New password must be different from the current password.");

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        _users.Update(user);
        var tokens = await _refreshTokens.ToListAsync(_refreshTokens.Query().Where(t => t.UserId == userId && t.RevokedAt == null), ct);
        foreach (var token in tokens) { token.RevokedAt = DateTime.UtcNow; _refreshTokens.Update(token); }
        await _unitOfWork.SaveChangesAsync(ct);
        return new(true, null);
    }

    public async Task RevokeSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        // Section 4 requirement: "session revocation". Wire this to the Session entity
        // once you add ICurrentUser-based session tracking to the login flow.
        await Task.CompletedTask;
    }
}

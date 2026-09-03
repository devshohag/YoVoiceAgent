using CCaaS.Application.Identity;
using CCaaS.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenantEntity = CCaaS.Domain.Tenant.Tenant;

namespace CCaaS.Infrastructure.Persistence;

/// <summary>
/// Dev-only bootstrap seeding. Without this, the template has NO way to log in at all on a
/// fresh database: /api/auth/register requires an existing TenantId, and the only endpoint
/// that creates a tenant (POST /api/tenants) requires [Authorize(Roles = "PlatformAdmin")] -
/// a role nobody can ever hold, since nothing else creates a user+role. That's a real
/// chicken-and-egg gap in the scaffold (see README "Where to go next" - role/permission
/// seeding was left as a TODO); this closes it for local development only.
///
/// Fixed, well-known IDs/credentials on purpose - so they can be documented and used
/// immediately without needing to grep container logs. The seed is also a non-destructive
/// repair: an existing canonical development tenant keeps all of its data while missing
/// roles/admin mappings are restored. It runs ONLY from Program.cs's Development branch.
/// </summary>
public static class DevDataSeeder
{
    public static readonly Guid DefaultTenantId = new("3FA85F64-5717-4562-B3FC-2C963F66AFA6");
    public const string DefaultAdminEmail = "admin@ccaas.local";
    public const string DefaultAdminPassword = "Admin@12345";

    public static async Task SeedAsync(CcaasDbContext db, IPasswordHasher passwordHasher, ILogger logger, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == DefaultTenantId, ct);
        if (tenant is null)
        {
            tenant = new TenantEntity
            {
                Id = DefaultTenantId,
                Name = "Default Tenant",
                Slug = "default",
                Edition = CCaaS.Domain.Tenant.TenantEdition.Enterprise,
                Status = CCaaS.Domain.Tenant.TenantStatus.Active
            };
            db.Tenants.Add(tenant);
        }

        if (!await db.TenantSettings.IgnoreQueryFilters().AnyAsync(x => x.TenantId == DefaultTenantId, ct))
            db.TenantSettings.Add(new CCaaS.Domain.Tenant.TenantSettings { TenantId = DefaultTenantId });

        var roleNames = new[] { "PlatformAdmin", "TenantAdmin", "Supervisor", "Agent" };
        var existingRoles = await db.Roles.IgnoreQueryFilters()
            .Where(x => x.TenantId == DefaultTenantId && roleNames.Contains(x.Name))
            .ToListAsync(ct);
        foreach (var roleName in roleNames.Except(existingRoles.Select(x => x.Name)))
        {
            var role = new Role { TenantId = DefaultTenantId, Name = roleName };
            db.Roles.Add(role);
            existingRoles.Add(role);
        }
        var adminRole = existingRoles.First(r => r.Name == "PlatformAdmin");

        var adminUser = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(
            x => x.TenantId == DefaultTenantId && x.Email == DefaultAdminEmail, ct);
        if (adminUser is null)
        {
            adminUser = new User
            {
                TenantId = DefaultTenantId,
                Email = DefaultAdminEmail,
                DisplayName = "Default Admin",
                PasswordHash = passwordHasher.Hash(DefaultAdminPassword),
                IsActive = true
            };
            db.Users.Add(adminUser);
        }
        else
        {
            adminUser.IsActive = true;
        }

        if (!await db.UserRoles.IgnoreQueryFilters().AnyAsync(
                x => x.TenantId == DefaultTenantId && x.UserId == adminUser.Id && x.RoleId == adminRole.Id, ct))
            db.UserRoles.Add(new UserRole
            {
                TenantId = DefaultTenantId,
                UserId = adminUser.Id,
                RoleId = adminRole.Id
            });

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Canonical development tenant/admin configuration ready (local dev only). " +
            "TenantId={TenantId} Email={Email} Password={Password}",
            tenant.Id, DefaultAdminEmail, DefaultAdminPassword);
    }
}

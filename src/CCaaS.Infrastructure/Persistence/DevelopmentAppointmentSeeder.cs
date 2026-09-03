using CCaaS.Domain.Appointment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CCaaS.Infrastructure.Persistence;

public static class DevelopmentAppointmentSeeder
{
    public static async Task SeedAsync(CcaasDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var tenantIds = await db.Tenants.IgnoreQueryFilters()
            .Select(x => x.Id).ToListAsync(ct);
        if (tenantIds.Count == 0) return;

        var totalAdded = 0;
        foreach (var tenantId in tenantIds)
            totalAdded += await SeedTenantAsync(db, tenantId, ct);

        logger.LogInformation("Development appointment schedules ready. Tenants={TenantCount} AddedSlots={AddedSlots}",
            tenantIds.Count, totalAdded);
    }

    private static async Task<int> SeedTenantAsync(CcaasDbContext db, Guid tenantId, CancellationToken ct)
    {

        var provider = await db.AppointmentProviders.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Name == "Development Appointment Desk", ct);
        if (provider is null)
        {
            provider = new AppointmentProvider
            {
                TenantId = tenantId,
                Name = "Development Appointment Desk",
                TimeZoneId = "UTC"
            };
            db.AppointmentProviders.Add(provider);
            await db.SaveChangesAsync(ct);
        }

        var firstDay = DateTime.UtcNow.Date.AddDays(1);
        var lastDay = firstDay.AddDays(14);
        var existingStarts = await db.AppointmentAvailabilitySlots.IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId && x.AppointmentProviderId == provider.Id
                && x.StartsAtUtc >= firstDay && x.StartsAtUtc < lastDay)
            .Select(x => x.StartsAtUtc).ToListAsync(ct);
        var starts = existingStarts.ToHashSet();
        var times = new[] { new TimeSpan(14, 0, 0), new TimeSpan(15, 30, 0), new TimeSpan(16, 15, 0) };
        var added = 0;
        for (var day = firstDay; day < lastDay; day = day.AddDays(1))
        {
            foreach (var time in times)
            {
                var startsAt = day.Add(time);
                if (starts.Contains(startsAt)) continue;
                db.AppointmentAvailabilitySlots.Add(new AppointmentAvailabilitySlot
                {
                    TenantId = tenantId,
                    AppointmentProviderId = provider.Id,
                    StartsAtUtc = startsAt,
                    EndsAtUtc = startsAt.AddMinutes(30)
                });
                added++;
            }
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }
}

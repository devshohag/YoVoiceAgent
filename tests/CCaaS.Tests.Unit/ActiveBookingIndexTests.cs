using CCaaS.Domain.Appointment;
using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

public sealed class ActiveBookingIndexTests
{
    [Fact]
    public void Only_active_bookings_participate_in_slot_uniqueness()
    {
        using var db = new CcaasDbContext(new DbContextOptionsBuilder<CcaasDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true;TrustServerCertificate=true").Options);
        var entity = db.Model.FindEntityType(typeof(AppointmentBooking))!;
        var index = entity.GetIndexes().Single(i => i.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { "TenantId", "AvailabilitySlotId" }));
        Assert.True(index.IsUnique);
        Assert.Equal("[Status] = 0 AND [IsDeleted] = 0", index.GetFilter());
        // Metadata only: a real SQL Server race test remains a deployment gate.
    }
}

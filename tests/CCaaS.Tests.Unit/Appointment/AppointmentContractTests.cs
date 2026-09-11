using CCaaS.Application.Appointment;
using CCaaS.Domain.Appointment;
using CCaaS.Domain.Scheduling.Booking;
using CCaaS.Infrastructure.Appointment;
using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCaaS.Tests.Unit.Appointment;

public sealed class AppointmentContractTests
{
    [Theory]
    [InlineData("01712345678", "BD", "+8801712345678")]
    [InlineData("+8801712345678", "BD", "+8801712345678")]
    [InlineData("+14155552671", "BD", "+14155552671")]
    [InlineData("4155552671", "US", "+14155552671")]
    [InlineData(" Example@Example.com ", "BD", "example@example.com")]
    public void Shared_normalization_preserves_phone_and_email(string raw, string region, string expected)
    {
        var result = AppointmentService.NormaliseContact(raw, region);
        Assert.True(result.Ok);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a phone")]
    [InlineData("0171234567")]
    public void Invalid_contact_has_a_spoken_error(string? raw)
    {
        var result = AppointmentService.NormaliseContact(raw, "BD");
        Assert.False(result.Ok);
        Assert.Null(result.Value);
        Assert.StartsWith("Please", result.Error!);
    }

    [Fact]
    public void Key_is_stable_case_insensitive_and_scoped()
    {
        var tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var slot = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var key = BookingIdempotency.Derive(tenant, slot, "A@EXAMPLE.COM");
        Assert.Equal(key, BookingIdempotency.Derive(tenant, slot, " a@example.com "));
        Assert.Equal(64, key.Length);
        Assert.NotEqual(key, BookingIdempotency.Derive(Guid.NewGuid(), slot, "a@example.com"));
        Assert.NotEqual(key, BookingIdempotency.Derive(tenant, Guid.NewGuid(), "a@example.com"));
        Assert.NotEqual(key, BookingIdempotency.Derive(tenant, slot, "b@example.com"));
    }

    [Theory]
    [InlineData(BookingOutcome.SlotTaken, BookingFailure.SlotTaken)]
    [InlineData(BookingOutcome.InvalidDetails, BookingFailure.InvalidDetails)]
    [InlineData(BookingOutcome.ProviderUnavailable, BookingFailure.SystemError)]
    [InlineData(BookingOutcome.SystemError, BookingFailure.SystemError)]
    public void Outcomes_map_to_the_existing_machine(BookingOutcome outcome, BookingFailure expected)
        => Assert.Equal(expected, outcome.ToFailure());

    [Theory]
    [InlineData(BookingOutcome.Booked)]
    [InlineData(BookingOutcome.AlreadyYours)]
    public void Success_is_not_a_failure(BookingOutcome outcome)
        => Assert.Throws<ArgumentOutOfRangeException>(() => outcome.ToFailure());

    [Fact]
    public void Model_keeps_original_unique_index_and_matches_snapshot()
    {
        using var db = new CcaasDbContext(new DbContextOptionsBuilder<CcaasDbContext>()
            .UseSqlServer("Server=localhost;Database=ModelOnly;Integrated Security=true;TrustServerCertificate=true").Options);
        var slot = db.Model.FindEntityType(typeof(AppointmentAvailabilitySlot))!;
        Assert.True(slot.FindProperty("RowVersion")!.IsConcurrencyToken);
        Assert.Equal("rowversion", slot.FindProperty("RowVersion")!.GetColumnType());
        var booking = db.Model.FindEntityType(typeof(AppointmentBooking))!;
        var index = booking.GetIndexes().Single(x => x.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { "TenantId", "AvailabilitySlotId" }));
        Assert.True(index.IsUnique);
        Assert.Null(index.GetFilter());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}

using System.Text.Json;
using CCaaS.Application.Appointment;
using CCaaS.Domain.Appointment;
using CCaaS.Infrastructure.Ai;

namespace CCaaS.Tests.Unit.Ai;

public sealed class BookAppointmentToolTests
{
    [Theory]
    [InlineData(BookingOutcome.Booked, false)]
    [InlineData(BookingOutcome.AlreadyYours, true)]
    public async Task Successful_retry_outcome_is_structured(BookingOutcome outcome, bool wasExisting)
    {
        var bookingId = Guid.NewGuid();
        var service = new StubAppointmentService(new BookingAttempt(outcome, bookingId,
            "APT-SAME-REFERENCE", new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc),
            "Confirmed", null));
        var tool = new BookAppointmentTool(service);

        using var result = JsonDocument.Parse(await tool.ExecuteAsync(Guid.NewGuid(), Arguments()));

        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(outcome.ToString(), result.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(wasExisting, result.RootElement.GetProperty("wasExisting").GetBoolean());
        Assert.Equal("APT-SAME-REFERENCE", result.RootElement.GetProperty("bookingReference").GetString());
        Assert.Equal(1, service.TryBookCalls);
        Assert.Equal(0, service.BookCalls);
    }

    [Fact]
    public async Task Failed_outcome_is_returned_instead_of_thrown()
    {
        var service = new StubAppointmentService(new BookingAttempt(BookingOutcome.SlotTaken,
            null, null, null, null, "That appointment slot was just booked."));
        var tool = new BookAppointmentTool(service);

        using var result = JsonDocument.Parse(await tool.ExecuteAsync(Guid.NewGuid(), Arguments()));

        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("SlotTaken", result.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("That appointment slot was just booked.", result.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("voucherUrl").ValueKind);
    }

    private static string Arguments() => JsonSerializer.Serialize(new
    {
        slotId = Guid.NewGuid(),
        customerName = "Ada Lovelace",
        contact = "+14155552671",
        purpose = "Consultation"
    });

    private sealed class StubAppointmentService : IAppointmentService
    {
        private readonly BookingAttempt _attempt;
        public int TryBookCalls { get; private set; }
        public int BookCalls { get; private set; }

        public StubAppointmentService(BookingAttempt attempt) => _attempt = attempt;

        public Task<BookingAttempt> TryBookAsync(Guid tenantId, BookAppointmentCommand command,
            CancellationToken ct = default)
        {
            TryBookCalls++;
            return Task.FromResult(_attempt);
        }

        public Task<AppointmentBookingResult> BookAsync(Guid tenantId, BookAppointmentCommand command,
            CancellationToken ct = default)
        {
            BookCalls++;
            throw new InvalidOperationException("The retry-safe handler must not use BookAsync.");
        }

        public Task<IReadOnlyList<AvailableAppointmentSlot>> GetAvailabilityAsync(Guid tenantId,
            DateOnly date, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AvailableAppointmentSlot>> GetAvailabilityAsync(Guid tenantId,
            DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AppointmentVoucherData>> FindUpcomingAsync(Guid tenantId,
            string contact, DateTime fromUtc, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AppointmentChangeResult> CancelAsync(Guid tenantId, Guid bookingId,
            string reason, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AppointmentChangeResult> RescheduleAsync(Guid tenantId, Guid bookingId,
            Guid newSlotId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AppointmentBooking>> GetBookingsAsync(Guid tenantId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AppointmentVoucherData?> GetVoucherAsync(Guid tenantId, Guid bookingId,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}

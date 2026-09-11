using CCaaS.Domain.Appointment;

namespace CCaaS.Application.Appointment;

public record AvailableAppointmentSlot(Guid SlotId, Guid ProviderId, string ProviderName, DateTime StartsAtUtc, DateTime EndsAtUtc);
public record BookAppointmentCommand(Guid SlotId, string CustomerName, string CustomerContact, string? Purpose);
public record AppointmentBookingResult(Guid BookingId, string BookingReference, DateTime StartsAtUtc, string Status, bool WasExisting);
public record AppointmentVoucherData(Guid BookingId, string BookingReference, string CustomerName,
    string CustomerContact, string? Purpose, string ProviderName, DateTime StartsAtUtc,
    DateTime EndsAtUtc, string Status, DateTime ConfirmedAtUtc);

public enum BookingOutcome { Booked, AlreadyYours, SlotTaken, InvalidDetails, ProviderUnavailable, SystemError }

public sealed record BookingAttempt(BookingOutcome Outcome, Guid? BookingId,
    string? BookingReference, DateTime? StartsAtUtc, string? Status, string? Error);

public static class BookingOutcomeMapping
{
    public static CCaaS.Domain.Scheduling.Booking.BookingFailure ToFailure(this BookingOutcome outcome) => outcome switch
    {
        BookingOutcome.SlotTaken => CCaaS.Domain.Scheduling.Booking.BookingFailure.SlotTaken,
        BookingOutcome.InvalidDetails => CCaaS.Domain.Scheduling.Booking.BookingFailure.InvalidDetails,
        BookingOutcome.ProviderUnavailable or BookingOutcome.SystemError => CCaaS.Domain.Scheduling.Booking.BookingFailure.SystemError,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), "A successful booking is not a failure.")
    };
}

public interface IAppointmentService
{
    Task<IReadOnlyList<AvailableAppointmentSlot>> GetAvailabilityAsync(Guid tenantId, DateOnly date, CancellationToken ct = default);
    Task<IReadOnlyList<AvailableAppointmentSlot>> GetAvailabilityAsync(Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task<BookingAttempt> TryBookAsync(Guid tenantId, BookAppointmentCommand command, CancellationToken ct = default);
    Task<AppointmentBookingResult> BookAsync(Guid tenantId, BookAppointmentCommand command, CancellationToken ct = default);
    Task<IReadOnlyList<AppointmentBooking>> GetBookingsAsync(Guid tenantId, CancellationToken ct = default);
    Task<AppointmentVoucherData?> GetVoucherAsync(Guid tenantId, Guid bookingId, CancellationToken ct = default);
}

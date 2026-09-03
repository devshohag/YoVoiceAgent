using CCaaS.Domain.Appointment;

namespace CCaaS.Application.Appointment;

public record AvailableAppointmentSlot(Guid SlotId, Guid ProviderId, string ProviderName, DateTime StartsAtUtc, DateTime EndsAtUtc);
public record BookAppointmentCommand(Guid SlotId, string CustomerName, string CustomerContact, string? Purpose);
public record AppointmentBookingResult(Guid BookingId, string BookingReference, DateTime StartsAtUtc, string Status, bool WasExisting);
public record AppointmentVoucherData(Guid BookingId, string BookingReference, string CustomerName,
    string CustomerContact, string? Purpose, string ProviderName, DateTime StartsAtUtc,
    DateTime EndsAtUtc, string Status, DateTime ConfirmedAtUtc);

public interface IAppointmentService
{
    Task<IReadOnlyList<AvailableAppointmentSlot>> GetAvailabilityAsync(Guid tenantId, DateOnly date, CancellationToken ct = default);
    Task<AppointmentBookingResult> BookAsync(Guid tenantId, BookAppointmentCommand command, CancellationToken ct = default);
    Task<IReadOnlyList<AppointmentBooking>> GetBookingsAsync(Guid tenantId, CancellationToken ct = default);
    Task<AppointmentVoucherData?> GetVoucherAsync(Guid tenantId, Guid bookingId, CancellationToken ct = default);
}

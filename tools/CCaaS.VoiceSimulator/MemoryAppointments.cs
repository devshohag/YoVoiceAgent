using CCaaS.Application.Appointment;
using CCaaS.Domain.Appointment;

namespace CCaaS.VoiceSimulator;

// Test double only. Never registered with the API/worker dependency container.
public sealed class MemoryAppointments(Guid tenant, IEnumerable<AvailableAppointmentSlot> initial) : IAppointmentService
{
    private readonly object gate = new();
    private readonly List<AvailableAppointmentSlot> slots = initial.ToList();
    private readonly Dictionary<Guid, AppointmentBooking> bookings = new();
    public int Mutations { get; private set; }
    public bool FailNextWrite { get; set; }
    public bool TimeoutNextWrite { get; set; }
    private void Check(Guid id) { if (id != tenant) throw new UnauthorizedAccessException(); }
    public Task<IReadOnlyList<AvailableAppointmentSlot>> GetAvailabilityAsync(Guid id, DateOnly date, CancellationToken ct = default)
    {
        Check(id);
        lock (gate) return Task.FromResult<IReadOnlyList<AvailableAppointmentSlot>>(slots.Where(s => DateOnly.FromDateTime(s.StartsAtUtc) == date
            && !bookings.Values.Any(b => b.AvailabilitySlotId == s.SlotId && b.Status == AppointmentBookingStatus.Confirmed)).ToArray());
    }
    public Task<AppointmentBookingResult> BookAsync(Guid id, BookAppointmentCommand command, CancellationToken ct = default)
    {
        Check(id);
        lock (gate)
        {
            if (TimeoutNextWrite) { TimeoutNextWrite = false; throw new TimeoutException("Simulated uncertain backend result"); }
            if (FailNextWrite) { FailNextWrite = false; throw new InvalidOperationException("Simulated slot race"); }
            var slot = slots.Single(s => s.SlotId == command.SlotId);
            var previous = bookings.Values.FirstOrDefault(b => b.AvailabilitySlotId == command.SlotId && b.Status == AppointmentBookingStatus.Confirmed);
            if (previous is not null)
            {
                if (previous.CustomerContact != command.CustomerContact || previous.CustomerName != command.CustomerName) throw new InvalidOperationException("Occupied");
                return Task.FromResult(new AppointmentBookingResult(previous.Id, previous.BookingReference, slot.StartsAtUtc, "Confirmed", true));
            }
            var booking = new AppointmentBooking { Id = Guid.NewGuid(), TenantId = id, AvailabilitySlotId = slot.SlotId,
                AppointmentProviderId = slot.ProviderId, CustomerName = command.CustomerName, CustomerContact = command.CustomerContact,
                BookingReference = "APT-20260908-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant() };
            bookings.Add(booking.Id, booking); Mutations++;
            return Task.FromResult(new AppointmentBookingResult(booking.Id, booking.BookingReference, slot.StartsAtUtc, "Confirmed", false));
        }
    }
    public Task<VoiceBookingIdentity?> FindForVoiceAsync(Guid id, string reference, string contact, CancellationToken ct = default)
    {
        Check(id);
        lock (gate)
        {
            var b = bookings.Values.SingleOrDefault(b => b.BookingReference == reference && b.CustomerContact == contact);
            return Task.FromResult(b is null ? null : new VoiceBookingIdentity(b.Id, b.CustomerName, b.BookingReference));
        }
    }
    public Task CancelForVoiceAsync(Guid id, Guid bookingId, string contact, CancellationToken ct = default)
    {
        Check(id);
        lock (gate)
        {
            var b = bookings[bookingId]; if (b.CustomerContact != contact) throw new InvalidOperationException();
            if (FailNextWrite) { FailNextWrite = false; throw new InvalidOperationException(); }
            if (b.Status != AppointmentBookingStatus.Cancelled) { b.Status = AppointmentBookingStatus.Cancelled; Mutations++; }
            return Task.CompletedTask;
        }
    }
    public Task<AppointmentBookingResult> RescheduleForVoiceAsync(Guid id, Guid bookingId, string contact, Guid targetSlotId, CancellationToken ct = default)
    {
        Check(id);
        lock (gate)
        {
            var b = bookings[bookingId];
            if (b.CustomerContact != contact || b.Status != AppointmentBookingStatus.Confirmed || FailNextWrite)
            { FailNextWrite = false; throw new InvalidOperationException(); }
            if (bookings.Values.Any(x => x.Id != b.Id && x.Status == AppointmentBookingStatus.Confirmed && x.AvailabilitySlotId == targetSlotId))
                throw new InvalidOperationException();
            var target = slots.Single(s => s.SlotId == targetSlotId);
            var existing = b.AvailabilitySlotId == targetSlotId;
            b.AvailabilitySlotId = targetSlotId;
            if (!existing) Mutations++;
            return Task.FromResult(new AppointmentBookingResult(b.Id, b.BookingReference, target.StartsAtUtc, "Confirmed", existing));
        }
    }
    public Task<IReadOnlyList<AppointmentBooking>> GetBookingsAsync(Guid id, CancellationToken ct = default)
    { Check(id); lock (gate) return Task.FromResult<IReadOnlyList<AppointmentBooking>>(bookings.Values.ToArray()); }
    public Task<AppointmentVoucherData?> GetVoucherAsync(Guid id, Guid bookingId, CancellationToken ct = default)
    { Check(id); return Task.FromResult<AppointmentVoucherData?>(null); }
}

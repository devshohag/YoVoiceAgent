using CCaaS.Domain.Common;

namespace CCaaS.Domain.Appointment;

public sealed class AppointmentProvider : BaseEntity
{
    public string Name { get; set; } = default!;
    public string TimeZoneId { get; set; } = "UTC";
    public bool IsActive { get; set; } = true;
}

public sealed class AppointmentAvailabilitySlot : BaseEntity
{
    public Guid AppointmentProviderId { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public AppointmentSlotStatus Status { get; set; } = AppointmentSlotStatus.Available;
}

public sealed class AppointmentBooking : BaseEntity
{
    public Guid AppointmentProviderId { get; set; }
    public Guid AvailabilitySlotId { get; set; }
    public string BookingReference { get; set; } = default!;
    public string CustomerName { get; set; } = default!;
    public string CustomerContact { get; set; } = default!;
    public string? Purpose { get; set; }
    public AppointmentBookingStatus Status { get; set; } = AppointmentBookingStatus.Confirmed;
    public DateTime ConfirmedAtUtc { get; set; } = DateTime.UtcNow;
}

public enum AppointmentSlotStatus { Available, Reserved, Booked, Blocked }
public enum AppointmentBookingStatus { Confirmed, Cancelled, Completed, NoShow }

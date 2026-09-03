using CCaaS.Application.Appointment;
using CCaaS.Domain.Appointment;
using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Net.Mail;

namespace CCaaS.Infrastructure.Appointment;

internal sealed class AppointmentService : IAppointmentService
{
    private readonly CcaasDbContext _db;

    public AppointmentService(CcaasDbContext db) => _db = db;

    public async Task<IReadOnlyList<AvailableAppointmentSlot>> GetAvailabilityAsync(
        Guid tenantId, DateOnly date, CancellationToken ct = default)
    {
        EnsureTenant(tenantId);
        var start = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var end = start.AddDays(1);

        // The tenant predicate is explicit so this also works from AI/background scopes.
        return await (
            from slot in _db.AppointmentAvailabilitySlots.IgnoreQueryFilters()
            join provider in _db.AppointmentProviders.IgnoreQueryFilters()
                on slot.AppointmentProviderId equals provider.Id
            where slot.TenantId == tenantId
                  && provider.TenantId == tenantId
                  && !slot.IsDeleted
                  && !provider.IsDeleted
                  && provider.IsActive
                  && slot.Status == AppointmentSlotStatus.Available
                  && slot.StartsAtUtc >= start
                  && slot.StartsAtUtc < end
            orderby slot.StartsAtUtc
            select new AvailableAppointmentSlot(
                slot.Id, provider.Id, provider.Name, slot.StartsAtUtc, slot.EndsAtUtc))
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<AppointmentBookingResult> BookAsync(
        Guid tenantId, BookAppointmentCommand command, CancellationToken ct = default)
    {
        EnsureTenant(tenantId);
        if (string.IsNullOrWhiteSpace(command.CustomerName))
            throw new ArgumentException("Customer name is required.");
        var normalizedContact = NormalizeContact(command.CustomerContact);

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var existing = await _db.AppointmentBookings.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId
                                       && !x.IsDeleted
                                       && x.AvailabilitySlotId == command.SlotId, ct);
        if (existing is not null)
        {
            if (!string.Equals(existing.CustomerContact, normalizedContact, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("That appointment slot has already been booked.");

            var existingSlot = await FindTenantSlotAsync(tenantId, command.SlotId, ct)
                ?? throw new KeyNotFoundException("Appointment slot was not found.");
            var existingResult = new AppointmentBookingResult(existing.Id, existing.BookingReference,
                existingSlot.StartsAtUtc, existing.Status.ToString(), true);
            await transaction.CommitAsync(ct);
            return existingResult;
        }

        var slot = await FindTenantSlotAsync(tenantId, command.SlotId, ct)
            ?? throw new KeyNotFoundException("Appointment slot was not found.");
        if (slot.Status != AppointmentSlotStatus.Available)
            throw new InvalidOperationException("That appointment slot is no longer available.");

        var providerExists = await _db.AppointmentProviders.IgnoreQueryFilters()
            .AnyAsync(x => x.Id == slot.AppointmentProviderId
                           && x.TenantId == tenantId
                           && !x.IsDeleted
                           && x.IsActive, ct);
        if (!providerExists)
            throw new InvalidOperationException("The appointment provider is not available.");

        var booking = new AppointmentBooking
        {
            TenantId = tenantId,
            AppointmentProviderId = slot.AppointmentProviderId,
            AvailabilitySlotId = slot.Id,
            BookingReference = $"APT-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..23].ToUpperInvariant(),
            CustomerName = command.CustomerName.Trim(),
            CustomerContact = normalizedContact,
            Purpose = string.IsNullOrWhiteSpace(command.Purpose) ? null : command.Purpose.Trim()
        };

        slot.Status = AppointmentSlotStatus.Booked;
        slot.UpdatedAt = DateTime.UtcNow;
        _db.AppointmentBookings.Add(booking);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new AppointmentBookingResult(booking.Id, booking.BookingReference,
            slot.StartsAtUtc, booking.Status.ToString(), false);
    }

    public async Task<IReadOnlyList<AppointmentBooking>> GetBookingsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        EnsureTenant(tenantId);
        return await _db.AppointmentBookings.IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .OrderByDescending(x => x.ConfirmedAtUtc)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<AppointmentVoucherData?> GetVoucherAsync(
        Guid tenantId, Guid bookingId, CancellationToken ct = default)
    {
        EnsureTenant(tenantId);
        return await (
            from booking in _db.AppointmentBookings.IgnoreQueryFilters()
            join slot in _db.AppointmentAvailabilitySlots.IgnoreQueryFilters()
                on booking.AvailabilitySlotId equals slot.Id
            join provider in _db.AppointmentProviders.IgnoreQueryFilters()
                on booking.AppointmentProviderId equals provider.Id
            where booking.Id == bookingId
                  && booking.TenantId == tenantId
                  && slot.TenantId == tenantId
                  && provider.TenantId == tenantId
                  && !booking.IsDeleted
                  && !slot.IsDeleted
                  && !provider.IsDeleted
            select new AppointmentVoucherData(
                booking.Id,
                booking.BookingReference,
                booking.CustomerName,
                booking.CustomerContact,
                booking.Purpose,
                provider.Name,
                slot.StartsAtUtc,
                slot.EndsAtUtc,
                booking.Status.ToString(),
                booking.ConfirmedAtUtc))
            .AsNoTracking()
            .SingleOrDefaultAsync(ct);
    }

    private Task<AppointmentAvailabilitySlot?> FindTenantSlotAsync(
        Guid tenantId, Guid slotId, CancellationToken ct) =>
        _db.AppointmentAvailabilitySlots.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == slotId && x.TenantId == tenantId && !x.IsDeleted, ct);

    private static void EnsureTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new UnauthorizedAccessException("A valid tenant is required.");
    }

    private static string NormalizeContact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Customer contact is required.");

        var contact = value.Trim();
        // Preserve the existing email-contact capability for non-voice clients.
        if (contact.Contains('@') && MailAddress.TryCreate(contact, out var email)
            && string.Equals(email.Address, contact, StringComparison.OrdinalIgnoreCase))
            return email.Address.ToLowerInvariant();

        var digits = string.Concat(contact.Where(char.IsDigit));
        if (digits.StartsWith("880", StringComparison.Ordinal) && digits.Length == 13)
            digits = "0" + digits[3..];

        if (digits.Length != 11 || !digits.StartsWith("01", StringComparison.Ordinal)
            || digits[2] is < '3' or > '9')
            throw new ArgumentException(
                "The phone number is incomplete or invalid. Please repeat the full 11-digit Bangladesh mobile number, for example 01712345678.");

        return digits;
    }
}

using CCaaS.Application.Appointment;
using CCaaS.Domain.Appointment;
using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using CCaaS.Domain.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Mail;

namespace CCaaS.Infrastructure.Appointment;

internal sealed partial class AppointmentService : IAppointmentService
{
    private readonly CcaasDbContext _db;

    private readonly ILogger<AppointmentService> _logger;

    public AppointmentService(CcaasDbContext db, ILogger<AppointmentService>? logger = null)
    {
        _db = db;
        _logger = logger ?? NullLogger<AppointmentService>.Instance;
    }

    public Task<IReadOnlyList<AvailableAppointmentSlot>> GetAvailabilityAsync(
        Guid tenantId, DateOnly date, CancellationToken ct = default)
    {
        var start = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        return GetAvailabilityAsync(tenantId, start, start.AddDays(1), ct);
    }

    public async Task<IReadOnlyList<AvailableAppointmentSlot>> GetAvailabilityAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        EnsureTenant(tenantId);
        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || fromUtc >= toUtc)
            throw new ArgumentException("Supply an increasing UTC range.");
        return await (
            from slot in _db.AppointmentAvailabilitySlots.IgnoreQueryFilters()
            join provider in _db.AppointmentProviders.IgnoreQueryFilters()
                on slot.AppointmentProviderId equals provider.Id
            where slot.TenantId == tenantId && provider.TenantId == tenantId
                && !slot.IsDeleted && !provider.IsDeleted && provider.IsActive
                && slot.Status == AppointmentSlotStatus.Available
                && slot.Capacity == 1 && slot.BookedCount < slot.Capacity
                && slot.StartsAtUtc >= fromUtc && slot.StartsAtUtc < toUtc
            orderby slot.StartsAtUtc
            select new AvailableAppointmentSlot(slot.Id, provider.Id, provider.Name, slot.StartsAtUtc, slot.EndsAtUtc))
            .AsNoTracking().ToListAsync(ct);
    }

    public async Task<AppointmentBookingResult> BookAsync(
        Guid tenantId, BookAppointmentCommand command, CancellationToken ct = default)
    {
        var attempt = await TryBookAsync(tenantId, command, ct);
        return attempt.Outcome switch
        {
            BookingOutcome.Booked or BookingOutcome.AlreadyYours => new AppointmentBookingResult(
                attempt.BookingId!.Value, attempt.BookingReference!, attempt.StartsAtUtc!.Value,
                attempt.Status!, attempt.Outcome == BookingOutcome.AlreadyYours),
            BookingOutcome.InvalidDetails => throw new ArgumentException(attempt.Error),
            BookingOutcome.SlotTaken when attempt.Error == "Appointment slot was not found." =>
                throw new KeyNotFoundException(attempt.Error),
            _ => throw new InvalidOperationException(attempt.Error)
        };
    }

    public async Task<BookingAttempt> TryBookAsync(
        Guid tenantId, BookAppointmentCommand command, CancellationToken ct = default)
    {
        EnsureTenant(tenantId);
        ct.ThrowIfCancellationRequested();
        if (command is null || string.IsNullOrWhiteSpace(command.CustomerName) || command.SlotId == Guid.Empty)
            return Failure(BookingOutcome.InvalidDetails, "Please provide a name and a valid appointment slot.");

        AppointmentAvailabilitySlot? slot = null;
        AppointmentBooking? booking = null;
        string? key = null;
        try
        {
            // Existing tenant-scoped settings avoid adding another tenant schema field.
            var region = await _db.SystemSettings.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive
                    && x.Category == "Appointment" && x.Key == "DefaultPhoneRegion")
                .OrderBy(x => x.Id).Select(x => x.Value).FirstOrDefaultAsync(ct);
            var contact = NormaliseContact(command.CustomerContact,
                string.IsNullOrWhiteSpace(region) ? "BD" : region.Trim());
            if (!contact.Ok) return Failure(BookingOutcome.InvalidDetails, contact.Error!);
            key = BookingIdempotency.Derive(tenantId, command.SlotId, contact.Value!);
            var existing = await FindExistingAsync(tenantId, key, ct);
            if (existing is not null) return existing.Status == AppointmentBookingStatus.Confirmed.ToString()
                ? existing : Failure(BookingOutcome.InvalidDetails, "That previous booking is no longer active.");

            slot = await FindTenantSlotAsync(tenantId, command.SlotId, ct);
            if (slot is null) return Failure(BookingOutcome.SlotTaken, "Appointment slot was not found.");
            if (slot.Status != AppointmentSlotStatus.Available || slot.Capacity != 1 || slot.BookedCount >= slot.Capacity)
                return await ResolveConflictAsync(tenantId, key, ct);
            if (!await _db.AppointmentProviders.IgnoreQueryFilters().AnyAsync(x =>
                x.Id == slot.AppointmentProviderId && x.TenantId == tenantId && !x.IsDeleted && x.IsActive, ct))
                return Failure(BookingOutcome.ProviderUnavailable, "The appointment provider is not available.");

            booking = new AppointmentBooking
            {
                TenantId = tenantId, AppointmentProviderId = slot.AppointmentProviderId,
                AvailabilitySlotId = slot.Id, IdempotencyKey = key,
                BookingReference = $"APT-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..23].ToUpperInvariant(),
                CustomerName = command.CustomerName.Trim(), CustomerContact = contact.Value!,
                Purpose = string.IsNullOrWhiteSpace(command.Purpose) ? null : command.Purpose.Trim()
            };
            slot.BookedCount++;
            slot.Status = AppointmentSlotStatus.Booked;
            _db.AppointmentBookings.Add(booking);
            // One SaveChanges: SQL Server's implicit transaction makes both writes atomic.
            await _db.SaveChangesAsync(ct);
            return new(BookingOutcome.Booked, booking.Id, booking.BookingReference,
                slot.StartsAtUtc, booking.Status.ToString(), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DbUpdateConcurrencyException)
        {
            Detach(slot, booking);
            return await ResolveConflictAsync(tenantId, key!, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            Detach(slot, booking);
            return await ResolveConflictAsync(tenantId, key!, ct);
        }
        catch (Exception ex)
        {
            // Do not log command payloads or speak SQL/normalizer diagnostics.
            _logger.LogError(ex, "Appointment booking failed for tenant {TenantId}", tenantId);
            return Failure(BookingOutcome.SystemError, "I could not finish the booking. Please let a colleague help.");
        }
        finally { Detach(slot, booking); }
    }

    private async Task<BookingAttempt> ResolveConflictAsync(Guid tenantId, string key, CancellationToken ct)
    {
        try
        {
            var existing = await FindExistingAsync(tenantId, key, ct);
            if (existing is not null)
                return existing.Status == AppointmentBookingStatus.Confirmed.ToString() ? existing
                    : Failure(BookingOutcome.InvalidDetails, "That previous booking is no longer active.");
            return Failure(BookingOutcome.SlotTaken, "That appointment slot was just booked. Please choose another time.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not resolve appointment conflict for tenant {TenantId}", tenantId);
            return Failure(BookingOutcome.SystemError, "I could not check the booking. Please let a colleague help.");
        }
    }

    private async Task<BookingAttempt?> FindExistingAsync(Guid tenantId, string key, CancellationToken ct)
    {
        return await (
            from booking in _db.AppointmentBookings.IgnoreQueryFilters()
            join slot in _db.AppointmentAvailabilitySlots.IgnoreQueryFilters()
                on booking.AvailabilitySlotId equals slot.Id
            where booking.TenantId == tenantId && slot.TenantId == tenantId
                && !booking.IsDeleted && !slot.IsDeleted && booking.IdempotencyKey == key
            select new BookingAttempt(BookingOutcome.AlreadyYours, booking.Id, booking.BookingReference,
                slot.StartsAtUtc, booking.Status.ToString(), null)).AsNoTracking().SingleOrDefaultAsync(ct);
    }

    private void Detach(AppointmentAvailabilitySlot? slot, AppointmentBooking? booking)
    {
        if (booking is not null) _db.Entry(booking).State = EntityState.Detached;
        if (slot is not null) _db.Entry(slot).State = EntityState.Detached;
    }

    private static BookingAttempt Failure(BookingOutcome outcome, string error) =>
        new(outcome, null, null, null, null, error);

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

    internal static (bool Ok, string? Value, string? Error) NormaliseContact(string? raw, string defaultRegion)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (false, null, "Please provide a phone number or email address.");
        var contact = raw.Trim();
        if (contact.Contains('@') && MailAddress.TryCreate(contact, out var email)
            && string.Equals(email.Address, contact, StringComparison.OrdinalIgnoreCase))
            return (true, email.Address.ToLowerInvariant(), null);
        var phone = PhoneNumber.TryNormalize(contact, defaultRegion);
        return phone.Succeeded ? (true, phone.E164, null)
            : (false, null, "Please repeat your full phone number, including the country code.");
    }
}

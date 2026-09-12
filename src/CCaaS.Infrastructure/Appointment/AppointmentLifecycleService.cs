using CCaaS.Application.Appointment;
using CCaaS.Domain.Appointment;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CCaaS.Infrastructure.Appointment;

internal sealed partial class AppointmentService
{
    public async Task<IReadOnlyList<AppointmentVoucherData>> FindUpcomingAsync(
        Guid tenantId, string contact, DateTime fromUtc, CancellationToken ct = default)
    {
        EnsureTenant(tenantId);
        if (fromUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Supply a UTC cutoff.", nameof(fromUtc));
        var region = await _db.SystemSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive
                && x.Category == "Appointment" && x.Key == "DefaultPhoneRegion")
            .OrderBy(x => x.Id).Select(x => x.Value).FirstOrDefaultAsync(ct);
        var normalized = NormaliseContact(contact, string.IsNullOrWhiteSpace(region) ? "BD" : region.Trim());
        if (!normalized.Ok) throw new ArgumentException(normalized.Error, nameof(contact));
        return await (from b in _db.AppointmentBookings.IgnoreQueryFilters()
            join s in _db.AppointmentAvailabilitySlots.IgnoreQueryFilters() on b.AvailabilitySlotId equals s.Id
            join p in _db.AppointmentProviders.IgnoreQueryFilters() on b.AppointmentProviderId equals p.Id
            where b.TenantId == tenantId && s.TenantId == tenantId && p.TenantId == tenantId
                && !b.IsDeleted && !s.IsDeleted && !p.IsDeleted
                && b.Status == AppointmentBookingStatus.Confirmed
                && b.CustomerContact == normalized.Value && s.StartsAtUtc > fromUtc
            orderby s.StartsAtUtc
            select new AppointmentVoucherData(b.Id, b.BookingReference, b.CustomerName,
                b.CustomerContact, b.Purpose, p.Name, s.StartsAtUtc, s.EndsAtUtc,
                b.Status.ToString(), b.ConfirmedAtUtc)).AsNoTracking().ToListAsync(ct);
    }

    // Called only from a trusted tenant scope. A voice adapter must first resolve the
    // authenticated caller's contact and select the returned booking; a spoken ID is not authority.
    public Task<AppointmentChangeResult> CancelAsync(Guid tenantId, Guid bookingId,
        string reason, CancellationToken ct = default) => ChangeAsync(tenantId, bookingId, null, reason, ct);

    public Task<AppointmentChangeResult> RescheduleAsync(Guid tenantId, Guid bookingId,
        Guid newSlotId, CancellationToken ct = default) =>
        ChangeAsync(tenantId, bookingId, newSlotId, "Rescheduled by caller", ct);

    private async Task<AppointmentChangeResult> ChangeAsync(Guid tenantId, Guid bookingId,
        Guid? newSlotId, string reason, CancellationToken ct)
    {
        EnsureTenant(tenantId);
        ct.ThrowIfCancellationRequested();
        if (bookingId == Guid.Empty || newSlotId == Guid.Empty
            || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
            return new(AppointmentChangeOutcome.InvalidDetails);

        AppointmentBooking? original = null;
        AppointmentBooking? replacement = null;
        AppointmentAvailabilitySlot? oldSlot = null;
        AppointmentAvailabilitySlot? newSlot = null;
        try
        {
            original = await _db.AppointmentBookings.IgnoreQueryFilters().SingleOrDefaultAsync(
                b => b.TenantId == tenantId && b.Id == bookingId && !b.IsDeleted, ct);
            if (original is null) return new(AppointmentChangeOutcome.NotFound);

            if (newSlotId.HasValue)
            {
                var retried = await FindReplacementAsync(tenantId, bookingId, newSlotId.Value, ct);
                if (retried is not null) return retried;
            }
            else if (original.Status == AppointmentBookingStatus.Cancelled)
                return new(AppointmentChangeOutcome.AlreadyCancelled, original.Id);

            if (original.Status != AppointmentBookingStatus.Confirmed)
                return new(AppointmentChangeOutcome.NotChangeable);
            oldSlot = await FindTenantSlotAsync(tenantId, original.AvailabilitySlotId, ct);
            if (oldSlot is null || oldSlot.StartsAtUtc <= DateTime.UtcNow
                || oldSlot.Status != AppointmentSlotStatus.Booked || oldSlot.BookedCount != 1)
                return new(AppointmentChangeOutcome.NotChangeable);

            if (newSlotId.HasValue)
            {
                if (newSlotId == oldSlot.Id) return new(AppointmentChangeOutcome.InvalidDetails);
                newSlot = await FindTenantSlotAsync(tenantId, newSlotId.Value, ct);
                if (newSlot is null || newSlot.StartsAtUtc <= DateTime.UtcNow
                    || newSlot.Status != AppointmentSlotStatus.Available
                    || newSlot.Capacity != 1 || newSlot.BookedCount != 0)
                    return new(AppointmentChangeOutcome.SlotTaken);
                if (!await _db.AppointmentProviders.IgnoreQueryFilters().AnyAsync(p =>
                    p.TenantId == tenantId && p.Id == newSlot.AppointmentProviderId && p.IsActive && !p.IsDeleted, ct))
                    return new(AppointmentChangeOutcome.ProviderUnavailable);
                replacement = new AppointmentBooking
                {
                    TenantId = tenantId, AppointmentProviderId = newSlot.AppointmentProviderId,
                    AvailabilitySlotId = newSlot.Id, CustomerName = original.CustomerName,
                    CustomerContact = original.CustomerContact, Purpose = original.Purpose,
                    IdempotencyKey = BookingIdempotency.Derive(tenantId, newSlot.Id, original.CustomerContact),
                    RescheduledFromBookingId = original.Id,
                    BookingReference = $"APT-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..23].ToUpperInvariant()
                };
                _db.AppointmentBookings.Add(replacement);
                newSlot.Status = AppointmentSlotStatus.Booked;
                newSlot.BookedCount = 1;
            }

            original.Status = AppointmentBookingStatus.Cancelled;
            original.CancelledAtUtc = DateTime.UtcNow;
            original.CancellationReason = reason.Trim();
            // The historical booking keeps UNIQUE(TenantId, AvailabilitySlotId) occupied.
            // Never advertise that slot as reusable. Its rowversion also arbitrates two
            // simultaneous changes of the SAME original appointment to different targets.
            oldSlot.Status = AppointmentSlotStatus.Blocked;
            oldSlot.BookedCount = 0;
            await _db.SaveChangesAsync(ct); // One atomic write: replacement AND cancellation.
            return replacement is null
                ? new(AppointmentChangeOutcome.Cancelled, original.Id)
                : new(AppointmentChangeOutcome.Rescheduled, replacement.Id,
                    replacement.BookingReference, newSlot!.StartsAtUtc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException
            || ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            Detach(oldSlot, original);
            Detach(newSlot, replacement);
            return await ResolveChangeConflictAsync(tenantId, bookingId, newSlotId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Appointment change failed for tenant {TenantId}", tenantId);
            return new(AppointmentChangeOutcome.SystemError);
        }
        finally
        {
            Detach(oldSlot, original);
            Detach(newSlot, replacement);
        }
    }

    private async Task<AppointmentChangeResult?> FindReplacementAsync(Guid tenantId,
        Guid originalId, Guid targetSlotId, CancellationToken ct)
    {
        var replacement = await (from b in _db.AppointmentBookings.IgnoreQueryFilters()
            join s in _db.AppointmentAvailabilitySlots.IgnoreQueryFilters() on b.AvailabilitySlotId equals s.Id
            where b.TenantId == tenantId && s.TenantId == tenantId && !b.IsDeleted && !s.IsDeleted
                && b.RescheduledFromBookingId == originalId
            select new { b.Id, b.BookingReference, b.AvailabilitySlotId, b.Status, s.StartsAtUtc })
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (replacement is null) return null;
        return replacement.AvailabilitySlotId == targetSlotId && replacement.Status == AppointmentBookingStatus.Confirmed
            ? new(AppointmentChangeOutcome.AlreadyRescheduled, replacement.Id,
                replacement.BookingReference, replacement.StartsAtUtc)
            : new(AppointmentChangeOutcome.Conflict);
    }

    private async Task<AppointmentChangeResult> ResolveChangeConflictAsync(Guid tenantId,
        Guid originalId, Guid? newSlotId, CancellationToken ct)
    {
        try
        {
            if (newSlotId.HasValue)
                return await FindReplacementAsync(tenantId, originalId, newSlotId.Value, ct)
                    ?? new(AppointmentChangeOutcome.Conflict);
            var cancelled = await _db.AppointmentBookings.IgnoreQueryFilters().AsNoTracking().AnyAsync(b =>
                b.TenantId == tenantId && b.Id == originalId && !b.IsDeleted
                && b.Status == AppointmentBookingStatus.Cancelled, ct);
            return new(cancelled ? AppointmentChangeOutcome.AlreadyCancelled : AppointmentChangeOutcome.Conflict,
                cancelled ? originalId : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Appointment change outcome unavailable for tenant {TenantId}", tenantId);
            return new(AppointmentChangeOutcome.SystemError);
        }
    }
}

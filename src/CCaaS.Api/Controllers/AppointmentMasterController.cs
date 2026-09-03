using CCaaS.Domain.Appointment;
using CCaaS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CCaaS.Api.Controllers;

[Authorize]
[Route("api/appointment-master")]
public sealed class AppointmentMasterController : ApiControllerBase
{
    private readonly CcaasDbContext _db;
    public AppointmentMasterController(CcaasDbContext db) => _db = db;

    [HttpGet("providers")]
    public async Task<IActionResult> Providers(CancellationToken ct) => Ok(await _db.AppointmentProviders
        .OrderBy(x => x.Name).AsNoTracking().Select(x => new { x.Id, x.Name, x.TimeZoneId, x.IsActive }).ToListAsync(ct));

    [HttpPost("providers")]
    public async Task<IActionResult> CreateProvider([FromBody] ProviderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { message = "Provider name is required." });
        var provider = new AppointmentProvider { TenantId = TenantId, Name = request.Name.Trim(),
            TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? "UTC" : request.TimeZoneId.Trim(), IsActive = request.IsActive };
        _db.AppointmentProviders.Add(provider); await _db.SaveChangesAsync(ct); return Ok(provider);
    }

    [HttpPut("providers/{id:guid}")]
    public async Task<IActionResult> UpdateProvider(Guid id, [FromBody] ProviderRequest request, CancellationToken ct)
    {
        var item = await _db.AppointmentProviders.SingleOrDefaultAsync(x => x.Id == id, ct); if (item is null) return NotFound();
        item.Name = request.Name.Trim(); item.TimeZoneId = request.TimeZoneId.Trim(); item.IsActive = request.IsActive;
        item.UpdatedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct); return Ok(item);
    }

    [HttpGet("slots")]
    public async Task<IActionResult> Slots([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var start = (from ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = (to ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30))).AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return Ok(await (from slot in _db.AppointmentAvailabilitySlots join provider in _db.AppointmentProviders on slot.AppointmentProviderId equals provider.Id
            where slot.StartsAtUtc >= start && slot.StartsAtUtc < end orderby slot.StartsAtUtc
            select new { slot.Id, slot.AppointmentProviderId, providerName = provider.Name, slot.StartsAtUtc, slot.EndsAtUtc, status = slot.Status.ToString() })
            .AsNoTracking().ToListAsync(ct));
    }

    [HttpPost("slots")]
    public async Task<IActionResult> CreateSlot([FromBody] SlotRequest request, CancellationToken ct)
    {
        if (request.EndsAtUtc <= request.StartsAtUtc) return BadRequest(new { message = "End time must be after start time." });
        if (!await _db.AppointmentProviders.AnyAsync(x => x.Id == request.AppointmentProviderId && x.IsActive, ct)) return BadRequest(new { message = "Active provider not found." });
        var overlap = await _db.AppointmentAvailabilitySlots.AnyAsync(x => x.AppointmentProviderId == request.AppointmentProviderId
            && x.StartsAtUtc < request.EndsAtUtc && x.EndsAtUtc > request.StartsAtUtc, ct);
        if (overlap) return Conflict(new { message = "This slot overlaps an existing provider slot." });
        var slot = new AppointmentAvailabilitySlot { TenantId = TenantId, AppointmentProviderId = request.AppointmentProviderId,
            StartsAtUtc = DateTime.SpecifyKind(request.StartsAtUtc, DateTimeKind.Utc), EndsAtUtc = DateTime.SpecifyKind(request.EndsAtUtc, DateTimeKind.Utc) };
        _db.AppointmentAvailabilitySlots.Add(slot); await _db.SaveChangesAsync(ct); return Ok(slot);
    }

    [HttpPut("slots/{id:guid}/status")]
    public async Task<IActionResult> SetSlotStatus(Guid id, [FromBody] SlotStatusRequest request, CancellationToken ct)
    {
        var slot = await _db.AppointmentAvailabilitySlots.SingleOrDefaultAsync(x => x.Id == id, ct); if (slot is null) return NotFound();
        if (!Enum.TryParse<AppointmentSlotStatus>(request.Status, true, out var status)) return BadRequest(new { message = "Invalid slot status." });
        if (slot.Status == AppointmentSlotStatus.Booked && status != AppointmentSlotStatus.Booked) return Conflict(new { message = "A booked slot cannot be changed here." });
        slot.Status = status; slot.UpdatedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct); return Ok(slot);
    }

    [HttpGet("bookings")]
    public async Task<IActionResult> Bookings(CancellationToken ct) => Ok(await (from b in _db.AppointmentBookings
        join p in _db.AppointmentProviders on b.AppointmentProviderId equals p.Id
        join s in _db.AppointmentAvailabilitySlots on b.AvailabilitySlotId equals s.Id
        orderby b.ConfirmedAtUtc descending select new { b.Id, b.BookingReference, b.CustomerName, b.CustomerContact,
            b.Purpose, providerName = p.Name, s.StartsAtUtc, s.EndsAtUtc, status = b.Status.ToString(), b.ConfirmedAtUtc }).AsNoTracking().Take(250).ToListAsync(ct));

    public sealed record ProviderRequest(string Name, string TimeZoneId, bool IsActive);
    public sealed record SlotRequest(Guid AppointmentProviderId, DateTime StartsAtUtc, DateTime EndsAtUtc);
    public sealed record SlotStatusRequest(string Status);
}

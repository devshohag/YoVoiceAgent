using CCaaS.Application.Appointment;
using CCaaS.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

[Authorize]
[Route("api/appointments")]
public sealed class AppointmentsController : ApiControllerBase
{
    private readonly IAppointmentService _service;
    public AppointmentsController(IAppointmentService service) => _service = service;

    [HttpGet("availability")]
    public async Task<IActionResult> GetAvailability([FromQuery] DateOnly date, CancellationToken ct)
        => Ok(await _service.GetAvailabilityAsync(TenantId, date, ct));

    [HttpGet("bookings")]
    public async Task<IActionResult> GetBookings(CancellationToken ct)
        => Ok(await _service.GetBookingsAsync(TenantId, ct));

    [HttpGet("{bookingId:guid}/voucher")]
    public async Task<IActionResult> DownloadVoucher(Guid bookingId, CancellationToken ct)
    {
        var voucher = await _service.GetVoucherAsync(TenantId, bookingId, ct);
        if (voucher is null) return NotFound();

        var pdf = AppointmentVoucherPdf.Create(voucher);
        var safeReference = string.Concat(voucher.BookingReference
            .Where(character => char.IsLetterOrDigit(character) || character == '-'));
        return File(pdf, "application/pdf", $"Appointment-{safeReference}.pdf");
    }
}

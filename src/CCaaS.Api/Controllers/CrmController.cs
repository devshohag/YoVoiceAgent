using CCaaS.Application.Crm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

[Authorize]
[Route("api/crm/customers")]
public class CrmController : ApiControllerBase
{
    private readonly ICrmService _crmService;

    public CrmController(ICrmService crmService) => _crmService = crmService;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCustomerRequest request, CancellationToken ct)
        => Ok(await _crmService.CreateCustomerAsync(TenantId, request, ct));

    [HttpGet("by-phone/{phone}")]
    public async Task<IActionResult> FindByPhone(string phone, CancellationToken ct)
    {
        var customer = await _crmService.FindByPhoneAsync(TenantId, phone, ct);
        return customer is null ? NotFound() : Ok(customer);
    }

    [HttpGet("{customerId:guid}/360")]
    public async Task<IActionResult> GetCustomer360(Guid customerId, CancellationToken ct)
    {
        var dto = await _crmService.GetCustomer360Async(TenantId, customerId, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPost("notes")]
    public async Task<IActionResult> AddNote([FromBody] AddNoteRequest request, CancellationToken ct)
    {
        await _crmService.AddNoteAsync(TenantId, request, ct);
        return NoContent();
    }
}

using CCaaS.Application.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

[Authorize]
[Route("api/ai-agents")]
public sealed class AiAgentsController : ApiControllerBase
{
    private readonly IAiAgentService _service;

    public AiAgentsController(IAiAgentService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> GetAgents(CancellationToken ct)
        => Ok(await _service.GetAgentsAsync(ct));

    [HttpPost]
    public async Task<IActionResult> CreateAgent([FromBody] CreateAiAgentRequest request, CancellationToken ct)
    {
        var agent = await _service.CreateAgentAsync(TenantId, request, ct);
        return CreatedAtAction(nameof(GetAgents), new { id = agent.Id }, agent);
    }

    [HttpPost("process-recording")]
    public async Task<IActionResult> ProcessRecording([FromBody] ProcessRecordingRequest request, CancellationToken ct)
        => Ok(await _service.ProcessRecordingAsync(TenantId, request, ct));
}

[Authorize]
[Route("api/ai-conversations")]
public sealed class AiConversationsController : ApiControllerBase
{
    private readonly IAiAgentService _service;

    public AiConversationsController(IAiAgentService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> GetConversations(CancellationToken ct)
        => Ok(await _service.GetConversationsAsync(ct));

    [HttpPost("test")]
    public async Task<IActionResult> StartTestConversation([FromBody] StartAiTestConversationRequest request, CancellationToken ct)
        => Ok(await _service.StartTestConversationAsync(TenantId, request.AiAgentId, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetConversation(Guid id, CancellationToken ct)
    {
        var conversation = await _service.GetConversationAsync(id, ct);
        return conversation is null ? NotFound() : Ok(conversation);
    }

    [HttpPost("{id:guid}/turns")]
    public async Task<IActionResult> AddTurn(Guid id, [FromBody] AddAiConversationTurnRequest request, CancellationToken ct)
        => Ok(await _service.AddTurnAsync(TenantId, id, request, ct));

    [HttpPost("{id:guid}/tools")]
    public async Task<IActionResult> ExecuteTool(Guid id, [FromBody] ExecuteAiToolRequest request, CancellationToken ct)
        => Ok(await _service.ExecuteToolAsync(TenantId, id, request, ct));

    [HttpPost("{id:guid}/handoff")]
    public async Task<IActionResult> RequestHandoff(Guid id, [FromQuery] string reason, CancellationToken ct)
        => Ok(await _service.RequestHandoffAsync(TenantId, id, reason, ct));
}

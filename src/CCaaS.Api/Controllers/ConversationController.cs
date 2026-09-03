using CCaaS.Application.Conversation;
using CCaaS.Domain.Conversation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

[Authorize]
[Route("api/conversations")]
public class ConversationController : ApiControllerBase
{
    private readonly IConversationService _conversationService;

    public ConversationController(IConversationService conversationService) => _conversationService = conversationService;

    [HttpPost("get-or-create")]
    public async Task<IActionResult> GetOrCreate([FromQuery] Guid customerId, [FromQuery] ConversationChannelType channelType, CancellationToken ct)
        => Ok(await _conversationService.GetOrCreateConversationAsync(TenantId, customerId, channelType, ct));

    [HttpPost("messages")]
    public async Task<IActionResult> PostMessage([FromBody] PostMessageRequest request, CancellationToken ct)
        => Ok(await _conversationService.PostMessageAsync(TenantId, request, ct));

    [HttpPut("{conversationId:guid}/assign")]
    public async Task<IActionResult> Assign(Guid conversationId, [FromQuery] Guid agentId, [FromQuery] string reason, CancellationToken ct)
    {
        await _conversationService.AssignAsync(TenantId, conversationId, agentId, reason, ct);
        return NoContent();
    }

    /// <summary>The Agent Workspace "unified inbox across voice and digital channels" (Section 11).</summary>
    [HttpGet("inbox")]
    public async Task<IActionResult> GetUnifiedInbox([FromQuery] Guid agentId, CancellationToken ct)
        => Ok(await _conversationService.GetUnifiedInboxAsync(TenantId, agentId, ct));
}

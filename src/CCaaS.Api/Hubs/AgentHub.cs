using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CCaaS.Api.Hubs;

/// <summary>
/// Section 5 - "SignalR carries real-time UI state, not voice media." Used for: incoming
/// call/message popups, agent presence broadcasts, live queue/channel dashboards for the
/// Supervisor Center (Section 11). Voice media itself flows browser &lt;-&gt; Asterisk directly
/// over WebRTC/SIP.js - this hub never touches RTP.
/// </summary>
[Authorize]
public class AgentHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.User?.FindFirst("tenant_id")?.Value;
        if (tenantId is not null)
            await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenantId}");

        await base.OnConnectedAsync();
    }

    // TODO: server -> client push methods are typically called from Application/Infrastructure
    // event handlers (e.g. when a CallEvent or Message is created) via IHubContext<AgentHub>,
    // not from client-invoked methods here. Add IHubContext-based notifications in
    // ConversationService/CallService once you're ready to wire up the live UI.
}

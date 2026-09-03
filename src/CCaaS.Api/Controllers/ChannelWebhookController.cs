using CCaaS.Application.Channel;
using CCaaS.Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

/// <summary>
/// Section 9 - Inbound message flow: "Provider Webhook -> Signature Verification ->
/// Idempotency Store -> Normalize Provider Payload -> MessageReceived Event ->
/// Conversation Match/Create -> Customer Match -> Routing/Assignment -> Persist -> SignalR
/// -> Unified Agent Inbox." This controller only does the first two steps (receive +
/// verify); everything after that belongs in a queued handler so a slow webhook consumer
/// on Meta's side never blocks HTTP response time (Section 14 - Rate Limiting / Section 19
/// - Availability: "no single application-process failure should corrupt conversation state").
/// Webhook signature verification means this endpoint deliberately does NOT require a JWT -
/// Meta/SMS/Email providers call it directly, authenticated instead by their own signature.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/webhooks/channels")]
public class ChannelWebhookController : ControllerBase
{
    private readonly IChannelProviderFactory _providerFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<ChannelWebhookController> _logger;

    public ChannelWebhookController(IChannelProviderFactory providerFactory, IEventBus eventBus, ILogger<ChannelWebhookController> logger)
    {
        _providerFactory = providerFactory;
        _eventBus = eventBus;
        _logger = logger;
    }

    [HttpGet("{channelKey}")]
    public IActionResult VerifySubscription(string channelKey, [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        // Meta webhook subscription handshake (GET with hub.challenge) - Section 27 reference:
        // Meta WhatsApp Webhooks / Messenger Webhooks / Instagram Messaging Webhooks.
        return challenge is null ? Ok() : Content(challenge, "text/plain");
    }

    [HttpPost("{channelKey}")]
    public async Task<IActionResult> Receive(string channelKey, [FromHeader(Name = "X-Hub-Signature-256")] string? signature, CancellationToken ct)
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        var provider = _providerFactory.Resolve(channelKey);

        // TODO: fetch the tenant-specific channel secret via ChannelAccount/ChannelCredentialRef
        // before verifying - using a single shared secret here is a placeholder only.
        var configuredSecret = HttpContext.RequestServices
            .GetRequiredService<IConfiguration>()[$"Channels:{channelKey}:WebhookSecret"] ?? string.Empty;

        if (!string.IsNullOrEmpty(configuredSecret) &&
            (signature is null || !provider.VerifyWebhookSignature(rawBody, signature, configuredSecret)))
        {
            _logger.LogWarning("Rejected {ChannelKey} webhook: signature verification failed.", channelKey);
            return Unauthorized();
        }

        // Hand off to CCaaS.Workers.Channel for the rest of the pipeline (Conversation
        // Match/Create -> Customer Match -> Routing/Assignment -> Persist -> SignalR). This
        // endpoint has no tenant JWT (the provider calls it directly), so the consumer must
        // resolve the tenant from the payload itself (e.g. WABA phone-number-id ->
        // ChannelAccount.ExternalAccountId lookup) - see InboundChannelMessageConsumer.
        await _eventBus.PublishAsync("InboundChannelMessageReceived", new { channelKey, rawBody }, ct);
        _logger.LogInformation("Received {ChannelKey} webhook ({Length} bytes)", channelKey, rawBody.Length);
        return Ok();
    }
}

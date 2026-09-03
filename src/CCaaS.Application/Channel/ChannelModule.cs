namespace CCaaS.Application.Channel;

// Section 9 - "Provider adapter pattern": IChannelProvider - common send/read/status contract.
// Concrete implementations (WhatsAppChannelProvider, MessengerChannelProvider, ...) belong in
// Infrastructure (they need HttpClient + the provider's SDK/REST calls); Application only
// depends on this abstraction so business logic never talks to Meta/SMS/Email APIs directly.

public record OutboundChannelMessage(string ToAddress, string Body, string? TemplateName);
public record ChannelSendResult(bool Succeeded, string? ProviderMessageId, string? Error);

public interface IChannelProvider
{
    string ChannelKey { get; } // "whatsapp" | "messenger" | "instagram" | "email" | "sms" | "webchat"
    Task<ChannelSendResult> SendAsync(Guid tenantId, Guid channelAccountId, OutboundChannelMessage message, CancellationToken ct = default);

    /// <summary>Verify the provider's webhook signature before any payload is trusted (Section 14: Webhook Security).</summary>
    bool VerifyWebhookSignature(string rawBody, string signatureHeader, string channelSecret);
}

/// <summary>
/// Resolves the right IChannelProvider by key. Register each concrete provider in DI and
/// this factory picks the correct one for a given inbound webhook route or outbound send.
/// </summary>
public interface IChannelProviderFactory
{
    IChannelProvider Resolve(string channelKey);
}

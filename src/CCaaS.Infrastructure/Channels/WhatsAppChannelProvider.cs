using System.Security.Cryptography;
using System.Text;
using CCaaS.Application.Channel;

namespace CCaaS.Infrastructure.Channels;

// Section 9 - "WhatsAppChannelProvider - Meta WhatsApp Cloud API/webhook adapter."
// Section 27 references: Meta WhatsApp Cloud API - Get Started / Webhooks.
public class WhatsAppChannelProvider : IChannelProvider
{
    private readonly IHttpClientFactory _httpClientFactory;

    public string ChannelKey => "whatsapp";

    public WhatsAppChannelProvider(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<ChannelSendResult> SendAsync(Guid tenantId, Guid channelAccountId, OutboundChannelMessage message, CancellationToken ct = default)
    {
        // TODO: look up the tenant's WABA phone number id + access token via
        // ChannelAccount/ChannelCredentialRef (Section 14 - never store the raw token in
        // source or in a plain column, only a secret-store reference), then POST to
        // https://graph.facebook.com/v20.0/{phone-number-id}/messages
        var client = _httpClientFactory.CreateClient("whatsapp");
        await Task.CompletedTask; // placeholder for the real HTTP call
        return new ChannelSendResult(true, ProviderMessageId: Guid.NewGuid().ToString(), Error: null);
    }

    public bool VerifyWebhookSignature(string rawBody, string signatureHeader, string channelSecret)
    {
        // Meta signs webhooks with "sha256=<hex-hmac>" in the X-Hub-Signature-256 header.
        var expected = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signatureHeader));
    }
}

public class ChannelProviderFactory : IChannelProviderFactory
{
    private readonly IEnumerable<IChannelProvider> _providers;

    public ChannelProviderFactory(IEnumerable<IChannelProvider> providers) => _providers = providers;

    public IChannelProvider Resolve(string channelKey)
        => _providers.FirstOrDefault(p => p.ChannelKey == channelKey)
           ?? throw new NotSupportedException($"No channel provider registered for '{channelKey}'.");
}

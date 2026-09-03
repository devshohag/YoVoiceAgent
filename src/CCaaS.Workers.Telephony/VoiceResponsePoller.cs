using System.Collections.Concurrent;
using System.Text.Json;
using CCaaS.Application.Calls;
using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCaaS.Workers.Telephony;

/// <summary>Development adapter from persisted browser responses back into the live ARI call.</summary>
public sealed class VoiceResponsePoller : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AriEventListener _listener;
    private readonly ILogger<VoiceResponsePoller> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _handled = new();

    public VoiceResponsePoller(IServiceScopeFactory scopeFactory, AriEventListener listener,
        ILogger<VoiceResponsePoller> logger)
    {
        _scopeFactory = scopeFactory;
        _listener = listener;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { _logger.LogWarning(ex, "Voice response polling failed; retrying."); }
            await Task.Delay(500, stoppingToken);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CcaasDbContext>();
        var events = await db.CallEvents.IgnoreQueryFilters()
            .Where(x => !x.IsDeleted && (x.EventType == "VoiceResponseSubmitted"
                || x.EventType == "VoiceBridgeHangupRequested"))
            .OrderByDescending(x => x.OccurredAt)
            .Take(50)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var item in events.Where(x => !_handled.ContainsKey(x.Id)))
        {
            var marker = item.Id.ToString("N");
            var alreadyProcessed = await db.CallEvents.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.TenantId == item.TenantId && x.CallSessionId == item.CallSessionId
                    && !x.IsDeleted && x.EventType == "TelephonyCommandProcessed"
                    && x.PayloadJson != null && x.PayloadJson.Contains(marker), ct);
            if (alreadyProcessed) { _handled[item.Id] = 0; continue; }

            var processed = false;
            if (item.EventType == "VoiceBridgeHangupRequested")
            {
                processed = await _listener.HangupCallAsync(item.CallSessionId, ct);
            }
            else if (!string.IsNullOrWhiteSpace(item.PayloadJson))
            {
                using var json = JsonDocument.Parse(item.PayloadJson);
                var root = json.RootElement;
                var objectKey = root.GetProperty("objectKey").GetString();
                var responseName = root.GetProperty("responseName").GetString();
                var endCall = root.TryGetProperty("endCall", out var endCallElement) && endCallElement.ValueKind == JsonValueKind.True;
                var handoff = root.TryGetProperty("handoffAfterPlayback", out var handoffElement) && handoffElement.ValueKind == JsonValueKind.True;
                var extension = root.TryGetProperty("handoffExtension", out var extensionElement) ? extensionElement.GetString() : null;
                var reason = root.TryGetProperty("handoffReason", out var reasonElement) ? reasonElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(objectKey) && !string.IsNullOrWhiteSpace(responseName))
                    processed = await _listener.PlayVoiceResponseAsync(item.CallSessionId, objectKey, responseName,
                        endCall, handoff, extension, reason, ct);
            }

            if (!processed) continue;
            await scope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
                item.TenantId, item.CallSessionId, "TelephonyCommandProcessed",
                JsonSerializer.Serialize(new { sourceEventId = marker, item.EventType }), ct);
            _handled[item.Id] = 0;
        }
    }
}

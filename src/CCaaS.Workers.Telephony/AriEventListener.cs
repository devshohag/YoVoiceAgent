using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CCaaS.Application.Calls;
using CCaaS.Application.Crm;
using CCaaS.Infrastructure.ObjectStorage;
using CCaaS.Infrastructure.Persistence;
using CCaaS.Domain.Organization;
using Microsoft.EntityFrameworkCore;

namespace CCaaS.Workers.Telephony;

/// <summary>
/// Owns the development inbound lifecycle: Stasis entry, answer, welcome playback,
/// caller recording, MinIO upload, normalized SQL events, and clean call completion.
/// </summary>
public sealed class AriEventListener : BackgroundService
{
    private sealed class CallContext
    {
        public required Guid TenantId { get; init; }
        public required Guid CallSessionId { get; init; }
        public required string ChannelId { get; init; }
        public required string ExternalCallId { get; init; }
        public int Turn { get; set; } = 1;
        public bool HandoffInProgress { get; set; }
        public bool FinalRecordingPersisted { get; set; }
        public string? CurrentHandoffExtension { get; set; }
        public Guid? CurrentAgentId { get; set; }
        public HashSet<string> AttemptedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string RecordingName => $"ccaas-{CallSessionId:N}-customer-{Turn:000}";
        public string FinalRecordingName => $"ccaas-full-{ExternalCallId}";
    }
    private enum PlaybackPurpose { Welcome, AiResponse }
    private sealed record PlaybackContext(CallContext Call, PlaybackPurpose Purpose,
        bool EndCallAfterPlayback = false, bool HandoffAfterPlayback = false,
        string? HandoffExtension = null, string? HandoffReason = null);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly AriClient _ari;
    private readonly ILogger<AriEventListener> _logger;
    private readonly ConcurrentDictionary<string, CallContext> _callsByChannel = new();
    private readonly ConcurrentDictionary<string, CallContext> _callsByRecording = new();
    private readonly ConcurrentDictionary<string, PlaybackContext> _playbacks = new();

    public AriEventListener(IServiceScopeFactory scopeFactory, IConfiguration configuration,
        AriClient ari, ILogger<AriEventListener> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _ari = ari;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consecutiveDisconnects = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var connectionStartedAt = DateTime.UtcNow;
            try
            {
                await ConnectAndListenAsync(stoppingToken);
                consecutiveDisconnects = DateTime.UtcNow - connectionStartedAt > TimeSpan.FromMinutes(1)
                    ? 1 : consecutiveDisconnects + 1;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                consecutiveDisconnects = DateTime.UtcNow - connectionStartedAt > TimeSpan.FromMinutes(1)
                    ? 1 : consecutiveDisconnects + 1;
                _logger.LogWarning(ex, "ARI event socket dropped.");
            }
            await UpdateAsteriskHealthAsync(false, stoppingToken);
            var seconds = Math.Min(30, Math.Pow(2, Math.Min(consecutiveDisconnects, 5) - 1));
            _logger.LogWarning("Reconnecting to ARI in {DelaySeconds} seconds (disconnect {Count}).",
                seconds, consecutiveDisconnects);
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        var appName = _configuration["Telephony:StasisAppName"] ?? "ccaas";
        var username = _configuration["Telephony:AriUsername"] ?? "ccaas";
        var password = _configuration["Telephony:AriPassword"] ?? "ccaas_dev_password";
        var httpBase = _configuration["Telephony:AriBaseUrl"] ?? "http://asterisk:8088/ari";
        var socketBase = httpBase.Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);
        var uri = new Uri($"{socketBase}/events?app={Uri.EscapeDataString(appName)}" +
                          $"&api_key={Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}");

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(uri, ct);
        await UpdateAsteriskHealthAsync(true, ct);
        await RecoverActiveCallsAsync(ct);
        _logger.LogInformation("Connected to Asterisk ARI application {Application} at {BaseUrl}.",
            appName, httpBase);

        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
                await HandleEventAsync(Encoding.UTF8.GetString(message.ToArray()), ct);
        }
    }

    private async Task RecoverActiveCallsAsync(CancellationToken ct)
    {
        var channelIds = await _ari.ListChannelIdsAsync(ct);
        if (channelIds.Count == 0) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CcaasDbContext>();
        var sessions = await db.CallSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(x => !x.IsDeleted && x.EndedAt == null && x.ExternalCallId != null
                        && channelIds.Contains(x.ExternalCallId)).ToListAsync(ct);
        var agentIds = sessions.Where(x => x.AgentId != null).Select(x => x.AgentId!.Value).Distinct().ToList();
        var extensions = await db.Agents.IgnoreQueryFilters().AsNoTracking()
            .Where(x => agentIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ExtensionNumber, ct);
        foreach (var session in sessions)
        {
            if (_callsByChannel.ContainsKey(session.ExternalCallId!)) continue;
            var context = new CallContext { TenantId = session.TenantId, CallSessionId = session.Id,
                ChannelId = session.ExternalCallId!, ExternalCallId = session.ExternalCallId!,
                HandoffInProgress = session.Status == CCaaS.Domain.Calls.CallStatus.Transferring,
                CurrentAgentId = session.AgentId,
                CurrentHandoffExtension = session.AgentId is not null
                    && extensions.TryGetValue(session.AgentId.Value, out var extension) ? extension : null };
            if (context.CurrentHandoffExtension is not null)
                context.AttemptedExtensions.Add(context.CurrentHandoffExtension);
            _callsByChannel[context.ChannelId] = context;
        }
        if (sessions.Count > 0)
            _logger.LogInformation("Recovered {Count} active call contexts after ARI reconnect/startup.", sessions.Count);
    }

    private async Task UpdateAsteriskHealthAsync(bool healthy, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CcaasDbContext>();
            var nodes = await db.AsteriskNodes.IgnoreQueryFilters().Where(x => !x.IsDeleted).ToListAsync(ct);
            foreach (var node in nodes)
            { node.IsHealthy = healthy; node.LastHeartbeatAt = DateTime.UtcNow; }
            if (nodes.Count > 0) await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { _logger.LogDebug(ex, "Could not persist Asterisk health state."); }
    }

    private async Task HandleEventAsync(string json, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = ReadString(root, "type");
        if (type is null) return;

        try
        {
            switch (type)
            {
                case "StasisStart": await HandleStasisStartAsync(root, json, ct); break;
                case "PlaybackFinished": await HandlePlaybackFinishedAsync(root, ct); break;
                case "RecordingFinished": await HandleRecordingFinishedAsync(root, json, ct); break;
                case "ChannelHangupRequest": await RecordChannelEventAsync(root, type, json, ct); break;
                case "StasisEnd":
                case "ChannelDestroyed": await HandleCallEndedAsync(root, type, json, ct); break;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to process ARI event {EventType}.", type);
        }
    }

    private async Task HandleStasisStartAsync(JsonElement root, string json, CancellationToken ct)
    {
        if (!root.TryGetProperty("channel", out var channel)) return;
        var channelId = ReadString(channel, "id");
        if (string.IsNullOrWhiteSpace(channelId)) return;

        if (_callsByChannel.TryGetValue(channelId, out var returningContext))
        {
            var returnedFromHandoff = root.TryGetProperty("args", out var args)
                && args.ValueKind == JsonValueKind.Array
                && args.EnumerateArray().Any(x => x.GetString() == "handoff-complete");
            if (returnedFromHandoff && returningContext.HandoffInProgress)
            {
                var argValues = args.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                var rawDialStatus = argValues.ElementAtOrDefault(1);
                var hangupCause = argValues.ElementAtOrDefault(2);
                var outcome = CallLifecycleClassifier.NormalizeDialStatus(rawDialStatus);
                returningContext.HandoffInProgress = false;
                if (CallLifecycleClassifier.WasAnswered(rawDialStatus))
                {
                    using var returnScope = _scopeFactory.CreateScope();
                    await returnScope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
                        returningContext.TenantId, returningContext.CallSessionId,
                        "HumanHandoffCompleted", JsonSerializer.Serialize(new
                        {
                            channelId, extension = returningContext.CurrentHandoffExtension,
                            dialStatus = rawDialStatus, outcome, hangupCause,
                            hangupInitiator = "caller-or-agent"
                        }), ct);
                    await SetAgentPresenceAsync(returningContext, AgentPresence.WrapUp, ct);
                    await PersistFinalRecordingAsync(returningContext, ct);
                    await _ari.HangupAsync(channelId, ct);
                }
                else
                {
                    using var returnScope = _scopeFactory.CreateScope();
                    await returnScope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
                        returningContext.TenantId, returningContext.CallSessionId,
                        "HumanHandoffAttemptFailed", JsonSerializer.Serialize(new
                        {
                            extension = returningContext.CurrentHandoffExtension,
                            dialStatus = rawDialStatus, outcome, hangupCause
                        }), ct);
                    await SetAgentPresenceAsync(returningContext, AgentPresence.Available, ct);
                    returningContext.CurrentAgentId = null;
                    var fallback = await ResolveNextHandoffExtensionAsync(returningContext, ct);
                    if (fallback is not null)
                        await BeginHandoffAsync(returningContext, fallback,
                            $"Automatic fallback after {outcome}.", ct);
                    else
                    {
                        await returnScope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
                            returningContext.TenantId, returningContext.CallSessionId,
                            "HumanHandoffFailed", JsonSerializer.Serialize(new { outcome, hangupCause,
                                attemptedExtensions = returningContext.AttemptedExtensions }), ct);
                        await _ari.HangupAsync(channelId, ct);
                    }
                }
            }
            return;
        }

        var from = ReadNestedString(channel, "caller", "number") ?? "anonymous";
        var to = ReadNestedString(channel, "dialplan", "exten") ?? "7000";
        var externalId = ReadString(channel, "id")!;

        using var scope = _scopeFactory.CreateScope();
        var tenantId = await scope.ServiceProvider.GetRequiredService<IInboundTenantResolver>()
            .ResolveAsync(to, ct);
        var calls = scope.ServiceProvider.GetRequiredService<ICallService>();
        Guid? customerId = null;
        if (!string.Equals(from, "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            var crm = scope.ServiceProvider.GetRequiredService<ICrmService>();
            var customer = await crm.FindByPhoneAsync(tenantId, from, ct)
                ?? await crm.CreateCustomerAsync(tenantId,
                    new CreateCustomerRequest($"Caller {from}", from, null), ct);
            customerId = customer.Id;
        }
        var session = await calls.OpenInboundCallAsync(tenantId, externalId, from, to, customerId, ct);
        var context = new CallContext { TenantId = tenantId, CallSessionId = session.Id,
            ChannelId = channelId, ExternalCallId = externalId };
        _callsByChannel[channelId] = context;
        _callsByRecording[context.RecordingName] = context;

        await calls.RecordEventAsync(tenantId, session.Id, "StasisStart", json, ct);
        await _ari.AnswerAsync(channelId, ct);
        await calls.RecordEventAsync(tenantId, session.Id, "Answered", null, ct);

        var welcome = _configuration["Telephony:WelcomeMedia"] ?? "sound:hello-world";
        try
        {
            var playbackId = await _ari.PlayAsync(channelId, welcome, ct);
            _playbacks[playbackId] = new PlaybackContext(context, PlaybackPurpose.Welcome);
            await calls.RecordEventAsync(tenantId, session.Id, "WelcomePlaybackStarted",
                JsonSerializer.Serialize(new { media = welcome, playbackId }), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Welcome sound could not be played; starting recording immediately.");
            await StartRecordingAsync(context, ct);
        }
    }

    private async Task HandlePlaybackFinishedAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("playback", out var playback)) return;
        var playbackId = ReadString(playback, "id");
        if (playbackId is null || !_playbacks.TryRemove(playbackId, out var context)) return;

        if (context.Purpose == PlaybackPurpose.Welcome)
            await StartRecordingAsync(context.Call, ct);
        else if (context.HandoffAfterPlayback)
        {
            var extension = string.IsNullOrWhiteSpace(context.HandoffExtension)
                ? _configuration["Telephony:FallbackHandoffExtension"] ?? "1002"
                : context.HandoffExtension;
            if (!extension.All(char.IsDigit))
                throw new InvalidOperationException("The resolved handoff extension is invalid.");
            await BeginHandoffAsync(context.Call, extension, context.HandoffReason, ct);
        }
        else if (context.EndCallAfterPlayback)
            await _ari.HangupAsync(context.Call.ChannelId, ct);
        else
        {
            context.Call.Turn++;
            _callsByRecording[context.Call.RecordingName] = context.Call;
            await StartRecordingAsync(context.Call, ct);
        }
    }

    private async Task StartRecordingAsync(CallContext context, CancellationToken ct)
    {
        var maxDuration = _configuration.GetValue("Telephony:RecordingMaxDurationSeconds", 30);
        var maxSilence = _configuration.GetValue("Telephony:RecordingMaxSilenceSeconds", 6);
        await _ari.StartRecordingAsync(context.ChannelId, context.RecordingName,
            maxDuration, maxSilence, ct);

        using var scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
            context.TenantId, context.CallSessionId, "RecordingStarted",
            JsonSerializer.Serialize(new { name = context.RecordingName, format = "wav" }), ct);
    }

    private async Task HandleRecordingFinishedAsync(JsonElement root, string json, CancellationToken ct)
    {
        if (!root.TryGetProperty("recording", out var recording)) return;
        var name = ReadString(recording, "name");
        if (name is null || !_callsByRecording.TryRemove(name, out var context)) return;
        var duration = ReadInt(recording, "duration");
        var recordingDirectory = _configuration["Telephony:RecordingDirectory"] ?? "/recordings";
        var localPath = Path.Combine(recordingDirectory, name + ".wav");

        using var scope = _scopeFactory.CreateScope();
        var calls = scope.ServiceProvider.GetRequiredService<ICallService>();
        try
        {
            var file = await WaitForFileAsync(localPath, ct);
            var bucket = _configuration["Telephony:RecordingBucket"] ?? "call-recordings";
            var objectKey = $"{context.TenantId:N}/calls/{context.CallSessionId:N}/{name}.wav";
            await UploadWithRetryAsync(scope.ServiceProvider.GetRequiredService<IObjectStorageService>(),
                bucket, objectKey, file.FullName, ct);
            var saved = await calls.AddRecordingAsync(context.TenantId, context.CallSessionId,
                objectKey, duration, file.Length, ct);
            await calls.RecordEventAsync(context.TenantId, context.CallSessionId,
                "RecordingReady", JsonSerializer.Serialize(new { recordingId = saved.Id, objectKey, ariEvent = json }), ct);
            await calls.RecordEventAsync(context.TenantId, context.CallSessionId,
                "AwaitingVoiceBridge", JsonSerializer.Serialize(new { recordingId = saved.Id }), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist recording {RecordingName}.", name);
            await calls.RecordEventAsync(context.TenantId, context.CallSessionId,
                "RecordingFailed", JsonSerializer.Serialize(new { name, error = ex.Message }), ct);
        }

        // Keep the channel in Stasis. The development browser bridge will submit a WAV reply;
        // production replaces that bridge with server-side STT/LLM/TTS providers.
    }

    public async Task<bool> PlayVoiceResponseAsync(Guid callSessionId, string objectKey,
        string responseName, bool endCallAfterPlayback, bool handoffAfterPlayback,
        string? handoffExtension, string? handoffReason, CancellationToken ct)
    {
        var context = _callsByChannel.Values.FirstOrDefault(x => x.CallSessionId == callSessionId);
        if (context is null) return false;
        var directory = _configuration["Telephony:RecordingDirectory"] ?? "/recordings";
        var path = Path.Combine(directory, responseName + ".wav");
        using var scope = _scopeFactory.CreateScope();
        await using (var source = await scope.ServiceProvider.GetRequiredService<IObjectStorageService>()
            .DownloadAsync(_configuration["Telephony:RecordingBucket"] ?? "call-recordings", objectKey, ct))
        await using (var target = File.Create(path))
            await source.CopyToAsync(target, ct);

        var playbackId = await _ari.PlayAsync(context.ChannelId, $"recording:{responseName}", ct);
        _playbacks[playbackId] = new PlaybackContext(context, PlaybackPurpose.AiResponse,
            endCallAfterPlayback, handoffAfterPlayback, handoffExtension, handoffReason);
        await scope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
            context.TenantId, context.CallSessionId, "VoiceResponsePlaybackStarted",
            JsonSerializer.Serialize(new { responseName, objectKey, playbackId }), ct);
        return true;
    }

    public async Task<bool> HangupCallAsync(Guid callSessionId, CancellationToken ct)
    {
        var context = _callsByChannel.Values.FirstOrDefault(x => x.CallSessionId == callSessionId);
        if (context is null) return false;
        await _ari.HangupAsync(context.ChannelId, ct);
        return true;
    }

    private async Task RecordChannelEventAsync(JsonElement root, string type, string json, CancellationToken ct)
    {
        var channelId = ReadNestedString(root, "channel", "id");
        if (channelId is null || !_callsByChannel.TryGetValue(channelId, out var context)) return;
        using var scope = _scopeFactory.CreateScope();
        var cause = ReadString(root, "cause_txt") ?? ReadString(root, "cause");
        await scope.ServiceProvider.GetRequiredService<ICallService>()
            .RecordEventAsync(context.TenantId, context.CallSessionId, type, json, ct);
        await scope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
            context.TenantId, context.CallSessionId, "HangupInitiated",
            JsonSerializer.Serialize(new { initiator = "caller", cause }), ct);
    }

    private async Task HandleCallEndedAsync(JsonElement root, string type, string json, CancellationToken ct)
    {
        var channelId = ReadNestedString(root, "channel", "id");
        if (channelId is null || !_callsByChannel.TryGetValue(channelId, out var context)) return;
        if (type == "StasisEnd" && context.HandoffInProgress)
        {
            using var handoffScope = _scopeFactory.CreateScope();
            await handoffScope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
                context.TenantId, context.CallSessionId, "HumanHandoffDialplanEntered", json, ct);
            return;
        }
        if (!_callsByChannel.TryRemove(channelId, out context)) return;
        _callsByRecording.TryRemove(context.RecordingName, out _);
        var cause = ReadString(root, "cause_txt") ?? ReadString(root, "cause") ?? type;
        using var scope = _scopeFactory.CreateScope();
        var calls = scope.ServiceProvider.GetRequiredService<ICallService>();
        await PersistFinalRecordingAsync(context, ct);
        await calls.RecordEventAsync(context.TenantId, context.CallSessionId, type, json, ct);
        await calls.EndCallAsync(context.TenantId, context.CallSessionId, cause, ct);
        if (context.CurrentAgentId is not null)
            await SetAgentPresenceAsync(context, AgentPresence.WrapUp, ct);
    }

    private async Task BeginHandoffAsync(CallContext context, string extension, string? reason, CancellationToken ct)
    {
        if (!extension.All(char.IsDigit)) throw new InvalidOperationException("The resolved handoff extension is invalid.");
        context.HandoffInProgress = true;
        context.CurrentHandoffExtension = extension;
        context.AttemptedExtensions.Add(extension);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CcaasDbContext>();
        var agent = await db.Agents.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == context.TenantId
            && !x.IsDeleted && x.ExtensionNumber == extension, ct);
        if (agent is not null)
        {
            agent.Presence = AgentPresence.OnCall;
            agent.PresenceChangedAt = DateTime.UtcNow;
            context.CurrentAgentId = agent.Id;
            var call = await db.CallSessions.IgnoreQueryFilters().SingleAsync(x => x.Id == context.CallSessionId
                && x.TenantId == context.TenantId, ct);
            call.AgentId = agent.Id;
            await db.SaveChangesAsync(ct);
        }
        await scope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
            context.TenantId, context.CallSessionId, "HumanHandoffStarted",
            JsonSerializer.Serialize(new { extension, reason, attempt = context.AttemptedExtensions.Count,
                agentId = context.CurrentAgentId }), ct);
        try { await _ari.ContinueInDialplanAsync(context.ChannelId, "human-handoff", extension, 1, ct); }
        catch
        {
            context.HandoffInProgress = false;
            await SetAgentPresenceAsync(context, AgentPresence.Available, ct);
            await scope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
                context.TenantId, context.CallSessionId, "HumanHandoffDispatchFailed",
                JsonSerializer.Serialize(new { extension, attempt = context.AttemptedExtensions.Count }), ct);
            throw;
        }
    }

    private async Task<string?> ResolveNextHandoffExtensionAsync(CallContext context, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CcaasDbContext>();
        var candidates = await (from member in db.QueueMembers.IgnoreQueryFilters().AsNoTracking()
                                join agent in db.Agents.IgnoreQueryFilters().AsNoTracking() on member.AgentId equals agent.Id
                                join extension in db.Extensions.IgnoreQueryFilters().AsNoTracking() on agent.Id equals extension.AgentId
                                where member.TenantId == context.TenantId && !member.IsDeleted && member.IsActive
                                      && agent.TenantId == context.TenantId && !agent.IsDeleted
                                      && agent.Presence == AgentPresence.Available
                                      && extension.TenantId == context.TenantId && !extension.IsDeleted
                                orderby member.Priority, member.Penalty, agent.PresenceChangedAt
                                select extension.ExtensionNumber).ToListAsync(ct);
        var next = candidates.FirstOrDefault(x => !context.AttemptedExtensions.Contains(x));
        if (next is not null) return next;
        var configured = _configuration["Telephony:FallbackHandoffExtension"];
        return !string.IsNullOrWhiteSpace(configured) && !context.AttemptedExtensions.Contains(configured)
            ? configured : null;
    }

    private async Task SetAgentPresenceAsync(CallContext context, AgentPresence presence, CancellationToken ct)
    {
        if (context.CurrentAgentId is null) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CcaasDbContext>();
        var agent = await db.Agents.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == context.CurrentAgentId
            && x.TenantId == context.TenantId && !x.IsDeleted, ct);
        if (agent is null) return;
        agent.Presence = presence;
        agent.PresenceChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task PersistFinalRecordingAsync(CallContext context, CancellationToken ct)
    {
        if (context.FinalRecordingPersisted) return;
        var directory = _configuration["Telephony:RecordingDirectory"] ?? "/recordings";
        var path = Path.Combine(directory, context.FinalRecordingName + ".wav");
        try
        {
            var file = await WaitForStableFileAsync(path, ct);
            using var scope = _scopeFactory.CreateScope();
            var bucket = _configuration["Telephony:RecordingBucket"] ?? "call-recordings";
            var objectKey = $"{context.TenantId:N}/calls/{context.CallSessionId:N}/final/{context.FinalRecordingName}.wav";
            await UploadWithRetryAsync(scope.ServiceProvider.GetRequiredService<IObjectStorageService>(),
                bucket, objectKey, file.FullName, ct);
            var calls = scope.ServiceProvider.GetRequiredService<ICallService>();
            var saved = await calls.AddRecordingAsync(context.TenantId, context.CallSessionId,
                objectKey, 0, file.Length, ct);
            await calls.RecordEventAsync(context.TenantId, context.CallSessionId, "FullCallRecordingReady",
                JsonSerializer.Serialize(new { recordingId = saved.Id, objectKey }), ct);
            context.FinalRecordingPersisted = true;
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("Full-call recording {RecordingName} was not produced.", context.FinalRecordingName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist full-call recording {RecordingName}.", context.FinalRecordingName);
        }
    }

    private static async Task<FileInfo> WaitForFileAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length > 0) return file;
            await Task.Delay(100, ct);
        }
        throw new FileNotFoundException("Asterisk recording file was not available.", path);
    }

    private static async Task UploadWithRetryAsync(IObjectStorageService storage, string bucket,
        string objectKey, string path, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await using var stream = File.OpenRead(path);
                await storage.UploadAsync(bucket, objectKey, stream, "audio/wav", ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 3)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1)), ct);
            }
        }
        throw lastError ?? new IOException("Object storage upload failed after retries.");
    }

    private static async Task<FileInfo> WaitForStableFileAsync(string path, CancellationToken ct)
    {
        long previousLength = -1;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length > 44 && file.Length == previousLength) return file;
            previousLength = file.Exists ? file.Length : -1;
            await Task.Delay(150, ct);
        }
        throw new FileNotFoundException("The finalized Asterisk recording was not available.", path);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.ToString() : null;

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static string? ReadNestedString(JsonElement element, string parent, string property) =>
        element.TryGetProperty(parent, out var child) ? ReadString(child, property) : null;

}

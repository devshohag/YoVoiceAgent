using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CCaaS.Application.Ai;
using CCaaS.Application.Calls;
using CCaaS.Domain.Ai;
using CCaaS.Domain.Organization;
using CCaaS.Infrastructure.ObjectStorage;
using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCaaS.Workers.Telephony;

/// <summary>
/// Server-side development voice pipeline. Each CallSession owns a separate conversation;
/// the global semaphore only limits machine load and never mixes call histories.
/// </summary>
public sealed partial class LocalAiVoiceProcessor : BackgroundService
{
    private const string Bucket = "call-recordings";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalAiVoiceProcessor> _logger;
    private readonly SemaphoreSlim _capacity;
    private readonly ConcurrentDictionary<Guid, byte> _inflight = new();
    private readonly ConcurrentDictionary<Guid, int> _failures = new();

    public LocalAiVoiceProcessor(IServiceScopeFactory scopeFactory, IHttpClientFactory clients,
        IConfiguration configuration, ILogger<LocalAiVoiceProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _clients = clients;
        _configuration = configuration;
        _logger = logger;
        var parallel = Math.Clamp(configuration.GetValue("AiVoice:MaxConcurrentCalls", 2), 1, 16);
        _capacity = new SemaphoreSlim(parallel, parallel);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { _logger.LogWarning(ex, "Local AI voice polling failed; it will retry."); }
            await Task.Delay(500, stoppingToken);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CcaasDbContext>();
        var candidates = await db.CallEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(x => !x.IsDeleted && x.EventType == "AwaitingVoiceBridge")
            .OrderByDescending(x => x.OccurredAt).Take(100).ToListAsync(ct);
        var tasks = new List<Task>();
        foreach (var source in candidates)
        {
            if (_inflight.ContainsKey(source.Id)) continue;
            if (!await IsLocalModeAsync(db, source.TenantId, ct)) continue;
            var marker = source.Id.ToString("N");
            var done = await db.CallEvents.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
                x.TenantId == source.TenantId && x.CallSessionId == source.CallSessionId && !x.IsDeleted
                && x.EventType == "LocalAiTurnProcessed" && x.PayloadJson != null
                && x.PayloadJson.Contains(marker), ct);
            if (done || !_inflight.TryAdd(source.Id, 0)) continue;
            tasks.Add(ProcessGuardedAsync(source.Id, ct));
        }
        if (tasks.Count > 0) await Task.WhenAll(tasks);
    }

    private async Task ProcessGuardedAsync(Guid sourceEventId, CancellationToken ct)
    {
        await _capacity.WaitAsync(ct);
        try
        {
            await ProcessTurnAsync(sourceEventId, ct);
            _failures.TryRemove(sourceEventId, out _);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var count = _failures.AddOrUpdate(sourceEventId, 1, (_, value) => value + 1);
            _logger.LogError(ex, "Local AI turn {SourceEventId} failed ({Attempt}/3).", sourceEventId, count);
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CcaasDbContext>();
            var source = await db.CallEvents.IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == sourceEventId, ct);
            if (source is not null)
            {
                var calls = scope.ServiceProvider.GetRequiredService<ICallService>();
                await calls.RecordEventAsync(source.TenantId, source.CallSessionId, "LocalAiTurnFailed",
                    JsonSerializer.Serialize(new { sourceEventId, attempt = count,
                        error = ex.Message[..Math.Min(ex.Message.Length, 500)] }), ct);
                if (count >= 3)
                {
                    await calls.RecordEventAsync(source.TenantId, source.CallSessionId,
                        "VoiceBridgeHangupRequested", JsonSerializer.Serialize(new
                        { reason = "Local AI voice processing failed three consecutive times." }), ct);
                    await calls.RecordEventAsync(source.TenantId, source.CallSessionId,
                        "LocalAiTurnProcessed", JsonSerializer.Serialize(new { sourceEventId, failed = true }), ct);
                }
            }
        }
        finally
        {
            _capacity.Release();
            _inflight.TryRemove(sourceEventId, out _);
        }
    }

    private async Task ProcessTurnAsync(Guid sourceEventId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CcaasDbContext>();
        var source = await db.CallEvents.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == sourceEventId, ct);
        var session = await db.CallSessions.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == source.CallSessionId && x.TenantId == source.TenantId, ct);
        if (session.EndedAt is not null)
        {
            await scope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
                source.TenantId, session.Id, "LocalAiTurnProcessed",
                JsonSerializer.Serialize(new { sourceEventId, skipped = "call-ended" }), ct);
            return;
        }

        using var sourceJson = JsonDocument.Parse(source.PayloadJson ?? "{}");
        var recordingId = sourceJson.RootElement.TryGetProperty("recordingId", out var recordingValue)
            && recordingValue.TryGetGuid(out var parsedRecordingId) ? parsedRecordingId : session.RecordingId;
        if (recordingId is null || session.RecordingId != recordingId)
        {
            await scope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
                source.TenantId, session.Id, "LocalAiTurnProcessed",
                JsonSerializer.Serialize(new { sourceEventId, skipped = "superseded-recording" }), ct);
            return;
        }
        var recording = await db.Recordings.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == recordingId && x.TenantId == source.TenantId && !x.IsDeleted, ct);

        var stt = await ResolveProviderAsync(db, source.TenantId, "Stt", "http://local-ai:8080", "small", ct);
        var llm = await ResolveProviderAsync(db, source.TenantId, "Llm", "http://ollama:11434", "qwen3:4b", ct);
        var tts = await ResolveProviderAsync(db, source.TenantId, "Tts", "http://local-ai:8080", "piper", ct);
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageService>();
        await using var callerAudio = await storage.DownloadAsync(Bucket, recording.ObjectStorageKey, ct);
        var transcript = await TranscribeAsync(stt, callerAudio, ct);
        if (string.IsNullOrWhiteSpace(transcript.Text))
            throw new InvalidOperationException("Local speech recognition returned an empty transcript.");

        var agent = await ResolveAgentAsync(db, source.TenantId, ct);
        var conversation = await db.AiConversations.IgnoreQueryFilters()
            .Where(x => x.TenantId == source.TenantId && x.CallSessionId == session.Id && !x.IsDeleted)
            .OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(ct);
        if (conversation is null)
        {
            conversation = new AiConversation { TenantId = source.TenantId, AiAgentId = agent.Id,
                CallSessionId = session.Id, CustomerId = session.CustomerId,
                Status = AiConversationStatus.Processing, DetectedLanguage = transcript.Language };
            db.AiConversations.Add(conversation);
            await db.SaveChangesAsync(ct);
        }

        var priorTurns = await db.AiConversationTurns.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == source.TenantId && x.AiConversationId == conversation.Id && !x.IsDeleted)
            .OrderBy(x => x.Sequence).Take(80).ToListAsync(ct);
        var sequence = priorTurns.Count == 0 ? 1 : priorTurns.Max(x => x.Sequence) + 1;
        db.AiConversationTurns.Add(new AiConversationTurn { TenantId = source.TenantId,
            AiConversationId = conversation.Id, Speaker = AiSpeaker.Customer,
            Text = transcript.Text, Sequence = sequence });

        var explicitHandoff = HumanRequestRegex().IsMatch(transcript.Text);
        var decision = explicitHandoff
            ? new VoiceDecision(transcript.Language.StartsWith("bn", StringComparison.OrdinalIgnoreCase)
                ? "অনুগ্রহ করে অপেক্ষা করুন, আমি আপনাকে একজন মানব সহায়তা প্রতিনিধির সাথে সংযুক্ত করছি।"
                : "Please hold while I connect you to a human support agent.", false, true,
                "Caller explicitly requested a human agent.")
            : await GenerateReplyAsync(llm, agent, priorTurns, transcript.Text, transcript.Language, ct);
        if (string.IsNullOrWhiteSpace(decision.Reply))
            decision = decision with { Reply = transcript.Language.StartsWith("bn", StringComparison.OrdinalIgnoreCase)
                ? "দুঃখিত, অনুগ্রহ করে কথাটি আবার বলুন।" : "Sorry, please say that again." };

        conversation.DetectedLanguage = transcript.Language;
        conversation.UpdatedAt = DateTime.UtcNow;

        var handoffExtension = decision.HandoffRequested
            ? await ResolveHandoffExtensionAsync(db, source.TenantId, ct) : null;
        var handoff = decision.HandoffRequested && handoffExtension is not null;
        if (decision.HandoffRequested && !handoff)
        {
            decision = decision with { Reply = transcript.Language.StartsWith("bn", StringComparison.OrdinalIgnoreCase)
                ? "এই মুহূর্তে কোনো মানব প্রতিনিধি available নেই। অনুগ্রহ করে কিছুক্ষণ পরে আবার চেষ্টা করুন।"
                : "No human agent is available right now. Please try again shortly.", HandoffRequested = false };
            conversation.Outcome = "Human agent unavailable";
        }

        sequence++;
        db.AiConversationTurns.Add(new AiConversationTurn { TenantId = source.TenantId,
            AiConversationId = conversation.Id, Speaker = AiSpeaker.AiAgent,
            Text = decision.Reply, Sequence = sequence });
        conversation.Transcript = BuildTranscript(priorTurns, transcript.Text, decision.Reply);
        if (decision.EndCall)
        {
            conversation.Status = AiConversationStatus.Completed;
            conversation.EndedAt = DateTime.UtcNow;
            conversation.Outcome = "Completed by local AI";
        }

        if (handoff)
        {
            conversation.Status = AiConversationStatus.HandoffRequested;
            conversation.HandoffReason = decision.HandoffReason;
            var summary = await scope.ServiceProvider.GetRequiredService<IHandoffContextSummarizer>()
                .SummarizeAsync(source.TenantId, conversation.Transcript ?? transcript.Text,
                    decision.HandoffReason ?? "Caller requested a human agent.", ct);
            conversation.Summary = summary.Summary;
            await scope.ServiceProvider.GetRequiredService<ICallService>().RecordEventAsync(
                source.TenantId, session.Id, "HumanHandoffContextPrepared", JsonSerializer.Serialize(new
                {
                    conversationId = conversation.Id, session.FromNumber, session.ToNumber,
                    handoffReason = decision.HandoffReason, handoffExtension,
                    summary.Summary, summary.CustomerIntent, summary.DetectedLanguage, summary.Sentiment,
                    summary.CollectedDetails, summary.UnresolvedItems, summary.SuggestedOpening,
                    summary.Provider, summary.UsedFallback, contextStage = "final",
                    transcript = conversation.Transcript, preparedAtUtc = DateTime.UtcNow
                }), ct);
        }

        var responseName = $"ccaas-{session.Id:N}-local-ai-{recordingId:N}";
        await using var responseAudio = await SynthesizeAsync(tts, decision.Reply, transcript.Language, agent.VoiceName, ct);
        var objectKey = $"{source.TenantId:N}/calls/{session.Id:N}/responses/{responseName}.wav";
        await storage.UploadAsync(Bucket, objectKey, responseAudio, "audio/wav", ct);
        await db.SaveChangesAsync(ct);

        var calls = scope.ServiceProvider.GetRequiredService<ICallService>();
        await calls.RecordEventAsync(source.TenantId, session.Id, "VoiceResponseSubmitted",
            JsonSerializer.Serialize(new { recordingId, responseName, objectKey,
                endCall = decision.EndCall, handoffAfterPlayback = handoff, handoffExtension,
                handoffReason = decision.HandoffReason, transcript = transcript.Text,
                responseText = decision.Reply, conversationId = conversation.Id, provider = "local-ai" }), ct);
        await calls.RecordEventAsync(source.TenantId, session.Id, "LocalAiTurnProcessed",
            JsonSerializer.Serialize(new { sourceEventId, recordingId, conversationId = conversation.Id,
                stt = stt.Model, llm = llm.Model, tts = tts.Model }), ct);
    }

    private async Task<bool> IsLocalModeAsync(CcaasDbContext db, Guid tenantId, CancellationToken ct)
    {
        var configured = await db.SystemSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive
                && x.Category == "AiVoice" && x.Key == "ProcessingMode")
            .Select(x => x.Value).FirstOrDefaultAsync(ct);
        return string.Equals(configured ?? _configuration["AiVoice:Mode"] ?? "Local",
            "Local", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Provider> ResolveProviderAsync(CcaasDbContext db, Guid tenantId,
        string capability, string fallbackUrl, string fallbackModel, CancellationToken ct)
    {
        var profile = await db.AiProviderProfiles.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive && x.Capability == capability)
            .OrderBy(x => x.Priority).FirstOrDefaultAsync(ct);
        if (profile is null) return new Provider(fallbackUrl, fallbackModel, 60);
        var baseUrl = profile.BaseUrl.TrimEnd('/');
        if (capability == "Llm" && baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^3];
        return new Provider(baseUrl, profile.Model, profile.TimeoutSeconds);
    }

    private static async Task<AiAgent> ResolveAgentAsync(CcaasDbContext db, Guid tenantId, CancellationToken ct)
    {
        var selectedId = await db.SystemSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive
                && x.Category == "AiVoice" && x.Key == "DefaultAgentId")
            .Select(x => x.Value).FirstOrDefaultAsync(ct);
        if (Guid.TryParse(selectedId, out var id))
        {
            var selected = await db.AiAgents.IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted
                    && x.IsActive && x.AllowInboundCalls, ct);
            if (selected is not null) return selected;
        }
        return await db.AiAgents.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive && x.AllowInboundCalls)
            .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("No active inbound AI agent is configured.");
    }

    private async Task<Transcript> TranscribeAsync(Provider provider, Stream audio, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 10, 300)));
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(audio), "file", "caller.wav");
        form.Add(new StringContent("auto"), "language");
        using var response = await _clients.CreateClient("local-ai")
            .PostAsync(provider.BaseUrl + "/v1/audio/transcriptions", form, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return new Transcript(json.RootElement.GetProperty("text").GetString()?.Trim() ?? "",
            json.RootElement.TryGetProperty("language", out var language) ? language.GetString() ?? "en" : "en");
    }

    private async Task<VoiceDecision> GenerateReplyAsync(Provider provider, AiAgent agent,
        IReadOnlyList<AiConversationTurn> history, string latest, string language, CancellationToken ct)
    {
        var messages = new List<object> { new { role = "system", content =
            $"{agent.SystemPrompt}\nThis is a live phone call. Reply in the caller's language ({language}). " +
            "Use no more than two short spoken sentences and ask one question at a time. " +
            "Return only JSON with reply, endCall, handoffRequested, handoffReason. " +
            "Set handoffRequested when the caller requests a person or the request is outside your ability." } };
        messages.AddRange(history.TakeLast(30).Select(x => new { role = x.Speaker == AiSpeaker.Customer ? "user" : "assistant", content = x.Text }));
        messages.Add(new { role = "user", content = latest });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 10, 300)));
        using var response = await _clients.CreateClient("ollama").PostAsJsonAsync(provider.BaseUrl + "/api/chat",
            new { model = provider.Model, messages, stream = false, format = "json",
                options = new { temperature = 0.2, num_predict = 180 } }, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var content = envelope.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "{}";
        using var result = JsonDocument.Parse(content);
        var root = result.RootElement;
        return new VoiceDecision(root.TryGetProperty("reply", out var reply) ? reply.GetString() ?? "" : "",
            root.TryGetProperty("endCall", out var end) && end.ValueKind == JsonValueKind.True,
            root.TryGetProperty("handoffRequested", out var handoff) && handoff.ValueKind == JsonValueKind.True,
            root.TryGetProperty("handoffReason", out var reason) ? reason.GetString() : null);
    }

    private async Task<Stream> SynthesizeAsync(Provider provider, string text, string language,
        string voice, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 10, 300)));
        using var response = await _clients.CreateClient("local-ai").PostAsJsonAsync(
            provider.BaseUrl + "/v1/audio/speech", new { input = text,
                language = language.StartsWith("bn", StringComparison.OrdinalIgnoreCase) ? "bn-BD" : "en-US", voice }, timeout.Token);
        response.EnsureSuccessStatusCode();
        var memory = new MemoryStream();
        await response.Content.CopyToAsync(memory, timeout.Token);
        memory.Position = 0;
        return memory;
    }

    private static async Task<string?> ResolveHandoffExtensionAsync(CcaasDbContext db, Guid tenantId, CancellationToken ct)
        => await (from member in db.QueueMembers.IgnoreQueryFilters().AsNoTracking()
                  join agent in db.Agents.IgnoreQueryFilters().AsNoTracking() on member.AgentId equals agent.Id
                  join extension in db.Extensions.IgnoreQueryFilters().AsNoTracking() on agent.Id equals extension.AgentId
                  where member.TenantId == tenantId && !member.IsDeleted && member.IsActive
                        && agent.TenantId == tenantId && !agent.IsDeleted && agent.Presence == AgentPresence.Available
                        && extension.TenantId == tenantId && !extension.IsDeleted
                  orderby member.Priority, member.Penalty, agent.PresenceChangedAt
                  select extension.ExtensionNumber).FirstOrDefaultAsync(ct);

    private static string BuildTranscript(IReadOnlyList<AiConversationTurn> prior, string customer, string ai)
    {
        var lines = prior.TakeLast(60).Select(x => $"{(x.Speaker == AiSpeaker.Customer ? "Customer" : "AI")}: {x.Text}")
            .Append($"Customer: {customer}").Append($"AI: {ai}");
        var value = string.Join('\n', lines);
        return value[..Math.Min(value.Length, 16_000)];
    }

    private sealed record Provider(string BaseUrl, string Model, int TimeoutSeconds);
    private sealed record Transcript(string Text, string Language);
    private sealed record VoiceDecision(string Reply, bool EndCall, bool HandoffRequested, string? HandoffReason);

    [GeneratedRegex(@"\b(human|operator|representative|supervisor|live\s+(person|agent)|real\s+(person|agent)|transfer\s+me)\b|মানুষ|হিউম্যান|এজেন্ট|অপারেটর|প্রতিনিধি|সুপারভাইজার|কথা\s*বলতে\s*চাই|কল\s*ট্রান্সফার", RegexOptions.IgnoreCase)]
    private static partial Regex HumanRequestRegex();
}

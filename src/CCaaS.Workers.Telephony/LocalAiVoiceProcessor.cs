using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CCaaS.Application.Ai;
using CCaaS.Application.Appointment;
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
    private const string AppointmentStatePrefix = "[APPOINTMENT_STATE]";
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
        using var pollTiming = new VoiceTiming(_logger, "worker", "poll", "poll_cycle");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CcaasDbContext>();
        var candidates = await CCaaS.Infrastructure.Telephony.PendingVoiceTurns.Query(
            db.CallEvents.IgnoreQueryFilters(), db.CallSessions.IgnoreQueryFilters())
            .AsNoTracking().OrderByDescending(x => x.OccurredAt).Take(100).ToListAsync(ct);
        var tasks = new List<Task>();
        foreach (var source in candidates)
        {
            if (_inflight.ContainsKey(source.Id)) continue;
            if (!await IsLocalModeAsync(db, source.TenantId, ct)) continue;
            var marker = source.Id.ToString("D");
            var legacyMarker = source.Id.ToString("N");
            var done = await db.CallEvents.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
                x.TenantId == source.TenantId && x.CallSessionId == source.CallSessionId && !x.IsDeleted
                && x.EventType == "LocalAiTurnProcessed" && x.PayloadJson != null
                && (x.PayloadJson.Contains(marker) || x.PayloadJson.Contains(legacyMarker)), ct);
            if (done || !_inflight.TryAdd(source.Id, 0)) continue;
            tasks.Add(ProcessGuardedAsync(source.Id, source.CallSessionId, ct));
        }
        if (tasks.Count > 0) await Task.WhenAll(tasks);
        pollTiming.Complete();
    }

    private int _activeTurns;

    private async Task ProcessGuardedAsync(Guid sourceEventId, Guid callId, CancellationToken ct)
    {
        var acquired = false;
        try
        {
            await VoiceTiming.Run(_logger, callId, sourceEventId, "queue_wait", () => _capacity.WaitAsync(ct));
            acquired = true;
            Interlocked.Increment(ref _activeTurns);
            using var turnTiming = new VoiceTiming(_logger, callId, sourceEventId, "turn_processing_total", Volatile.Read(ref _activeTurns));
            await ProcessTurnAsync(sourceEventId, ct);
            turnTiming.Complete();
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
            if (acquired) { Interlocked.Decrement(ref _activeTurns); _capacity.Release(); }
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
        _logger.LogInformation("VOICE_EVENT_LINK {Link}", JsonSerializer.Serialize(new {
            call_id = session.Id, turn_id = recordingId, source_event_id = sourceEventId,
            source_created_at = source.OccurredAt, processing_started_at = DateTime.UtcNow,
            event_age_ms = (DateTime.UtcNow - source.OccurredAt).TotalMilliseconds }));
        var recording = await db.Recordings.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == recordingId && x.TenantId == source.TenantId && !x.IsDeleted, ct);

        _logger.LogInformation("VOICE_RECORDING_LINK {Link}", JsonSerializer.Serialize(new {
            call_id = session.Id, turn_id = recordingId,
            recording_name = Path.GetFileNameWithoutExtension(recording.ObjectStorageKey),
            audio_duration_seconds = recording.DurationSeconds }));
        var stt = await ResolveProviderAsync(db, source.TenantId, "Stt", "http://local-ai:8080", "small", ct);
        var llm = await ResolveProviderAsync(db, source.TenantId, "Llm", "http://ollama:11434", "qwen3:4b", ct);
        var tts = await ResolveProviderAsync(db, source.TenantId, "Tts", "http://local-ai:8080", "piper", ct);
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageService>();
        await using var callerAudio = await VoiceTiming.Run(_logger, session.Id, recordingId, "recording_download", () => storage.DownloadAsync(Bucket, recording.ObjectStorageKey, ct));
        var transcript = await VoiceTiming.Run(_logger, session.Id, recordingId, "stt_http_batch", () => TranscribeAsync(stt, callerAudio, ct));
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

        VoiceTiming.Current.Value = (session.Id.ToString(), recordingId.ToString()!);
        using var decisionTiming = new VoiceTiming(_logger, session.Id, recordingId, "decision");
        var explicitHandoff = HumanRequestRegex().IsMatch(transcript.Text);
        var appointment = explicitHandoff ? null : await TryHandleAppointmentAsync(
            scope.ServiceProvider.GetRequiredService<IAppointmentService>(), source.TenantId,
            priorTurns, transcript.Text, transcript.Language, ct);
        var decision = explicitHandoff
            ? new VoiceDecision(transcript.Language.StartsWith("bn", StringComparison.OrdinalIgnoreCase)
                ? "অনুগ্রহ করে অপেক্ষা করুন, আমি আপনাকে একজন মানব সহায়তা প্রতিনিধির সাথে সংযুক্ত করছি।"
                : "Please hold while I connect you to a human support agent.", false, true,
                "Caller explicitly requested a human agent.")
            : appointment?.Decision ?? TryFastCommonReply(transcript.Text, transcript.Language)
                ?? await GenerateReplyAsync(llm, agent, priorTurns, transcript.Text, transcript.Language, ct);
        if (string.IsNullOrWhiteSpace(decision.Reply))
            decision = decision with { Reply = transcript.Language.StartsWith("bn", StringComparison.OrdinalIgnoreCase)
                ? "দুঃখিত, অনুগ্রহ করে কথাটি আবার বলুন।" : "Sorry, please say that again." };

        var route = explicitHandoff ? "handoff" : appointment is not null ? "appointment" : "general";
        // Free-form model output must not decide when a live call is terminated.
        if (decision.EndCall && appointment is null && !GoodbyeRegex().IsMatch(transcript.Text))
            decision = decision with { EndCall = false };
        _logger.LogInformation("VOICE_DECISION {Decision}", JsonSerializer.Serialize(new {
            call_id = session.Id, turn_id = recordingId, route, language = transcript.Language,
            end_call = decision.EndCall, handoff_requested = decision.HandoffRequested }));
        decisionTiming.Complete();
        decisionTiming.Dispose();
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

        if (appointment is not null)
        {
            sequence++;
            db.AiConversationTurns.Add(new AiConversationTurn { TenantId = source.TenantId,
                AiConversationId = conversation.Id, Speaker = AiSpeaker.System,
                Text = AppointmentStatePrefix + JsonSerializer.Serialize(appointment.State), Sequence = sequence });
            conversation.Intent = "Appointment";
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
        await using var responseAudio = await VoiceTiming.Run(_logger, session.Id, recordingId, "tts_http_batch", () => SynthesizeAsync(tts, decision.Reply, transcript.Language, agent.VoiceName, ct));
        var objectKey = $"{source.TenantId:N}/calls/{session.Id:N}/responses/{responseName}.wav";
        await VoiceTiming.Run(_logger, session.Id, recordingId, "response_upload", () => storage.UploadAsync(Bucket, objectKey, responseAudio, "audio/wav", ct));
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

    private async Task<Provider> ResolveProviderAsync(CcaasDbContext db, Guid tenantId,
        string capability, string fallbackUrl, string fallbackModel, CancellationToken ct)
    {
        var profile = await db.AiProviderProfiles.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive && x.Capability == capability)
            .OrderBy(x => x.Priority).FirstOrDefaultAsync(ct);
        var configuredModel = string.Equals(capability, "Llm", StringComparison.OrdinalIgnoreCase)
            ? _configuration["AiVoice:Model"] : null;
        var configuredTimeout = _configuration.GetValue<int?>("AiVoice:ProviderTimeoutSeconds");
        if (profile is null)
            return new Provider(fallbackUrl, configuredModel ?? fallbackModel,
                Math.Clamp(configuredTimeout ?? 120, 30, 300));
        var baseUrl = profile.BaseUrl.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configuredModel))
            return new Provider(baseUrl, configuredModel, Math.Clamp(configuredTimeout ?? 120, 30, 300));
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
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl + "/v1/audio/transcriptions") { Content = form };
        AddTimingHeaders(request);
        using var response = await _clients.CreateClient("local-ai").SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return new Transcript(json.RootElement.GetProperty("text").GetString()?.Trim() ?? "",
            json.RootElement.TryGetProperty("language", out var language) ? language.GetString() ?? "en" : "en");
    }

    private async Task<VoiceDecision> GenerateReplyAsync(Provider provider, AiAgent agent,
        IReadOnlyList<AiConversationTurn> history, string latest, string language, CancellationToken ct)
    {
        using var llmTiming = new VoiceTiming(_logger, VoiceTiming.Current.Value?.Call ?? "unknown", VoiceTiming.Current.Value?.Turn ?? "unknown", "llm_http");
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
            new { model = provider.Model, messages, stream = false, format = "json", think = false,
                keep_alive = "30m", options = new { temperature = 0.2, num_predict = 120 } }, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var content = envelope.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "{}";
        using var result = JsonDocument.Parse(content);
        var root = result.RootElement;
        llmTiming.Complete();
        return new VoiceDecision(root.TryGetProperty("reply", out var reply) ? reply.GetString() ?? "" : "",
            root.TryGetProperty("endCall", out var end) && end.ValueKind == JsonValueKind.True,
            root.TryGetProperty("handoffRequested", out var handoff) && handoff.ValueKind == JsonValueKind.True,
            root.TryGetProperty("handoffReason", out var reason) ? reason.GetString() : null);
    }

    private async Task<AppointmentResult?> TryHandleAppointmentAsync(IAppointmentService appointments,
        Guid tenantId, IReadOnlyList<AiConversationTurn> history, string input, string detectedLanguage,
        CancellationToken ct)
    {
        var state = LoadAppointmentState(history);
        if (state is null && !AppointmentIntentRegex().IsMatch(input)) return null;

        var bn = detectedLanguage.StartsWith("bn", StringComparison.OrdinalIgnoreCase)
            || BengaliTextRegex().IsMatch(input);
        state ??= new AppointmentState("date-time", null, null, null, null, null, bn);
        state = state with { Bengali = state.Bengali || bn };

        if (CancelRegex().IsMatch(input))
            return Result(state with { Stage = "cancelled" }, state.Bengali
                ? "অ্যাপয়েন্টমেন্ট অনুরোধটি বাতিল করা হয়েছে। আর কিছুতে সাহায্য করতে পারি?"
                : "The appointment request has been cancelled. Can I help with anything else?");

        var localDate = ParseDate(input);
        var localTime = ParseTime(input);
        if (state.Date is null && localDate is not null) state = state with { Date = localDate };
        if (state.Date is null)
            return Result(state with { Stage = "date-time" }, state.Bengali
                ? "কোন তারিখে অ্যাপয়েন্টমেন্ট চান? আজ, আগামীকাল, অথবা তারিখটি বলুন।"
                : "What date would you like the appointment? You can say today, tomorrow, or a date.");

        if (state.SlotId is null)
        {
            var slots = await appointments.GetAvailabilityAsync(tenantId, DateOnly.Parse(state.Date), ct);
            if (slots.Count == 0)
                return Result(state with { Stage = "date-time" }, state.Bengali
                    ? $"{FormatDate(state.Date)} তারিখে কোনো সময় খালি নেই। অন্য একটি তারিখ বলুন।"
                    : $"There are no available slots on {FormatDate(state.Date)}. Please choose another date.");

            var zone = ResolveAppointmentTimeZone();
            AvailableAppointmentSlot? selected = null;
            if (localTime is not null)
                selected = slots.OrderBy(x => Math.Abs((TimeZoneInfo.ConvertTimeFromUtc(x.StartsAtUtc, zone).TimeOfDay - localTime.Value).TotalMinutes))
                    .FirstOrDefault(x => Math.Abs((TimeZoneInfo.ConvertTimeFromUtc(x.StartsAtUtc, zone).TimeOfDay - localTime.Value).TotalMinutes) <= 20);
            if (selected is null)
            {
                var choices = string.Join(", ", slots.Take(4).Select(x =>
                    TimeZoneInfo.ConvertTimeFromUtc(x.StartsAtUtc, zone).ToString("h:mm tt")));
                return Result(state with { Stage = "date-time" }, state.Bengali
                    ? $"খালি সময়গুলো হলো {choices}। কোন সময়টি চান?"
                    : $"Available times are {choices}. Which time would you like?");
            }
            state = state with { SlotId = selected.SlotId, SlotTimeUtc = selected.StartsAtUtc, Stage = "name" };
        }

        var phone = ParsePhone(input);
        var name = ParseName(input, state.Stage == "name");
        if (state.CustomerName is null && name is not null) state = state with { CustomerName = name };
        if (state.Contact is null && phone is not null) state = state with { Contact = phone };
        if (state.CustomerName is null)
            return Result(state with { Stage = "name" }, state.Bengali
                ? "অ্যাপয়েন্টমেন্টটি কার নামে করব?"
                : "What name should I use for the appointment?");
        if (state.Contact is null)
            return Result(state with { Stage = "contact" }, state.Bengali
                ? $"ধন্যবাদ {state.CustomerName}। আপনার ফোন নম্বরটি বলুন।"
                : $"Thank you, {state.CustomerName}. What is your phone number?");

        if (state.Stage != "confirm")
        {
            state = state with { Stage = "confirm" };
            var when = FormatSlot(state.SlotTimeUtc!.Value);
            return Result(state, state.Bengali
                ? $"{state.CustomerName} নামে {when}-এর অ্যাপয়েন্টমেন্ট নিশ্চিত করব? হ্যাঁ অথবা না বলুন।"
                : $"Shall I confirm the appointment for {state.CustomerName} at {when}? Please say yes or no.");
        }

        if (!ConfirmRegex().IsMatch(input))
            return Result(state, state.Bengali ? "বুকিং নিশ্চিত করতে হ্যাঁ, অথবা বাতিল করতে না বলুন।"
                : "Please say yes to confirm the booking, or no to cancel.");

        try
        {
            var booking = await appointments.BookAsync(tenantId,
                new BookAppointmentCommand(state.SlotId!.Value, state.CustomerName, state.Contact, state.Purpose), ct);
            state = state with { Stage = "booked" };
            return Result(state, state.Bengali
                ? $"আপনার অ্যাপয়েন্টমেন্ট নিশ্চিত হয়েছে। বুকিং রেফারেন্স {booking.BookingReference}। ধন্যবাদ।"
                : $"Your appointment is confirmed. Booking reference {booking.BookingReference}. Thank you.", true);
        }
        catch (InvalidOperationException)
        {
            state = state with { SlotId = null, SlotTimeUtc = null, Stage = "date-time" };
            return Result(state, state.Bengali
                ? "দুঃখিত, সময়টি ইতিমধ্যে বুক হয়েছে। অন্য সময় বলুন।"
                : "Sorry, that slot was just booked. Please choose another time.");
        }
    }

    private static VoiceDecision? TryFastCommonReply(string input, string language)
    {
        var bn = language.StartsWith("bn", StringComparison.OrdinalIgnoreCase) || BengaliTextRegex().IsMatch(input);
        if (GreetingRegex().IsMatch(input))
            return new VoiceDecision(bn
                ? "স্বাগতম। আমি অ্যাপয়েন্টমেন্ট বুক করা, সময় দেখা এবং সাধারণ সহায়তা দিতে পারি। আপনি কী করতে চান?"
                : "Welcome. I can check availability, book appointments, and provide general help. What would you like to do?",
                false, false, null);
        if (GoodbyeRegex().IsMatch(input))
            return new VoiceDecision(bn ? "ধন্যবাদ। ভালো থাকবেন।" : "Thank you. Goodbye.", true, false, null);
        return null;
    }

    private AppointmentState? LoadAppointmentState(IReadOnlyList<AiConversationTurn> history)
    {
        var text = history.LastOrDefault(x => x.Speaker == AiSpeaker.System
            && x.Text.StartsWith(AppointmentStatePrefix, StringComparison.Ordinal))?.Text;
        if (text is null) return null;
        try { return JsonSerializer.Deserialize<AppointmentState>(text[AppointmentStatePrefix.Length..]); }
        catch (JsonException) { return null; }
    }

    private TimeZoneInfo ResolveAppointmentTimeZone()
    {
        var id = _configuration["AiVoice:AppointmentTimeZoneId"] ?? "Asia/Dhaka";
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }

    private string FormatSlot(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, ResolveAppointmentTimeZone())
        .ToString("dddd, d MMMM 'at' h:mm tt");
    private static string FormatDate(string date) => DateOnly.Parse(date).ToString("d MMMM yyyy");
    private static AppointmentResult Result(AppointmentState state, string reply, bool end = false)
        => new(state, new VoiceDecision(reply, end, false, null));

    private static string? ParseDate(string input)
    {
        if (Regex.IsMatch(input, @"আগামীকাল|tomorrow", RegexOptions.IgnoreCase)) return DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd");
        if (Regex.IsMatch(input, @"আজ|today", RegexOptions.IgnoreCase)) return DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var match = Regex.Match(ToAsciiDigits(input), @"\b(20\d{2})[-/](\d{1,2})[-/](\d{1,2})\b");
        return match.Success && DateOnly.TryParse($"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}", out var date)
            ? date.ToString("yyyy-MM-dd") : null;
    }

    private static TimeSpan? ParseTime(string input)
    {
        var value = ToAsciiDigits(input);
        var match = Regex.Match(value, @"(?<!\d)(\d{1,2})(?::(\d{2}))?\s*(am|pm|টা|টায়|টায়)?", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var hour)) return null;
        var minute = int.TryParse(match.Groups[2].Value, out var parsedMinute) ? parsedMinute : 0;
        var suffix = match.Groups[3].Value.ToLowerInvariant();
        var afternoon = suffix == "pm" || Regex.IsMatch(input, "দুপুর|বিকাল|সন্ধ্যা|রাত");
        if (afternoon && hour < 12) hour += 12;
        if (suffix == "am" && hour == 12) hour = 0;
        return hour is >= 0 and < 24 && minute is >= 0 and < 60 ? new TimeSpan(hour, minute, 0) : null;
    }

    private static string? ParsePhone(string input)
    {
        var match = Regex.Match(ToAsciiDigits(input), @"(?<!\d)(?:\+?88)?0?1[3-9]\d{8}(?!\d)");
        return match.Success ? match.Value : null;
    }

    private static string? ParseName(string input, bool acceptWholeInput)
    {
        var match = Regex.Match(input, @"(?:my\s+name\s+is|name\s+is|আমার\s+নাম)\s*[:,-]?\s*([\p{L} .'-]{2,60})", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value.Trim();
        if (!acceptWholeInput || ParsePhone(input) is not null || input.Length > 60 || Regex.IsMatch(input, @"\d")) return null;
        return input.Trim(' ', '.', ',', '?');
    }

    private static string ToAsciiDigits(string value)
    {
        const string bengali = "০১২৩৪৫৬৭৮৯";
        var chars = value.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            var digit = bengali.IndexOf(chars[index]);
            if (digit >= 0) chars[index] = (char)('0' + digit);
        }
        return new string(chars);
    }

    private async Task<Stream> SynthesizeAsync(Provider provider, string text, string language,
        string voice, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 10, 300)));
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl + "/v1/audio/speech")
        { Content = JsonContent.Create(new { input = text,
            language = language.StartsWith("bn", StringComparison.OrdinalIgnoreCase) ? "bn-BD" : "en-US", voice }) };
        AddTimingHeaders(request);
        using var response = await _clients.CreateClient("local-ai").SendAsync(request, timeout.Token);
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

    private static void AddTimingHeaders(HttpRequestMessage request)
    {
        if (VoiceTiming.Current.Value is { } ids)
        {
            request.Headers.TryAddWithoutValidation("X-Call-Id", ids.Call);
            request.Headers.TryAddWithoutValidation("X-Turn-Id", ids.Turn);
        }
    }

    private sealed record Provider(string BaseUrl, string Model, int TimeoutSeconds);
    private sealed record Transcript(string Text, string Language);
    private sealed record VoiceDecision(string Reply, bool EndCall, bool HandoffRequested, string? HandoffReason);
    private sealed record AppointmentResult(AppointmentState State, VoiceDecision Decision);
    private sealed record AppointmentState(string Stage, string? Date, Guid? SlotId, DateTime? SlotTimeUtc,
        string? CustomerName, string? Contact, bool Bengali, string? Purpose = null);

    [GeneratedRegex(@"appointment|book(?:ing)?|schedule|doctor|অ্যাপয়েন্টমেন্ট|এপয়েন্টমেন্ট|বুকিং|ডাক্তার|সময়\s*(?:চাই|নিতে)", RegexOptions.IgnoreCase)]
    private static partial Regex AppointmentIntentRegex();
    [GeneratedRegex(@"[\u0980-\u09FF]")]
    private static partial Regex BengaliTextRegex();
    [GeneratedRegex(@"\b(yes|confirm|book|okay|ok)\b|হ্যাঁ|হ্যা|নিশ্চিত|বুক\s*কর", RegexOptions.IgnoreCase)]
    private static partial Regex ConfirmRegex();
    [GeneratedRegex(@"\b(no|cancel|stop)\b|(?:^|\s)না(?:\s|$|[।,.!?])|বাতিল", RegexOptions.IgnoreCase)]
    private static partial Regex CancelRegex();
    [GeneratedRegex(@"^\s*(hello|hi|hey|assalamu alaikum|হ্যালো|হাই|সালাম|আসসালামু আলাইকুম)[।,.!?\s]*$", RegexOptions.IgnoreCase)]
    private static partial Regex GreetingRegex();
    [GeneratedRegex(@"\b(goodbye|bye|thank you|thanks)\b|বিদায়|ধন্যবাদ", RegexOptions.IgnoreCase)]
    private static partial Regex GoodbyeRegex();

    [GeneratedRegex(@"\b(human|operator|representative|supervisor|live\s+(person|agent)|real\s+(person|agent)|transfer\s+me)\b|মানুষ|হিউম্যান|এজেন্ট|অপারেটর|প্রতিনিধি|সুপারভাইজার|কথা\s*বলতে\s*চাই|কল\s*ট্রান্সফার", RegexOptions.IgnoreCase)]
    private static partial Regex HumanRequestRegex();
}

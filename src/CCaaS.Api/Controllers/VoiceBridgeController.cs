using System.Text.Json;
using CCaaS.Application.Ai;
using CCaaS.Application.Calls;
using CCaaS.Domain.Ai;
using CCaaS.Domain.Organization;
using CCaaS.Infrastructure.ObjectStorage;
using CCaaS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CCaaS.Api.Controllers;

[Authorize]
[Route("api/voice-bridge")]
public sealed class VoiceBridgeController : ApiControllerBase
{
    private const string Bucket = "call-recordings";
    private readonly CcaasDbContext _db;
    private readonly IObjectStorageService _storage;
    private readonly ICallService _calls;
    private readonly IHandoffContextSummarizer _handoffSummarizer;
    private readonly IConfiguration _configuration;

    public VoiceBridgeController(CcaasDbContext db, IObjectStorageService storage, ICallService calls,
        IHandoffContextSummarizer handoffSummarizer, IConfiguration configuration)
    {
        _db = db;
        _storage = storage;
        _calls = calls;
        _handoffSummarizer = handoffSummarizer;
        _configuration = configuration;
    }

    [HttpGet("pending")]
    public async Task<IActionResult> Pending(CancellationToken ct)
    {
        var mode = await GetProcessingModeAsync(ct);
        if (string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase))
            return Ok(Array.Empty<object>());

        var staleCutoff = DateTime.UtcNow.AddMinutes(-5);
        var sessions = await _db.CallSessions.IgnoreQueryFilters()
            .Where(x => x.TenantId == TenantId && !x.IsDeleted && x.EndedAt == null && x.RecordingId != null)
            .OrderBy(x => x.StartedAt)
            .Take(20)
            .AsNoTracking()
            .ToListAsync(ct);

        var result = new List<object>();
        foreach (var session in sessions)
        {
            var latest = await _db.CallEvents.IgnoreQueryFilters()
                .Where(x => x.TenantId == TenantId && !x.IsDeleted && x.CallSessionId == session.Id)
                .OrderByDescending(x => x.OccurredAt)
                .FirstOrDefaultAsync(ct);
            // A worker/container restart loses the in-memory ARI channel map. Do not feed
            // an orphan recording from that previous process to a newly opened browser bridge.
            if (latest?.EventType != "AwaitingVoiceBridge" || latest.OccurredAt < staleCutoff) continue;

            var recording = await _db.Recordings.IgnoreQueryFilters()
                .SingleOrDefaultAsync(x => x.Id == session.RecordingId && x.TenantId == TenantId && !x.IsDeleted, ct);
            if (recording is null) continue;
            result.Add(new
            {
                callSessionId = session.Id,
                recordingId = recording.Id,
                session.FromNumber,
                session.ToNumber,
                recording.DurationSeconds,
                recordedAtUtc = recording.CreatedAt
            });
        }
        return Ok(result);
    }

    [HttpGet("mode")]
    public async Task<IActionResult> Mode(CancellationToken ct)
    {
        var mode = await GetProcessingModeAsync(ct);
        return Ok(new { mode, maxConcurrentCalls = _configuration.GetValue("AiVoice:MaxConcurrentCalls", 2),
            browserRequired = !string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase) });
    }

    private async Task<string> GetProcessingModeAsync(CancellationToken ct)
        => await _db.SystemSettings.AsNoTracking()
            .Where(x => x.Category == "AiVoice" && x.Key == "ProcessingMode" && x.IsActive)
            .Select(x => x.Value).FirstOrDefaultAsync(ct)
            ?? _configuration["AiVoice:Mode"] ?? "Local";

    [HttpGet("recordings/{recordingId:guid}")]
    public async Task<IActionResult> DownloadRecording(Guid recordingId, CancellationToken ct)
    {
        var recording = await _db.Recordings.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == recordingId && x.TenantId == TenantId && !x.IsDeleted, ct);
        if (recording is null) return NotFound();
        var stream = await _storage.DownloadAsync(Bucket, recording.ObjectStorageKey, ct);
        return File(stream, "audio/wav", $"caller-{recording.Id:N}.wav");
    }

    [HttpPost("calls/{callSessionId:guid}/response")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> SubmitResponse(Guid callSessionId,
        [FromForm] IFormFile audio,
        [FromForm] Guid recordingId,
        [FromForm] string transcript,
        [FromForm] string responseText,
        [FromForm] bool endCall,
        [FromForm] bool handoffAfterPlayback,
        [FromForm] string? handoffExtension,
        [FromForm] string? handoffReason,
        [FromForm] Guid? conversationId,
        [FromForm] string? conversationJson,
        CancellationToken ct)
    {
        var session = await _db.CallSessions.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == callSessionId && x.TenantId == TenantId && !x.IsDeleted, ct);
        if (session is null) return NotFound();
        if (session.EndedAt is not null) return Conflict(new { message = "The call has already ended." });
        if (session.RecordingId != recordingId) return Conflict(new { message = "The recording is no longer current." });
        if (audio.Length == 0 || audio.Length > 5 * 1024 * 1024)
            return BadRequest(new { message = "A non-empty WAV response under 5 MB is required." });

        var resolvedHandoffExtension = handoffAfterPlayback
            ? await ResolveHandoffExtensionAsync(handoffExtension, ct)
            : null;
        if (handoffAfterPlayback && resolvedHandoffExtension is null)
            return Conflict(new { message = "No available human agent is configured for this tenant." });

        var responseName = $"ccaas-{callSessionId:N}-ai-{recordingId:N}";
        var objectKey = $"{TenantId:N}/calls/{callSessionId:N}/responses/{responseName}.wav";
        await using var stream = audio.OpenReadStream();
        await _storage.UploadAsync(Bucket, objectKey, stream, "audio/wav", ct);

        var fullTranscript = handoffAfterPlayback
            ? BuildTranscript(conversationJson, transcript, responseText) : string.Empty;
        var normalizedReason = string.IsNullOrWhiteSpace(handoffReason)
            ? "Caller requested a human agent."
            : handoffReason[..Math.Min(handoffReason.Length, 500)];

        if (handoffAfterPlayback)
        {
            var bangla = fullTranscript.Any(x => x is >= '\u0980' and <= '\u09FF');
            await _calls.RecordEventAsync(TenantId, callSessionId, "HumanHandoffContextPrepared",
                JsonSerializer.Serialize(new
                {
                    conversationId,
                    session.FromNumber,
                    session.ToNumber,
                    handoffReason = normalizedReason,
                    handoffExtension = resolvedHandoffExtension,
                    summary = transcript[..Math.Min(transcript.Length, 700)],
                    customerIntent = normalizedReason,
                    detectedLanguage = bangla ? "bn-BD" : "en-US",
                    sentiment = "pending",
                    collectedDetails = Array.Empty<string>(),
                    unresolvedItems = new[] { "Local AI summary is being prepared." },
                    suggestedOpening = bangla
                        ? "আপনার আগের কথোপকথনের তথ্য আমার সামনে আছে—আমি এখান থেকেই আপনাকে সাহায্য করছি।"
                        : "I have your earlier conversation in front of me, so I can continue from here.",
                    provider = "local-summary-preparing",
                    usedFallback = true,
                    contextStage = "preliminary",
                    transcript = fullTranscript[..Math.Min(fullTranscript.Length, 12_000)],
                    preparedAtUtc = DateTime.UtcNow
                }), ct);
        }

        // Queue the hold message immediately. The worker polls this event independently, so
        // a cold local model can prepare richer context without adding silence before transfer.
        await _calls.RecordEventAsync(TenantId, callSessionId, "VoiceResponseSubmitted",
            JsonSerializer.Serialize(new
            {
                recordingId,
                responseName,
                objectKey,
                endCall,
                handoffAfterPlayback,
                handoffExtension = resolvedHandoffExtension,
                handoffReason = handoffAfterPlayback ? normalizedReason : null,
                transcript = transcript[..Math.Min(transcript.Length, 2000)],
                responseText = responseText[..Math.Min(responseText.Length, 2000)]
            }), ct);

        if (handoffAfterPlayback)
        {
            var context = await _handoffSummarizer.SummarizeAsync(
                TenantId, fullTranscript, normalizedReason, ct);
            await _calls.RecordEventAsync(TenantId, callSessionId, "HumanHandoffContextPrepared",
                JsonSerializer.Serialize(new
                {
                    conversationId,
                    session.FromNumber,
                    session.ToNumber,
                    handoffReason = normalizedReason,
                    handoffExtension = resolvedHandoffExtension,
                    summary = context.Summary,
                    customerIntent = context.CustomerIntent,
                    detectedLanguage = context.DetectedLanguage,
                    sentiment = context.Sentiment,
                    collectedDetails = context.CollectedDetails,
                    unresolvedItems = context.UnresolvedItems,
                    suggestedOpening = context.SuggestedOpening,
                    provider = context.Provider,
                    usedFallback = context.UsedFallback,
                    contextStage = "final",
                    transcript = fullTranscript[..Math.Min(fullTranscript.Length, 12_000)],
                    preparedAtUtc = DateTime.UtcNow
                }), ct);

            if (conversationId is not null)
            {
                var conversation = await _db.AiConversations.IgnoreQueryFilters()
                    .SingleOrDefaultAsync(x => x.Id == conversationId && x.TenantId == TenantId && !x.IsDeleted, ct);
                if (conversation is not null)
                {
                    conversation.CallSessionId = callSessionId;
                    conversation.Transcript = fullTranscript;
                    conversation.Summary = context.Summary;
                    conversation.Intent = context.CustomerIntent;
                    conversation.DetectedLanguage = context.DetectedLanguage;
                    conversation.Sentiment = context.Sentiment;
                    conversation.HandoffReason = normalizedReason;
                    conversation.SuggestedFollowUp = string.Join("; ", context.UnresolvedItems);
                    conversation.Status = AiConversationStatus.HandoffRequested;
                    conversation.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
            }
        }
        return Accepted(new { callSessionId, recordingId, responseName, status = "queued-for-asterisk" });
    }

    private static string BuildTranscript(string? conversationJson, string latestTranscript, string responseText)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(conversationJson))
        {
            try
            {
                using var document = JsonDocument.Parse(conversationJson);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in document.RootElement.EnumerateArray().Take(80))
                    {
                        var role = item.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : null;
                        var content = item.TryGetProperty("content", out var contentValue) ? contentValue.GetString() : null;
                        if (string.IsNullOrWhiteSpace(content)) continue;
                        var speaker = role switch { "user" => "Customer", "assistant" => "AI", _ => "System" };
                        lines.Add($"{speaker}: {content.Trim()}");
                    }
                }
            }
            catch (JsonException) { /* The current turn below still produces a safe context. */ }
        }
        if (lines.Count == 0 || !lines[^1].Contains(latestTranscript, StringComparison.Ordinal))
            lines.Add($"Customer: {latestTranscript.Trim()}");
        if (!string.IsNullOrWhiteSpace(responseText)) lines.Add($"AI: {responseText.Trim()}");
        var result = string.Join('\n', lines);
        return result[..Math.Min(result.Length, 16_000)];
    }

    private async Task<string?> ResolveHandoffExtensionAsync(string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var normalized = requested.Trim();
            var configured = await _db.Extensions.AsNoTracking()
                .AnyAsync(x => x.ExtensionNumber == normalized, ct);
            if (configured) return normalized;
        }

        var selected = await (from member in _db.QueueMembers.AsNoTracking()
                              join agent in _db.Agents.AsNoTracking() on member.AgentId equals agent.Id
                              join extension in _db.Extensions.AsNoTracking() on agent.Id equals extension.AgentId
                              where member.IsActive && agent.Presence == AgentPresence.Available
                              orderby member.Priority, member.Penalty, agent.PresenceChangedAt
                              select extension.ExtensionNumber).FirstOrDefaultAsync(ct);
        if (selected is not null) return selected;

        return await _db.SystemSettings.AsNoTracking()
            .Where(x => x.Category == "Telephony" && x.Key == "FallbackHandoffExtension" && x.IsActive)
            .Select(x => x.Value).FirstOrDefaultAsync(ct);
    }

    [HttpPost("calls/{callSessionId:guid}/hangup")]
    public async Task<IActionResult> RequestHangup(Guid callSessionId,
        [FromBody] HangupRequest request, CancellationToken ct)
    {
        var session = await _db.CallSessions.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == callSessionId && x.TenantId == TenantId && !x.IsDeleted, ct);
        if (session is null) return NotFound();
        if (session.EndedAt is not null) return Ok(new { callSessionId, status = "already-ended" });

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Voice bridge requested hangup."
            : request.Reason[..Math.Min(request.Reason.Length, 500)];
        await _calls.RecordEventAsync(TenantId, callSessionId, "VoiceBridgeHangupRequested",
            JsonSerializer.Serialize(new { reason }), ct);
        return Accepted(new { callSessionId, status = "hangup-requested" });
    }

    public sealed record HangupRequest(string Reason);
}

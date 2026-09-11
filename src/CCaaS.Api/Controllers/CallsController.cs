using CCaaS.Application.Calls;
using CCaaS.Domain.Crm;
using CCaaS.Infrastructure.ObjectStorage;
using CCaaS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CCaaS.Api.Controllers;

[Authorize(Roles = "Agent,Supervisor,TenantAdmin,PlatformAdmin")]
[Route("api/calls")]
public class CallsController : ApiControllerBase
{
    private readonly ICallService _callService;
    private readonly CcaasDbContext _db;
    private readonly IObjectStorageService _storage;

    public CallsController(ICallService callService, CcaasDbContext db, IObjectStorageService storage)
    { _callService = callService; _db = db; _storage = storage; }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 100, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 250);
        var calls = await _db.CallSessions.IgnoreQueryFilters()
            .Where(x => x.TenantId == TenantId && !x.IsDeleted)
            .OrderByDescending(x => x.StartedAt).Take(take).AsNoTracking()
            .Select(x => new { x.Id, direction = x.Direction.ToString(), x.FromNumber, x.ToNumber,
                x.StartedAt, x.AnsweredAt, x.EndedAt, status = x.Status.ToString(), x.HangupCause,
                durationSeconds = x.EndedAt == null ? (int?)(DateTime.UtcNow - x.StartedAt).TotalSeconds : (int?)(x.EndedAt.Value - x.StartedAt).TotalSeconds,
                x.RecordingId }).ToListAsync(ct);
        return Ok(calls);
    }

    [HttpGet("handoffs")]
    public async Task<IActionResult> Handoffs([FromQuery] int take = 50, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 100);
        // A worker restart can observe an already persisted response event again, and a
        // handoff has both preliminary and final context events. Return one stable card
        // per CallSessionId and always attach the newest context deterministically.
        var startedCandidates = await _db.CallEvents.IgnoreQueryFilters()
            .Where(x => x.TenantId == TenantId && !x.IsDeleted
                        && x.EventType == "HumanHandoffStarted")
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Take(Math.Max(take * 10, 100))
            .Select(x => new { x.Id, x.CallSessionId, x.OccurredAt, x.PayloadJson })
            .AsNoTracking().ToListAsync(ct);

        var started = startedCandidates
            .GroupBy(x => x.CallSessionId)
            .Select(x => x.OrderByDescending(y => y.OccurredAt).ThenByDescending(y => y.Id).First())
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Take(take).ToList();
        var callIds = started.Select(x => x.CallSessionId).ToList();

        var calls = await _db.CallSessions.IgnoreQueryFilters()
            .Where(x => callIds.Contains(x.Id) && x.TenantId == TenantId && !x.IsDeleted)
            .AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var contextCandidates = await _db.CallEvents.IgnoreQueryFilters()
            .Where(x => callIds.Contains(x.CallSessionId) && x.TenantId == TenantId
                        && !x.IsDeleted && x.EventType == "HumanHandoffContextPrepared")
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Select(x => new { x.Id, x.CallSessionId, x.OccurredAt, x.PayloadJson })
            .AsNoTracking().ToListAsync(ct);
        var contexts = contextCandidates.GroupBy(x => x.CallSessionId)
            .ToDictionary(x => x.Key,
                x => x.OrderByDescending(y => y.OccurredAt).ThenByDescending(y => y.Id)
                    .First().PayloadJson);
        var fullRecordingEvents = await _db.CallEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(x => callIds.Contains(x.CallSessionId) && x.TenantId == TenantId && !x.IsDeleted
                        && x.EventType == "FullCallRecordingReady")
            .OrderByDescending(x => x.OccurredAt).ToListAsync(ct);
        var fullRecordings = fullRecordingEvents.GroupBy(x => x.CallSessionId)
            .ToDictionary(x => x.Key, x => TryReadRecordingId(x.First().PayloadJson));
        var outcomeEvents = await _db.CallEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(x => callIds.Contains(x.CallSessionId) && x.TenantId == TenantId && !x.IsDeleted
                && (x.EventType == "HumanHandoffAttemptFailed" || x.EventType == "HumanHandoffFailed"
                    || x.EventType == "HumanHandoffCompleted" || x.EventType == "HumanHandoffDispatchFailed"))
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id).ToListAsync(ct);
        var outcomes = outcomeEvents.GroupBy(x => x.CallSessionId)
            .ToDictionary(x => x.Key, x =>
            {
                var latest = x.First();
                return new { latest.EventType, latest.PayloadJson, latest.OccurredAt };
            });

        var items = new List<object>();
        foreach (var handoff in started)
        {
            if (!calls.TryGetValue(handoff.CallSessionId, out var call)) continue;
            contexts.TryGetValue(handoff.CallSessionId, out var contextJson);
            fullRecordings.TryGetValue(handoff.CallSessionId, out var fullRecordingId);
            outcomes.TryGetValue(handoff.CallSessionId, out var handoffOutcome);
            items.Add(new
            {
                id = handoff.CallSessionId, // stable Angular tracking key across every poll
                callSessionId = handoff.CallSessionId,
                call.FromNumber,
                call.ToNumber,
                occurredAt = handoff.OccurredAt,
                payloadJson = handoff.PayloadJson,
                contextJson,
                callStatus = call.Status.ToString(),
                call.EndedAt,
                call.CustomerId,
                call.DispositionId,
                fullRecordingId,
                handoffOutcome
            });
        }
        return Ok(items);
    }

    [HttpGet("{callSessionId:guid}")]
    public async Task<IActionResult> Details(Guid callSessionId, CancellationToken ct)
    {
        var call = await _db.CallSessions.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == callSessionId && x.TenantId == TenantId && !x.IsDeleted, ct);
        if (call is null) return NotFound();
        var events = await _db.CallEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.CallSessionId == callSessionId && x.TenantId == TenantId && !x.IsDeleted)
            .OrderBy(x => x.OccurredAt)
            .Select(x => new { x.Id, x.EventType, x.OccurredAt, x.PayloadJson }).ToListAsync(ct);
        var recordings = await _db.Recordings.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.CallSessionId == callSessionId && x.TenantId == TenantId && !x.IsDeleted)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { x.Id, x.DurationSeconds, x.SizeBytes, x.CreatedAt }).ToListAsync(ct);
        return Ok(new { call, events, recordings });
    }

    [HttpGet("recordings/{recordingId:guid}")]
    public async Task<IActionResult> Recording(Guid recordingId, CancellationToken ct)
    {
        var recording = await _db.Recordings.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == recordingId && x.TenantId == TenantId && !x.IsDeleted, ct);
        if (recording is null) return NotFound();
        var stream = await _storage.DownloadAsync("call-recordings", recording.ObjectStorageKey, ct);
        return File(stream, "audio/wav", $"call-{recording.CallSessionId:N}-{recording.Id:N}.wav");
    }

    [HttpGet("recordings/{recordingId:guid}/url")]
    public async Task<IActionResult> RecordingUrl(Guid recordingId, CancellationToken ct)
    {
        var recording = await _db.Recordings.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == recordingId && x.TenantId == TenantId && !x.IsDeleted, ct);
        if (recording is null) return NotFound();
        var url = await _storage.GetPresignedUrlAsync("call-recordings", recording.ObjectStorageKey, TimeSpan.FromMinutes(15), ct);
        return Ok(new { url, expiresInSeconds = 900 });
    }

    /// <summary>Outbound call flow steps 1-4 (Section 8): agent clicks Call -> validated here -> dispatched to the Telephony worker.</summary>
    [HttpPost("outbound")]
    public async Task<IActionResult> InitiateOutbound([FromBody] InitiateOutboundCallRequest request, CancellationToken ct)
        => Ok(await _callService.InitiateOutboundCallAsync(TenantId, request, ct));

    [HttpPost("{callSessionId:guid}/events")]
    public async Task<IActionResult> RecordEvent(Guid callSessionId, [FromQuery] string eventType, [FromBody] string? payloadJson, CancellationToken ct)
    {
        await _callService.RecordEventAsync(TenantId, callSessionId, eventType, payloadJson, ct);
        return NoContent();
    }

    [HttpPost("{callSessionId:guid}/end")]
    public async Task<IActionResult> EndCall(Guid callSessionId, [FromQuery] string hangupCause, CancellationToken ct)
    {
        await _callService.EndCallAsync(TenantId, callSessionId, hangupCause, ct);
        return NoContent();
    }

    [HttpPut("{callSessionId:guid}/disposition")]
    public async Task<IActionResult> SetDisposition(Guid callSessionId, [FromQuery] Guid dispositionId, CancellationToken ct)
    {
        var exists = await _db.Dispositions.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.Id == dispositionId && x.TenantId == TenantId && !x.IsDeleted, ct);
        if (!exists) return BadRequest(new { message = "Invalid disposition for this tenant." });
        await _callService.SetDispositionAsync(TenantId, callSessionId, dispositionId, ct);
        await _callService.RecordEventAsync(TenantId, callSessionId, "DispositionSubmitted",
            System.Text.Json.JsonSerializer.Serialize(new { dispositionId }), ct);
        var session = await _db.CallSessions.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == callSessionId && x.TenantId == TenantId && !x.IsDeleted, ct);
        if (session.AgentId is not null)
        {
            var agent = await _db.Agents.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == session.AgentId
                && x.TenantId == TenantId && !x.IsDeleted, ct);
            if (agent is not null)
            {
                agent.Presence = CCaaS.Domain.Organization.AgentPresence.Available;
                agent.PresenceChangedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
        }
        return NoContent();
    }

    [HttpGet("dispositions")]
    public async Task<IActionResult> Dispositions(CancellationToken ct) => Ok(await _db.Dispositions
        .AsNoTracking().OrderBy(x => x.Description)
        .Select(x => new { x.Id, x.Code, x.Description, x.RequiresFollowUp }).ToListAsync(ct));

    [HttpPost("{callSessionId:guid}/notes")]
    public async Task<IActionResult> AddNote(Guid callSessionId, [FromBody] CallNoteRequest request, CancellationToken ct)
    {
        var body = request.Body?.Trim();
        if (string.IsNullOrWhiteSpace(body) || body.Length > 2000)
            return BadRequest(new { message = "Note must contain 1 to 2000 characters." });
        var call = await _db.CallSessions.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == callSessionId && x.TenantId == TenantId && !x.IsDeleted, ct);
        if (call is null) return NotFound();
        var userId = CurrentUserId;
        var agentId = await _db.Agents.IgnoreQueryFilters().Where(x => x.TenantId == TenantId && x.UserId == userId)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (call.CustomerId is not null && agentId is not null)
            _db.Notes.Add(new Note { TenantId = TenantId, CustomerId = call.CustomerId.Value,
                AuthorAgentId = agentId.Value, Body = body });
        await _callService.RecordEventAsync(TenantId, callSessionId, "AgentNoteAdded",
            System.Text.Json.JsonSerializer.Serialize(new { body, agentId, userId }), ct);
        return NoContent();
    }

    [HttpGet("{callSessionId:guid}/customer-360")]
    public async Task<IActionResult> Customer360(Guid callSessionId, CancellationToken ct)
    {
        var call = await _db.CallSessions.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == callSessionId && x.TenantId == TenantId && !x.IsDeleted, ct);
        if (call is null) return NotFound();
        var customer = call.CustomerId is null ? null : await _db.Customers.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == call.CustomerId && x.TenantId == TenantId && !x.IsDeleted, ct);
        var customerId = customer?.Id;
        var notes = await _db.Notes.IgnoreQueryFilters().AsNoTracking()
            .Where(x => customerId != null && x.CustomerId == customerId && x.TenantId == TenantId && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt).Take(20)
            .Select(x => new { x.Id, x.Body, x.AuthorAgentId, x.CreatedAt }).ToListAsync(ct);
        var history = await _db.CallSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == TenantId && !x.IsDeleted && x.Id != call.Id
                && (call.CustomerId != null ? x.CustomerId == call.CustomerId : x.FromNumber == call.FromNumber))
            .OrderByDescending(x => x.StartedAt).Take(10)
            .Select(x => new { x.Id, x.StartedAt, x.EndedAt, status = x.Status.ToString(), x.DispositionId }).ToListAsync(ct);
        // The two branches of this conditional must produce the SAME anonymous type, including
        // nullability. CallSession.FromNumber is non-nullable and Customer.Phone is nullable,
        // so without the cast the first branch types 'phone' as string and the second as
        // string?, and the compiler reports CS8619. That is a nullability warning, which a
        // local Debug build prints and walks past - but CI builds with --warnaserror, so it
        // fails there instead. Do not "tidy" the cast away.
        return Ok(new { customer = customer is null
                ? new { id = (Guid?)null, name = $"Caller {call.FromNumber}", phone = (string?)call.FromNumber, email = (string?)null }
                : new { id = (Guid?)customer.Id, name = customer.Name, phone = customer.Phone, email = customer.Email },
            notes, history });
    }

    [Authorize(Roles = "Supervisor,TenantAdmin,PlatformAdmin")]
    [HttpGet("monitoring/overview")]
    public async Task<IActionResult> Monitoring(CancellationToken ct)
    {
        var since = DateTime.UtcNow.Date;
        var calls = _db.CallSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == TenantId && !x.IsDeleted && x.StartedAt >= since);
        var active = await calls.CountAsync(x => x.EndedAt == null, ct);
        var completed = await calls.CountAsync(x => x.EndedAt != null, ct);
        var failed = await calls.CountAsync(x => x.Status == CCaaS.Domain.Calls.CallStatus.Failed
            || x.Status == CCaaS.Domain.Calls.CallStatus.Abandoned, ct);
        var handoffs = await _db.CallEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == TenantId && !x.IsDeleted && x.OccurredAt >= since && x.EventType == "HumanHandoffStarted")
            .Select(x => x.CallSessionId).Distinct().CountAsync(ct);
        var agents = await _db.Agents.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == TenantId && !x.IsDeleted)
            .GroupBy(x => x.Presence).Select(x => new { presence = x.Key.ToString(), count = x.Count() }).ToListAsync(ct);
        var wrapUp = agents.Where(x => x.presence == "WrapUp").Sum(x => x.count);
        var recentErrors = await _db.CallEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == TenantId && !x.IsDeleted && x.OccurredAt >= since
                && (x.EventType == "HumanHandoffAttemptFailed" || x.EventType == "HumanHandoffFailed"
                    || x.EventType == "HumanHandoffDispatchFailed" || x.EventType == "RecordingFailed"
                    || x.EventType == "StaleCallReconciled"))
            .OrderByDescending(x => x.OccurredAt).Take(20)
            .Select(x => new { x.Id, x.CallSessionId, x.EventType, x.OccurredAt, x.PayloadJson }).ToListAsync(ct);
        var telephonyNodes = await _db.AsteriskNodes.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == TenantId && !x.IsDeleted)
            .Select(x => new { x.Id, x.Name, x.IsHealthy, x.LastHeartbeatAt }).ToListAsync(ct);
        return Ok(new { generatedAtUtc = DateTime.UtcNow, active, completed, failed, handoffs,
            wrapUp, agents, telephonyNodes, recentErrors });
    }

    private Guid CurrentUserId => Guid.TryParse(User.FindFirst("sub")?.Value
        ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id)
        ? id : throw new UnauthorizedAccessException("Missing user identity claim.");

    private static Guid? TryReadRecordingId(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(payload);
            return json.RootElement.TryGetProperty("recordingId", out var value) && value.TryGetGuid(out var id) ? id : null;
        }
        catch { return null; }
    }

    public sealed record CallNoteRequest(string Body);
}

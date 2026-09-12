using CCaaS.Application.Common;
using CCaaS.Domain.Calls;

namespace CCaaS.Application.Calls;

public record InitiateOutboundCallRequest(Guid AgentId, string FromNumber, string ToNumber,
    Guid? CustomerId, Guid? LeadId, Guid? CampaignId, Guid? CampaignLeadId = null,
    int? AttemptNumber = null);

public static class CallLifecycleClassifier
{
    public static string NormalizeDialStatus(string? value) => (value ?? "UNKNOWN").Trim().ToUpperInvariant() switch
    {
        "ANSWER" => "Answered",
        "BUSY" => "Busy",
        "NOANSWER" => "NoAnswer",
        "CANCEL" => "Cancelled",
        "CHANUNAVAIL" => "Unavailable",
        "CONGESTION" => "Congestion",
        "DONTCALL" => "Rejected",
        "TORTURE" => "Rejected",
        _ => "Failed"
    };

    public static bool WasAnswered(string? value) =>
        string.Equals((value ?? "").Trim(), "ANSWER", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Implemented in the Telephony worker (Section 6/8): translates an application-level
/// "place this call" request into an ARI originate command against Asterisk.
/// Kept as an interface here so Application never references the ARI client library directly.
/// </summary>
public interface ITelephonyDispatcher
{
    Task OriginateCallAsync(Guid callSessionId, string fromNumber, string toNumber, CancellationToken ct = default);
}

public interface ICallService
{
    /// <summary>Outbound call flow step 1-3 (Section 8): agent clicks call -> .NET API validates -> command sent to worker.</summary>
    Task<CallSession> InitiateOutboundCallAsync(Guid tenantId, InitiateOutboundCallRequest request, CancellationToken ct = default);

    /// <summary>Inbound call flow step 11-12 (Section 8): telephony events normalized, CallSession opened.</summary>
    Task<CallSession> OpenInboundCallAsync(Guid tenantId, string externalCallId, string fromNumber, string toNumber, Guid? customerId, CancellationToken ct = default);

    Task RecordEventAsync(Guid tenantId, Guid callSessionId, string eventType, string? payloadJson, CancellationToken ct = default);
    Task<Recording> AddRecordingAsync(Guid tenantId, Guid callSessionId, string objectStorageKey,
        int durationSeconds, long sizeBytes, CancellationToken ct = default);
    Task EndCallAsync(Guid tenantId, Guid callSessionId, string hangupCause, CancellationToken ct = default);
    Task SetDispositionAsync(Guid tenantId, Guid callSessionId, Guid dispositionId, CancellationToken ct = default);
}

public class CallService : ICallService
{
    private readonly IRepository<CallSession> _callSessions;
    private readonly IRepository<CallEvent> _callEvents;
    private readonly IRepository<Recording> _recordings;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITelephonyDispatcher _telephonyDispatcher;

    public CallService(IRepository<CallSession> callSessions, IRepository<CallEvent> callEvents,
        IRepository<Recording> recordings, IUnitOfWork unitOfWork, ITelephonyDispatcher telephonyDispatcher)
    {
        _callSessions = callSessions;
        _callEvents = callEvents;
        _recordings = recordings;
        _unitOfWork = unitOfWork;
        _telephonyDispatcher = telephonyDispatcher;
    }

    public async Task<Recording> AddRecordingAsync(Guid tenantId, Guid callSessionId,
        string objectStorageKey, int durationSeconds, long sizeBytes, CancellationToken ct = default)
    {
        var session = await _callSessions.FirstOrDefaultAsync(
            x => x.Id == callSessionId && x.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Call session not found.");

        var recording = new Recording
        {
            TenantId = tenantId,
            CallSessionId = callSessionId,
            ObjectStorageKey = objectStorageKey,
            DurationSeconds = Math.Max(0, durationSeconds),
            SizeBytes = Math.Max(0, sizeBytes),
            RetainUntil = DateTime.UtcNow.AddDays(30)
        };
        await _recordings.AddAsync(recording, ct);
        session.RecordingId = recording.Id;
        _callSessions.Update(session);
        await _unitOfWork.SaveChangesAsync(ct);
        return recording;
    }

    public async Task<CallSession> InitiateOutboundCallAsync(Guid tenantId, InitiateOutboundCallRequest request, CancellationToken ct = default)
    {
        string? idempotencyKey = null;
        if (request.CampaignId is not null)
        {
            if (request.CampaignId == Guid.Empty
                || !request.LeadId.HasValue || request.LeadId.Value == Guid.Empty
                || !request.CampaignLeadId.HasValue || request.CampaignLeadId.Value == Guid.Empty
                || !request.AttemptNumber.HasValue || request.AttemptNumber.Value <= 0)
                throw new ArgumentException(
                    "Campaign calls require valid campaign, campaign-lead, lead and attempt identifiers.");

            // CampaignLead.LeadId is the campaign contact identifier in the current model.
            idempotencyKey = CallIdempotency.Derive(tenantId, request.CampaignId.Value,
                request.LeadId.Value, request.AttemptNumber.Value);
            var existing = await _callSessions.FirstOrDefaultAsync(x =>
                x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
                return existing;
        }

        var session = new CallSession
        {
            TenantId = tenantId,
            Direction = CallDirection.Outbound,
            AgentId = request.AgentId,
            CustomerId = request.CustomerId,
            LeadId = request.LeadId,
            CampaignId = request.CampaignId,
            CampaignLeadId = request.CampaignLeadId,
            IdempotencyKey = idempotencyKey,
            FromNumber = request.FromNumber,
            ToNumber = request.ToNumber,
            Status = CallStatus.Ringing
        };
        await _callSessions.AddAsync(session, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        // Step 3-4 (Section 8): telephony command handed to the worker, which owns ARI.
        await _telephonyDispatcher.OriginateCallAsync(session.Id, request.FromNumber, request.ToNumber, ct);
        return session;
    }

    public async Task<CallSession> OpenInboundCallAsync(Guid tenantId, string externalCallId, string fromNumber, string toNumber, Guid? customerId, CancellationToken ct = default)
    {
        var session = new CallSession
        {
            TenantId = tenantId,
            Direction = CallDirection.Inbound,
            ExternalCallId = externalCallId,
            FromNumber = fromNumber,
            ToNumber = toNumber,
            CustomerId = customerId,
            Status = CallStatus.Ringing
        };
        await _callSessions.AddAsync(session, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return session;
    }

    public async Task RecordEventAsync(Guid tenantId, Guid callSessionId, string eventType, string? payloadJson, CancellationToken ct = default)
    {
        await _callEvents.AddAsync(new CallEvent
        {
            TenantId = tenantId,
            CallSessionId = callSessionId,
            EventType = eventType,
            PayloadJson = payloadJson
        }, ct);

        var session = await _callSessions.FirstOrDefaultAsync(c => c.Id == callSessionId && c.TenantId == tenantId, ct);
        if (session is not null)
        {
            switch (eventType)
            {
                case "Answered": session.AnsweredAt = DateTime.UtcNow; session.Status = CallStatus.Answered; break;
                case "Hold": session.Status = CallStatus.OnHold; break;
                case "HumanHandoffStarted": session.Status = CallStatus.Transferring; break;
                case "HumanHandoffDialplanEntered": session.Status = CallStatus.Transferring; break;
                case "HumanHandoffCompleted": session.Status = CallStatus.Answered; break;
                case "HumanHandoffFailed": session.Status = CallStatus.Failed; break;
                case "StaleCallReconciled": session.Status = CallStatus.Abandoned; break;
            }
            _callSessions.Update(session);
        }

        await _unitOfWork.SaveChangesAsync(ct);
    }

    public async Task EndCallAsync(Guid tenantId, Guid callSessionId, string hangupCause, CancellationToken ct = default)
    {
        var session = await _callSessions.FirstOrDefaultAsync(c => c.Id == callSessionId && c.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Call session not found.");

        // ARI can emit StasisEnd, ChannelHangupRequest and ChannelDestroyed for the same
        // physical call. Ending an already-ended session must therefore be idempotent.
        if (session.EndedAt is not null) return;
        session.EndedAt = DateTime.UtcNow;
        session.HangupCause = hangupCause;
        if (session.Status is not (CallStatus.Failed or CallStatus.Abandoned))
            session.Status = CallStatus.Ended;
        if (session.AnsweredAt is not null)
            session.TalkDurationSeconds = (int)(session.EndedAt.Value - session.AnsweredAt.Value).TotalSeconds;

        _callSessions.Update(session);
        await _unitOfWork.SaveChangesAsync(ct);
    }

    public async Task SetDispositionAsync(Guid tenantId, Guid callSessionId, Guid dispositionId, CancellationToken ct = default)
    {
        var session = await _callSessions.FirstOrDefaultAsync(c => c.Id == callSessionId && c.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Call session not found.");

        session.DispositionId = dispositionId;
        _callSessions.Update(session);
        await _unitOfWork.SaveChangesAsync(ct);
    }
}

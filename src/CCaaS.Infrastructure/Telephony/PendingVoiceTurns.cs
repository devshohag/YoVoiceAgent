using CCaaS.Domain.Calls;

namespace CCaaS.Infrastructure.Telephony;

public static class PendingVoiceTurns
{
    // Apply before Take: completed events must not occupy the candidate window.
    // Legacy markers used N-format strings; System.Text.Json serializes Guid as D-format.
    public static IQueryable<CallEvent> Query(IQueryable<CallEvent> events, IQueryable<CallSession> calls)
        => events.Where(source => !source.IsDeleted && source.EventType == "AwaitingVoiceBridge"
            && calls.Any(call => call.Id == source.CallSessionId && call.TenantId == source.TenantId
                && !call.IsDeleted && call.EndedAt == null)
            && !events.Any(done => !done.IsDeleted && done.TenantId == source.TenantId
                && done.CallSessionId == source.CallSessionId && done.EventType == "LocalAiTurnProcessed"
                && done.PayloadJson != null
                && done.PayloadJson.Replace("-", "").Contains(source.Id.ToString().Replace("-", ""))));
}

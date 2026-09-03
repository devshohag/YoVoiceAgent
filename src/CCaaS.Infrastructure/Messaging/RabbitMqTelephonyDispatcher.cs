using CCaaS.Application.Calls;

namespace CCaaS.Infrastructure.Messaging;

/// <summary>
/// Implements Application's ITelephonyDispatcher by publishing a command event that the
/// CCaaS.Workers.Telephony process consumes and turns into an ARI originate call
/// (Section 8 outbound flow, steps 3-4: ".NET API validates... Telephony command is sent to
/// the Telephony Worker... Worker uses ARI to originate/control the call in Asterisk.").
/// This is a command (RPC-like intent), not a domain event, so it is published directly
/// rather than through the transactional outbox - it does not need "exactly once on commit"
/// semantics the same way billing/notification events do, since call origination is already
/// idempotent by CallSessionId in the worker.
/// </summary>
public class RabbitMqTelephonyDispatcher : ITelephonyDispatcher
{
    private readonly IEventBus _eventBus;

    public RabbitMqTelephonyDispatcher(IEventBus eventBus) => _eventBus = eventBus;

    public Task OriginateCallAsync(Guid callSessionId, string fromNumber, string toNumber, CancellationToken ct = default)
        => _eventBus.PublishAsync("CallOriginateRequested", new { callSessionId, fromNumber, toNumber }, ct);
}

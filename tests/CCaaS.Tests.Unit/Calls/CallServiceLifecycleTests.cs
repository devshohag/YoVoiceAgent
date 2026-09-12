using CCaaS.Application.Calls;
using CCaaS.Domain.Calls;
using CCaaS.Tests.Unit.Fakes;

namespace CCaaS.Tests.Unit.Calls;

public class CallServiceLifecycleTests
{
    [Fact]
    public async Task Handoff_events_move_session_through_transfer_and_answered()
    {
        var sessions = new FakeRepository<CallSession>();
        var events = new FakeRepository<CallEvent>();
        var service = Create(sessions, events);
        var session = new CallSession { TenantId = Guid.NewGuid(), FromNumber = "1001", ToNumber = "7000" };
        sessions.Items.Add(session);

        await service.RecordEventAsync(session.TenantId, session.Id, "HumanHandoffStarted", null);
        Assert.Equal(CallStatus.Transferring, session.Status);
        await service.RecordEventAsync(session.TenantId, session.Id, "HumanHandoffCompleted", null);
        Assert.Equal(CallStatus.Answered, session.Status);
    }

    [Fact]
    public async Task EndCall_is_idempotent_for_duplicate_ari_events()
    {
        var sessions = new FakeRepository<CallSession>();
        var service = Create(sessions, new FakeRepository<CallEvent>());
        var session = new CallSession { TenantId = Guid.NewGuid(), FromNumber = "1001", ToNumber = "7000", AnsweredAt = DateTime.UtcNow.AddMinutes(-1) };
        sessions.Items.Add(session);

        await service.EndCallAsync(session.TenantId, session.Id, "Normal Clearing");
        var endedAt = session.EndedAt;
        await service.EndCallAsync(session.TenantId, session.Id, "ChannelDestroyed");
        Assert.Equal(endedAt, session.EndedAt);
        Assert.Equal("Normal Clearing", session.HangupCause);
    }

    [Theory]
    [InlineData("ANSWER", "Answered")]
    [InlineData("BUSY", "Busy")]
    [InlineData("NOANSWER", "NoAnswer")]
    [InlineData("CHANUNAVAIL", "Unavailable")]
    [InlineData("DONTCALL", "Rejected")]
    [InlineData(null, "Failed")]
    public void Dial_status_is_normalized(string? raw, string expected)
        => Assert.Equal(expected, CallLifecycleClassifier.NormalizeDialStatus(raw));

    [Fact]
    public void Only_answer_dial_status_is_treated_as_connected()
    {
        Assert.True(CallLifecycleClassifier.WasAnswered("ANSWER"));
        Assert.False(CallLifecycleClassifier.WasAnswered("NOANSWER"));
        Assert.False(CallLifecycleClassifier.WasAnswered("BUSY"));
    }

    [Fact]
    public void Campaign_call_key_is_stable_scoped_and_attempt_specific()
    {
        var tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var campaign = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var contact = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var key = CallIdempotency.Derive(tenant, campaign, contact, 1);

        Assert.Equal(64, key.Length);
        Assert.Equal(key, CallIdempotency.Derive(tenant, campaign, contact, 1));
        Assert.NotEqual(key, CallIdempotency.Derive(tenant, campaign, contact, 2));
        Assert.NotEqual(key, CallIdempotency.Derive(tenant, campaign, Guid.NewGuid(), 1));
    }

    [Fact]
    public async Task Retried_campaign_dial_returns_one_session_and_dispatches_once()
    {
        var sessions = new FakeRepository<CallSession>();
        var dispatcher = new RecordingDispatcher();
        var service = Create(sessions, new FakeRepository<CallEvent>(), dispatcher);
        var tenant = Guid.NewGuid();
        var request = new InitiateOutboundCallRequest(Guid.NewGuid(), "1001", "+14155552671",
            null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);

        var first = await service.InitiateOutboundCallAsync(tenant, request);
        var retry = await service.InitiateOutboundCallAsync(tenant, request);

        Assert.Same(first, retry);
        Assert.Single(sessions.Items);
        Assert.Equal(1, dispatcher.Count);
        Assert.Equal(request.CampaignLeadId, first.CampaignLeadId);
        Assert.Equal(CallIdempotency.Derive(tenant, request.CampaignId!.Value,
            request.LeadId!.Value, request.AttemptNumber!.Value), first.IdempotencyKey);
    }

    [Fact]
    public async Task New_campaign_attempt_gets_a_new_session()
    {
        var sessions = new FakeRepository<CallSession>();
        var dispatcher = new RecordingDispatcher();
        var service = Create(sessions, new FakeRepository<CallEvent>(), dispatcher);
        var tenant = Guid.NewGuid();
        var firstAttempt = new InitiateOutboundCallRequest(Guid.NewGuid(), "1001", "+14155552671",
            null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);

        await service.InitiateOutboundCallAsync(tenant, firstAttempt);
        await service.InitiateOutboundCallAsync(tenant, firstAttempt with { AttemptNumber = 2 });

        Assert.Equal(2, sessions.Items.Count);
        Assert.Equal(2, dispatcher.Count);
        Assert.NotEqual(sessions.Items[0].IdempotencyKey, sessions.Items[1].IdempotencyKey);
    }

    [Fact]
    public async Task Failed_handoff_remains_failed_when_channel_is_destroyed()
    {
        var sessions = new FakeRepository<CallSession>();
        var service = Create(sessions, new FakeRepository<CallEvent>());
        var session = new CallSession { TenantId = Guid.NewGuid(), FromNumber = "1001", ToNumber = "7000" };
        sessions.Items.Add(session);
        await service.RecordEventAsync(session.TenantId, session.Id, "HumanHandoffFailed", null);
        await service.EndCallAsync(session.TenantId, session.Id, "No available agent");
        Assert.Equal(CallStatus.Failed, session.Status);
        Assert.NotNull(session.EndedAt);
    }

    private static CallService Create(FakeRepository<CallSession> sessions, FakeRepository<CallEvent> events,
        ITelephonyDispatcher? dispatcher = null) =>
        new(sessions, events, new FakeRepository<Recording>(), new FakeUnitOfWork(), dispatcher ?? new NoopDispatcher());

    private sealed class NoopDispatcher : ITelephonyDispatcher
    { public Task OriginateCallAsync(Guid callSessionId, string fromNumber, string toNumber, CancellationToken ct = default) => Task.CompletedTask; }

    private sealed class RecordingDispatcher : ITelephonyDispatcher
    {
        public int Count { get; private set; }

        public Task OriginateCallAsync(Guid callSessionId, string fromNumber, string toNumber,
            CancellationToken ct = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}

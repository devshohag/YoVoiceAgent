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

    private static CallService Create(FakeRepository<CallSession> sessions, FakeRepository<CallEvent> events) =>
        new(sessions, events, new FakeRepository<Recording>(), new FakeUnitOfWork(), new NoopDispatcher());

    private sealed class NoopDispatcher : ITelephonyDispatcher
    { public Task OriginateCallAsync(Guid callSessionId, string fromNumber, string toNumber, CancellationToken ct = default) => Task.CompletedTask; }
}

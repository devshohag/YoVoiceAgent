using CCaaS.Application.Appointment;
using CCaaS.Application.Appointment.Voice;
using CCaaS.VoiceSimulator;
using Xunit;

public sealed class AppointmentConversationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-08T03:00:00Z");
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dhaka");
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid session = Guid.NewGuid();
    private readonly MemoryAppointments store;
    private readonly AppointmentConversation engine;
    private AppointmentConversationState? state;
    private int turn;
    public AppointmentConversationTests()
    {
        var provider = Guid.NewGuid();
        store = new(tenant, new[] {
            new AvailableAppointmentSlot(Guid.NewGuid(), provider, "A", new DateTime(2026,9,9,4,0,0,DateTimeKind.Utc), new DateTime(2026,9,9,4,30,0,DateTimeKind.Utc)),
            new AvailableAppointmentSlot(Guid.NewGuid(), provider, "A", new DateTime(2026,9,9,10,30,0,DateTimeKind.Utc), new DateTime(2026,9,9,11,0,0,DateTimeKind.Utc)) });
        engine = new(store);
    }
    private async Task<AppointmentConversationReply> Say(string input)
    {
        var reply = await engine.StepAsync(tenant, session, state, (++turn).ToString(), input, Now, Zone);
        state = reply.State; return reply;
    }
    private async Task Prepare()
    {
        await Say("আগামীকাল সকাল দশটায় appointment নিতে চাই।");
        Assert.Equal("name", state!.Stage);
        Assert.Null(state.Name); // Initial appointment utterance must not become the customer name.
        await Say("আমার নাম সোহাগ");
        await Say("01712345678");
        Assert.Equal("confirm", state!.Stage);
        Assert.Equal(0, store.Mutations);
    }
    [Fact]
    public async Task Only_explicit_confirmation_writes_and_replay_does_not_book_again()
    {
        await Prepare();
        var result = await Say("হ্যাঁ");
        Assert.Equal("confirmed", result.Action);
        Assert.Equal(1, store.Mutations);
        var replay = await engine.StepAsync(tenant, session, state, turn.ToString(), "হ্যাঁ", Now, Zone);
        Assert.Equal("replay", replay.Action);
        await Say("হ্যাঁ");
        Assert.Equal(1, store.Mutations);
    }
    [Fact]
    public async Task Correction_invalidates_previous_confirmation()
    {
        await Prepare();
        var result = await Say("না, বিকাল সাড়ে চারটায়");
        Assert.Equal("confirm", result.State.Stage);
        Assert.Equal(new TimeOnly(16,30), result.State.Time);
        Assert.Equal(0, store.Mutations);
        await Say("হ্যাঁ");
        Assert.Equal(1, store.Mutations);
    }
    [Fact]
    public async Task No_and_ambiguous_yes_do_not_mutate()
    {
        await Prepare();
        await Say("yes but change the time");
        Assert.Equal(0, store.Mutations);
        await Say("না");
        await Say("yes");
        Assert.Equal(0, store.Mutations);
    }
    [Fact]
    public async Task Slot_race_has_no_false_confirmation()
    {
        await Prepare(); store.FailNextWrite = true;
        var result = await Say("হ্যাঁ");
        Assert.Equal("conflict", result.Action);
        Assert.Equal(0, store.Mutations);
        Assert.NotEqual("done", state!.Stage);
    }
    [Fact]
    public async Task Tenant_and_session_cannot_be_swapped()
    {
        await Prepare();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.StepAsync(Guid.NewGuid(), session, state, "other", "yes", Now, Zone));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.StepAsync(tenant, Guid.NewGuid(), state, "other", "yes", Now, Zone));
    }
    [Fact]
    public async Task Garbage_asks_for_intent_without_a_model_or_booking()
    {
        var result = await Say("Aamu Agamika Sapa Bostai");
        Assert.Equal("intent", result.State.Stage);
        Assert.Equal(0, store.Mutations);
    }
    [Fact]
    public async Task Ambiguous_time_requires_am_pm()
    {
        await Say("আগামীকাল চারটায় appointment চাই");
        Assert.True(state!.AmbiguousTime);
        Assert.Equal("time", state.Stage);
        Assert.Null(state.SlotId);
    }
    [Fact]
    public async Task Cancel_and_reschedule_require_matching_contact_and_confirmation()
    {
        await Prepare(); await Say("yes");
        var reference = state!.Reference;
        state = null;
        await Say($"reschedule {reference}");
        await Say("01712345678");
        await Say("আগামীকাল বিকাল সাড়ে চারটা");
        Assert.Equal("confirm", state!.Stage);
        Assert.Equal(1, store.Mutations);
        Assert.Equal("confirmed", (await Say("yes")).Action);
        Assert.Equal(2, store.Mutations);
        state = null;
        await Say($"cancel {reference}");
        Assert.Equal("not_found", (await Say("01812345678")).Action);
        await Say("01712345678");
        Assert.Equal("confirm", state!.Stage);
        Assert.Equal(2, store.Mutations);
        Assert.Equal("cancelled", (await Say("yes")).Action);
        Assert.Equal(3, store.Mutations);
    }
    [Fact]
    public async Task Backend_timeout_never_announces_confirmation()
    {
        await Prepare(); store.TimeoutNextWrite = true;
        var result = await Say("yes");
        Assert.Equal("backend_error", result.Action);
        Assert.NotEqual("done", result.State.Stage);
        Assert.Equal(0, store.Mutations);
    }
    [Fact]
    public async Task Availability_question_does_not_become_a_booking()
    {
        var result = await Say("appointment availability tomorrow");
        Assert.Equal("availability", result.Action);
        Assert.Equal(0, store.Mutations);
    }
    [Fact]
    public async Task Language_switch_preserves_slots_but_does_not_confirm()
    {
        await Prepare();
        var slot = state!.SlotId;
        var result = await Say("give me answer in english");
        Assert.False(result.State.Bengali);
        Assert.Equal(slot, result.State.SlotId);
        Assert.Equal("confirm", result.State.Stage);
        Assert.Equal(0, store.Mutations);
    }
    [Fact]
    public async Task Local_early_morning_slot_is_found_on_previous_utc_day()
    {
        var t = Guid.NewGuid(); var sid = Guid.NewGuid();
        var slot = new AvailableAppointmentSlot(Guid.NewGuid(), Guid.NewGuid(), "A",
            new DateTime(2026,9,8,19,0,0,DateTimeKind.Utc), new DateTime(2026,9,8,19,30,0,DateTimeKind.Utc));
        var service = new MemoryAppointments(t, new[] {slot});
        var reply = await new AppointmentConversation(service).StepAsync(t, sid, null, "one",
            "আগামীকাল ভোর একটা appointment চাই", Now, Zone);
        Assert.Equal(slot.SlotId, reply.State.SlotId);
        Assert.Equal("name", reply.State.Stage);
    }
    [Fact]
    public async Task Failed_reschedule_preserves_original_booking()
    {
        await Prepare(); await Say("yes");
        var original = (await store.GetBookingsAsync(tenant)).Single().AvailabilitySlotId;
        var reference = state!.Reference; state = null;
        await Say($"reschedule {reference} 01712345678 আগামীকাল বিকাল সাড়ে চারটা");
        store.FailNextWrite = true;
        Assert.Equal("conflict", (await Say("yes")).Action);
        Assert.Equal(original, (await store.GetBookingsAsync(tenant)).Single().AvailabilitySlotId);
        Assert.Equal(1, store.Mutations);
    }

}

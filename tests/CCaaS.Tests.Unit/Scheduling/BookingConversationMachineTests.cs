using System.Globalization;
using CCaaS.Domain.Scheduling.Booking;
using CCaaS.Domain.Scheduling.DateTimeParsing;
using Xunit;

namespace CCaaS.Tests.Unit.Scheduling;

// Task 2.3 - the booking conversation state machine.
//
// WHY THESE TESTS ARE WHOLE CALLS
// -------------------------------
// A booking flow does not fail at a single transition. It fails across a call: the caller
// corrects themselves on turn four and the agent forgets the name it collected on turn three;
// the slot is taken between the offer and the commit; the caller asks for a person while
// saying "no". None of that is visible when transitions are asserted one at a time, so every
// scenario below drives a complete conversation from the greeting to a terminal stage.
//
// The machine performs no I/O, so these run at full speed with no database, no telephone and
// no model - which is the entire reason the flow logic was pulled out of the prompt.
//
// Wording is asserted separately (see BookingPhrasesTests). Here a tagged phrase bank stands
// in, so a copy edit never breaks a flow test.
public class BookingConversationMachineTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 10, 0, 0);   // Wednesday

    private static DateTimeParseContext Ctx() => DateTimeParseContext.ForBusinessHours(Now);

    /// <summary>Open 08:00-20:00, which makes a bare "eight" genuinely ambiguous.</summary>
    private static DateTimeParseContext WideCtx() =>
        new(Now, new TimeOnly(8, 0), new TimeOnly(20, 0));

    // ============================================================== the happy path

    [Fact]
    public void HappyPath_RunsFromGreetingToBooked()
    {
        var call = new Call();

        call.Expect(BookingStage.ChoosingIntent, "ASK_INTENT");

        call.Says("tomorrow at four")
            .Expect(BookingStage.CheckingAvailability, "CHECKING", "LOOKUP:2026-09-17:16:00:None");

        call.Available(Slot(16, 0), Slot(17, 0))
            .Expect(BookingStage.CollectingName, "ASK_NAME");

        call.Says("Rahim Uddin")
            .Expect(BookingStage.ConfirmingBooking, "READBACK:1600:Rahim Uddin");

        call.Says("yes that's right")
            .Expect(BookingStage.Booking, "BOOKING", "COMMIT:1600:Rahim Uddin");

        call.Committed("APT-20260917-7K2")
            .Expect(BookingStage.Booked, "CONFIRMED:APT-20260917-7K2", "END:booked");

        Assert.Equal("APT-20260917-7K2", call.State.BookingReference);
    }

    [Fact]
    public void TerminalCall_IgnoresAnythingThatArrivesLate()
    {
        // Late events happen - a duplicated transcript, a retried webhook. Acting on one after
        // the call has ended would reopen a finished booking.
        var call = new Call();
        call.Says("tomorrow at four").Available(Slot(16, 0)).Says("Karim").Says("yes")
            .Committed("APT-1");

        call.Says("actually make it five").Expect(BookingStage.Booked);
        Assert.Empty(call.LastActions);
    }

    [Fact]
    public void NothingIsEverBookedWithoutACommitComingBack()
    {
        // The single most important property in this file. "Yes" does not book anything; only
        // BookingCommitted does. A caller can never be told they have an appointment that the
        // database does not have.
        var call = new Call();
        call.Says("tomorrow at four").Available(Slot(16, 0)).Says("Karim").Says("yes");

        Assert.Equal(BookingStage.Booking, call.State.Stage);
        Assert.Null(call.State.BookingReference);
        Assert.NotEqual(BookingStage.Booked, call.State.Stage);
    }

    // ============================================================== alternatives

    [Fact]
    public void RequestedTimeTaken_OffersTheNearestSlotsAndAcceptsAChoiceByTime()
    {
        var call = new Call();
        call.Says("tomorrow at four").Available(Slot(15, 0), Slot(17, 0), Slot(11, 0))
            // Nearest to 16:00 first, and only as many as a person can hold in their head.
            .Expect(BookingStage.OfferingAlternatives, "OFFER_ALT:1500,1700");

        call.Says("the three o'clock one").Expect(BookingStage.CollectingName, "ASK_NAME");
        Assert.Equal("1500", call.State.Selected!.StartsAtLocal.ToString("HHmm"));
    }

    [Fact]
    public void AnEarlierSlotCanBeTheNearestOne()
    {
        // Regression. TimeOnly subtraction is circular - 15:30 minus 16:00 is twenty-three and
        // a half hours, not thirty minutes - so ranking by it puts every earlier slot last. The
        // agent would read out six o'clock and skip the half past three sitting right there.
        var call = new Call();

        call.Says("tomorrow at four").Available(Slot(18, 0), Slot(15, 30))
            .Expect(BookingStage.OfferingAlternatives, "OFFER_ALT:1530,1800");
    }

    [Fact]
    public void EquallyNearSlots_AreOfferedEarliestFirst()
    {
        var call = new Call();

        call.Says("tomorrow at four").Available(Slot(17, 0), Slot(15, 0), Slot(11, 0))
            .Expect(BookingStage.OfferingAlternatives, "OFFER_ALT:1500,1700");
    }

    [Fact]
    public void SingleAlternative_AcceptsABareYes()
    {
        var call = new Call();
        call.Says("tomorrow at four").Available(Slot(17, 0))
            .Expect(BookingStage.OfferingAlternatives, "OFFER_ALT:1700");

        call.Says("yes that works").Expect(BookingStage.CollectingName, "ASK_NAME");
    }

    [Fact]
    public void DifferentDayDuringAlternatives_IsANewRequestNotARefusal()
    {
        var call = new Call();
        call.Says("tomorrow at four").Available(Slot(15, 0));

        call.Says("no, what about friday at four")
            .Expect(BookingStage.CheckingAvailability, "CHECKING", "LOOKUP:2026-09-18:16:00:None");
    }

    // ============================================================== ambiguity

    [Fact]
    public void AmbiguousHour_AsksAClosedQuestionInsteadOfGuessing()
    {
        var call = new Call(WideCtx());

        call.Says("tomorrow at eight")
            .Expect(BookingStage.ResolvingAmbiguity, "ASK_AMPM:8");

        call.Says("in the morning")
            .Expect(BookingStage.CheckingAvailability, "CHECKING", "LOOKUP:2026-09-17:08:00:Morning");

        Assert.Null(call.State.AmbiguousHour);
    }

    [Fact]
    public void AmbiguousHour_AcceptsTheEveningReadingToo()
    {
        var call = new Call(WideCtx());
        call.Says("tomorrow at eight")
            .Says("evening please")
            .Expect(BookingStage.CheckingAvailability, "CHECKING", "LOOKUP:2026-09-17:20:00:Evening");
    }

    [Fact]
    public void AmbiguousHour_CanBeAbandonedForAWhollyNewTime()
    {
        // Callers do not always answer the question they were asked.
        var call = new Call(WideCtx());
        call.Says("tomorrow at eight")
            .Says("actually make it 3pm")
            .Expect(BookingStage.CheckingAvailability, "CHECKING", "LOOKUP:2026-09-17:15:00:None");
    }

    // ============================================================== the caller's overrides

    [Theory]
    [InlineData("can I talk to a real person please")]
    [InlineData("no I want to speak to someone else")]   // a "no" that is really a transfer
    [InlineData("put me through to an agent")]
    public void RequestForAHuman_IsHonouredImmediatelyFromAnyStage(string utterance)
    {
        var atTheOpen = new Call();
        atTheOpen.Says(utterance)
            .Expect(BookingStage.Transferring, "HANDOVER:CallerAsked", "TRANSFER:CallerAsked");

        var midFlow = new Call();
        midFlow.Says("tomorrow at four").Available(Slot(16, 0));
        midFlow.Says(utterance)
            .Expect(BookingStage.Transferring, "HANDOVER:CallerAsked", "TRANSFER:CallerAsked");

        Assert.Equal(HandoffReason.CallerAsked, midFlow.State.HandoffReason);
    }

    [Fact]
    public void CallerWhoDoesNotWantToBook_EndsTheCallPolitely()
    {
        var call = new Call();
        call.Says("no thanks, not interested")
            .Expect(BookingStage.Ended, "GOODBYE", "END:caller declined");
    }

    // ============================================================== the guard rails

    [Fact]
    public void TwoUselessTurns_HandTheCallToAPerson()
    {
        // Without this the agent re-asks forever, burning call minutes and goodwill. One repeat
        // is a mishearing; two is a conversation that is not working.
        var call = new Call();

        call.Says("mmm").Expect(BookingStage.CollectingWhen, "REPEAT", "ASK_DAY");
        Assert.Equal(1, call.State.NoProgressTurns);

        call.Says("what").Expect(BookingStage.Transferring, "HANDOVER:NoProgress", "TRANSFER:NoProgress");
    }

    [Fact]
    public void RealProgress_ResetsThePatienceCounter()
    {
        var call = new Call();
        call.Says("mmm").Expect(BookingStage.CollectingWhen);
        call.Says("tomorrow at four").Expect(BookingStage.CheckingAvailability);

        Assert.Equal(0, call.State.NoProgressTurns);
    }

    [Fact]
    public void SilenceCountsTheSameWayAsNonsense()
    {
        var call = new Call();
        call.Silent().Expect(BookingStage.ChoosingIntent, "REPEAT", "ASK_INTENT");
        call.Silent().Expect(BookingStage.Transferring, "HANDOVER:NoProgress", "TRANSFER:NoProgress");
    }

    [Fact]
    public void CallerTalkingWhileALookupIsInFlight_IsNotCountedAgainstThem()
    {
        // People fill silence. Those words answer no question and must not spend patience.
        var call = new Call();
        call.Says("tomorrow at four").Expect(BookingStage.CheckingAvailability);

        call.Says("hello? are you there?").Expect(BookingStage.CheckingAvailability);
        Assert.Empty(call.LastActions);
        Assert.Equal(0, call.State.NoProgressTurns);
    }

    [Fact]
    public void ACallThatRunsTooLong_GoesToAPerson()
    {
        var call = new Call(Ctx(), new BookingPolicy(MaxNoProgressTurns: 99, MaxTurns: 3));

        call.Says("tomorrow").Says("hello").Says("hello")
            .Says("hello")
            .Expect(BookingStage.Transferring, "HANDOVER:CallLengthExceeded", "TRANSFER:CallLengthExceeded");
    }

    [Fact]
    public void RepeatedlyEmptyCalendar_IsTreatedAsThePlatformsFailureNotTheCallers()
    {
        // The caller is cooperating perfectly - naming day after day - so the no-progress
        // counter keeps resetting. Empty lookups therefore need a counter of their own, or a
        // thin calendar produces a polite, endless tour of the week.
        var call = new Call();

        call.Says("tomorrow at four").Available()
            .Expect(BookingStage.CollectingWhen, "NOTHING_FREE");

        call.Says("friday at four").Expect(BookingStage.CheckingAvailability);

        call.Available()
            .Expect(BookingStage.Transferring, "HANDOVER:NoAvailability", "TRANSFER:NoAvailability");
    }

    // ============================================================== corrections

    [Fact]
    public void PastTime_IsNamedRatherThanSilentlyMovedForward()
    {
        var call = new Call();

        call.Says("today at nine").Expect(BookingStage.CollectingWhen, "PASSED");
        Assert.Null(call.State.Date);

        call.Says("tomorrow at four")
            .Expect(BookingStage.CheckingAvailability, "CHECKING", "LOOKUP:2026-09-17:16:00:None");
    }

    [Fact]
    public void CorrectionDuringConfirmation_BeatsTheWordNoAndKeepsTheName()
    {
        // "No, make it five instead" is a refusal and a new request in one breath. Only the
        // second half is useful, and re-asking for a name already given is how a caller decides
        // the agent is not listening.
        var call = new Call();
        call.Says("tomorrow at four").Available(Slot(16, 0), Slot(17, 0));
        call.Says("Karim").Expect(BookingStage.ConfirmingBooking, "READBACK:1600:Karim");

        call.Says("no, make it five instead")
            .Expect(BookingStage.CheckingAvailability, "CHECKING", "LOOKUP:2026-09-17:17:00:None");

        call.Available(Slot(17, 0))
            .Expect(BookingStage.ConfirmingBooking, "READBACK:1700:Karim");

        call.Says("yes").Expect(BookingStage.Booking, "BOOKING", "COMMIT:1700:Karim");
    }

    [Fact]
    public void PlainNoAtConfirmation_DropsTheSelectionAndStartsTheQuestionAgain()
    {
        var call = new Call();
        call.Says("tomorrow at four").Available(Slot(16, 0));
        call.Says("Karim").Expect(BookingStage.ConfirmingBooking);

        call.Says("no thats wrong").Expect(BookingStage.CollectingWhen, "ASK_DAY");
        Assert.Null(call.State.Selected);
    }

    // ============================================================== commit failures

    [Fact]
    public void LosingTheSlotRace_RecoversInsteadOfLosingTheCall()
    {
        // Two callers reaching for the same slot is ordinary under concurrency. It is an
        // apology and another look, not a failed call.
        var call = new Call();
        call.Says("tomorrow at four").Available(Slot(16, 0), Slot(17, 0));
        call.Says("Karim").Says("yes").Expect(BookingStage.Booking);

        call.Rejected(BookingFailure.SlotTaken)
            .Expect(BookingStage.CheckingAvailability, "SLOT_TAKEN", "LOOKUP:2026-09-17:16:00:None");

        call.Available(Slot(17, 0)).Expect(BookingStage.OfferingAlternatives, "OFFER_ALT:1700");
        call.Says("yes").Expect(BookingStage.ConfirmingBooking, "READBACK:1700:Karim");
    }

    [Theory]
    [InlineData(BookingFailure.SystemError)]
    [InlineData(BookingFailure.InvalidDetails)]
    public void UnrecoverableCommitFailure_GoesToAPerson(BookingFailure failure)
    {
        var call = new Call();
        call.Says("tomorrow at four").Available(Slot(16, 0));
        call.Says("Karim").Says("yes");

        call.Rejected(failure)
            .Expect(BookingStage.Transferring, "HANDOVER:BookingFailed", "TRANSFER:BookingFailed");
    }

    // ============================================================== collecting across turns

    [Fact]
    public void DateAndTimeAccumulateAcrossTurns()
    {
        var call = new Call();

        call.Says("next tuesday").Expect(BookingStage.CollectingWhen, "ASK_TIME");
        Assert.Equal(new DateOnly(2026, 9, 22), call.State.Date);

        call.Says("at ten")
            .Expect(BookingStage.CheckingAvailability, "CHECKING", "LOOKUP:2026-09-22:10:00:None");
    }

    [Fact]
    public void ATimeGivenBeforeADay_SurvivesUntilTheDayArrives()
    {
        var call = new Call();

        call.Says("around four").Expect(BookingStage.CollectingWhen, "ASK_DAY");
        call.Says("thursday")
            .Expect(BookingStage.CheckingAvailability, "CHECKING", "LOOKUP:2026-09-17:16:00:None");
    }

    [Fact]
    public void APartOfDayIsEnoughToLookUp_AndFiltersWhatIsOffered()
    {
        // "Tomorrow morning" is a real request. Demanding a clock time before even checking
        // wastes a turn on every single call.
        var call = new Call();

        call.Says("tomorrow morning")
            .Expect(BookingStage.CheckingAvailability, "CHECKING", "LOOKUP:2026-09-17:none:Morning");

        call.Available(Slot(9, 30), Slot(11, 0), Slot(15, 0))
            .Expect(BookingStage.OfferingAlternatives, "OFFER_TIMES:0930,1100");

        call.Says("eleven").Expect(BookingStage.CollectingName);
        Assert.Equal("1100", call.State.Selected!.StartsAtLocal.ToString("HHmm"));
    }

    // ============================================================== names

    [Fact]
    public void ASentenceIsNotAName_AndTheAgentAsksAgain()
    {
        var call = new Call();
        call.Says("tomorrow at four").Available(Slot(16, 0));

        call.Says("I am free on Tuesday afternoon")
            .Expect(BookingStage.CollectingName, "REPEAT", "ASK_NAME");

        call.Says("Rahim").Expect(BookingStage.ConfirmingBooking, "READBACK:1600:Rahim");
    }

    [Fact]
    public void WithoutTheNameRequirement_TheContactNumberIsUsed()
    {
        // An invented placeholder like "Caller" is worse than an ugly value: staff cannot tell
        // it apart from a name somebody actually gave.
        var call = new Call(Ctx(), new BookingPolicy(RequireName: false));
        call.Says("tomorrow at four").Available(Slot(16, 0))
            .Expect(BookingStage.ConfirmingBooking, "READBACK:1600:you");
    }

    // =====================================================================================
    // harness
    // =====================================================================================

    private static OfferedSlot Slot(int hour, int minute, int day = 17)
    {
        var local = new DateTime(2026, 9, day, hour, minute, 0);
        // The id is made readable so a failing assertion says "1600", not a random GUID.
        var id = new Guid($"{hour:00}{minute:00}0000-0000-0000-0000-000000000000");
        return new OfferedSlot(id, local, local.AddHours(-6), "Provider A");
    }

    /// <summary>Drives one conversation and remembers the last turn, so scenarios read as calls.</summary>
    private sealed class Call
    {
        private readonly BookingConversationMachine _machine;
        private readonly DateTimeParseContext _context;

        public BookingState State { get; private set; }
        public IReadOnlyList<BookingAction> LastActions { get; private set; }

        public Call(DateTimeParseContext? context = null, BookingPolicy? policy = null)
        {
            _context = context ?? Ctx();
            _machine = new BookingConversationMachine(
                new EnglishDateTimeParser(), new TaggedPhrases(), policy);

            var opening = _machine.Begin("+8801712345678");
            State = opening.State;
            LastActions = opening.Actions;
        }

        private Call Step(BookingInput input)
        {
            var turn = _machine.Advance(State, input, _context);
            State = turn.State;
            LastActions = turn.Actions;
            return this;
        }

        public Call Says(string utterance) => Step(new BookingInput.CallerSpoke(utterance));
        public Call Silent() => Step(new BookingInput.CallerSilent());
        public Call Available(params OfferedSlot[] slots) =>
            Step(new BookingInput.AvailabilityChecked(slots));
        public Call Committed(string reference) => Step(new BookingInput.BookingCommitted(reference));
        public Call Rejected(BookingFailure failure) => Step(new BookingInput.BookingRejected(failure));

        public Call Expect(BookingStage stage, params string[] actions)
        {
            Assert.Equal(stage, State.Stage);
            if (actions.Length > 0)
                Assert.Equal(actions, LastActions.Select(Describe).ToArray());
            return this;
        }

        private static string Describe(BookingAction action) => action switch
        {
            BookingAction.Speak s => s.Text,
            BookingAction.LookUpAvailability l =>
                $"LOOKUP:{l.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:"
                + $"{l.PreferredTime?.ToString("HH\\:mm", CultureInfo.InvariantCulture) ?? "none"}:"
                + $"{l.DayPart}",
            BookingAction.CommitBooking c => $"COMMIT:{ShortId(c.SlotId)}:{c.CallerName}",
            BookingAction.TransferToHuman t => $"TRANSFER:{t.Reason}",
            BookingAction.EndCall e => $"END:{e.Reason}",
            _ => action.GetType().Name
        };

        private static string ShortId(Guid id) => id.ToString("N")[..4];
    }

    /// <summary>
    /// Stands in for the real phrase bank so flow tests assert on decisions rather than on
    /// wording. A copy edit must never turn a green suite red.
    /// </summary>
    private sealed class TaggedPhrases : IBookingPhrases
    {
        public string AskIntent() => "ASK_INTENT";
        public string ContactReadback(string contact) => "";
        public string AskWhen() => "ASK_WHEN";
        public string AskWhichDay() => "ASK_DAY";
        public string AskWhatTime(DateOnly date, DateOnly today) => "ASK_TIME";
        public string AskMorningOrEvening(int hour12) => $"ASK_AMPM:{hour12}";
        public string ThatTimeHasPassed() => "PASSED";
        public string NothingFreeThatDay(DateOnly date, DateOnly today) => "NOTHING_FREE";
        public string OfferAlternatives(IReadOnlyList<OfferedSlot> slots, DateOnly today) =>
            "OFFER_ALT:" + string.Join(",", slots.Select(Tag));
        public string OfferTimes(IReadOnlyList<OfferedSlot> slots, DateOnly today) =>
            "OFFER_TIMES:" + string.Join(",", slots.Select(Tag));
        public string AskName() => "ASK_NAME";
        public string ReadBackForConfirmation(OfferedSlot slot, string callerName, DateOnly today) =>
            $"READBACK:{Tag(slot)}:{callerName}";
        public string Confirmed(OfferedSlot slot, string reference, DateOnly today) =>
            $"CONFIRMED:{reference}";
        public string SlotJustTaken() => "SLOT_TAKEN";
        public string DidNotCatchThat() => "REPEAT";
        public string CheckingAvailability() => "CHECKING";
        public string BookingNow() => "BOOKING";
        public string HandingOver(HandoffReason reason) => $"HANDOVER:{reason}";
        public string Goodbye() => "GOODBYE";

        private static string Tag(OfferedSlot slot) => slot.StartsAtLocal.ToString("HHmm");
    }
}

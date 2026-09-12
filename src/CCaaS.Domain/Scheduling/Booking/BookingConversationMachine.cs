using CCaaS.Domain.Scheduling.DateTimeParsing;

namespace CCaaS.Domain.Scheduling.Booking;

/// <summary>
/// Drives an appointment-booking conversation, deterministically.
///
/// CONTRACT
/// --------
/// <see cref="Advance"/> is a pure function of (state, input). It reads no clock, touches no
/// database, calls no model and holds no field that changes between calls. Given the same
/// state and the same input it produces the same state and the same actions, on call one and
/// on call one hundred thousand.
///
/// That is not architectural tidiness for its own sake. It buys four things that a
/// prompt-driven flow cannot offer:
///
///   1. The whole conversation is testable at full speed with no telephone and no database,
///      which is why the scenarios in the test suite are entire calls rather than fragments.
///   2. <see cref="BookingState"/> is the only thing that needs persisting, so a worker can die
///      mid-call and another can pick the call up exactly where it was.
///   3. A caller can never be told they are booked unless a commit actually succeeded - the
///      only path to <see cref="BookingStage.Booked"/> is
///      <see cref="BookingInput.BookingCommitted"/>.
///   4. The call cannot loop forever. Every turn that adds nothing increments a counter, and
///      the counter ends the call with a human on the line.
///
/// WHERE THE LLM STILL BELONGS
/// ---------------------------
/// Understanding open-ended speech - a caller explaining a problem, asking what the service
/// covers, changing the subject. The model handles the talking. It does not decide whether an
/// appointment is booked.
/// </summary>
public sealed partial class BookingConversationMachine
{
    private readonly IDateTimeParser _parser;
    private readonly IBookingPhrases _say;
    private readonly BookingPolicy _policy;

    public BookingConversationMachine(
        IDateTimeParser parser,
        IBookingPhrases? phrases = null,
        BookingPolicy? policy = null)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _say = phrases ?? EnglishBookingPhrases.Instance;
        _policy = policy ?? BookingPolicy.Default;
    }

    /// <summary>Opens the call. <paramref name="welcomeLine"/> comes from the agent configuration.</summary>
    public BookingTurn Begin(string contact, string? welcomeLine = null)
    {
        var state = BookingState.Start(contact) with { Stage = BookingStage.ChoosingIntent };

        var actions = new List<BookingAction>();
        if (!string.IsNullOrWhiteSpace(welcomeLine))
            actions.Add(new BookingAction.Speak(welcomeLine.Trim()));
        actions.Add(new BookingAction.Speak(_say.AskIntent()));

        return Remember(new BookingTurn(state, actions));
    }

    public BookingTurn Advance(BookingState state, BookingInput input, DateTimeParseContext context)
    {
        // A terminal call takes no further input. Reached only through a bug or a late-arriving
        // event, and swallowing it is safer than acting on it.
        if (state.IsTerminal)
            return new BookingTurn(state, Array.Empty<BookingAction>());

        return Remember(input switch
        {
            BookingInput.CallerSpoke spoke => OnCallerSpoke(state, spoke.Utterance, context),
            BookingInput.CallerSilent => OnSilence(state, context),
            BookingInput.AvailabilityChecked checkedSlots when state.Stage == BookingStage.CheckingAvailability => OnAvailability(state, checkedSlots.Slots, context),
            BookingInput.BookingCommitted committed when state.Stage == BookingStage.Booking => OnCommitted(state, committed.Reference, context),
            BookingInput.BookingRejected rejected when state.Stage == BookingStage.Booking => OnRejected(state, rejected.Failure, context),
            BookingInput.AppointmentsFound found when state.Stage == BookingStage.LookingUpAppointments =>
                OnAppointmentsFound(state, found.Appointments, context),
            BookingInput.AppointmentCancelled when state.Stage == BookingStage.CancellingAppointment =>
                OnCancelled(state),
            BookingInput.AppointmentRescheduled changed when state.Stage == BookingStage.Rescheduling =>
                OnCommitted(state, changed.Reference, context),
            BookingInput.AppointmentChangeRejected when state.Stage is BookingStage.CancellingAppointment or BookingStage.Rescheduling =>
                Transfer(state with { HandoffPending = false }, state.HandoffPending ? HandoffReason.CallerAsked : HandoffReason.BookingFailed),
            _ => new BookingTurn(state, Array.Empty<BookingAction>())
        });
    }

    // =====================================================================================
    // caller spoke
    // =====================================================================================

    private BookingTurn OnCallerSpoke(BookingState state, string utterance, DateTimeParseContext context)
    {
        // While a database round trip is in flight the caller may still be talking. Those words
        // are not an answer to anything, and counting them as failure would end healthy calls.
        var move = CallerIntent.Detect(utterance);
        if (state.Stage == BookingStage.ChoosingIntent && utterance.Trim().TrimEnd('.', '!', '?')
            .Equals("cancel", StringComparison.OrdinalIgnoreCase))
            move = CallerMove.CancelAppointment;
        if (state.IsWaitingOnSystem)
        {
            if (move == CallerMove.WantsHuman)
                return state.Stage is BookingStage.Booking or BookingStage.CancellingAppointment or BookingStage.Rescheduling
                    ? new BookingTurn(state with { HandoffPending = true }, Array.Empty<BookingAction>())
                    : Transfer(state, HandoffReason.CallerAsked);
            return new BookingTurn(state, Array.Empty<BookingAction>());
        }

        state = state with { CallerTurns = state.CallerTurns + 1 };

        // Caller-facing stages transfer immediately. The waiting branch above preserves
        // a commit already in flight before fulfilling a pending transfer request.
        if (move == CallerMove.WantsHuman)
            return Transfer(state, HandoffReason.CallerAsked);

        if (move == CallerMove.WantsToEnd)
            return End(state);

        if (state.CallerTurns > _policy.MaxTurns)
            return Transfer(state, HandoffReason.CallLengthExceeded);

        if (move == CallerMove.Repeat)
        {
            var question = state.LastQuestion ?? CurrentQuestion(state, context);
            return new BookingTurn(state, question is null ? Array.Empty<BookingAction>()
                : new BookingAction[] { new BookingAction.Speak(question) });
        }
        if (move is CallerMove.CancelAppointment or CallerMove.RescheduleAppointment)
            return StartChange(state, move);
        if (state.Stage == BookingStage.ChoosingIntent)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(utterance,
                @"\b(?:book|schedule|appointment)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && !_parser.Parse(utterance, context).HasAnything)
                return Progress(state with { Intent = BookingIntent.Book, Stage = BookingStage.CollectingWhen },
                    new BookingAction.Speak(_say.AskWhen()));
            state = state with { Intent = BookingIntent.Book, Stage = BookingStage.CollectingWhen };
        }

        return state.Stage switch
        {
            BookingStage.Opening or BookingStage.CollectingWhen => OnWhen(state, utterance, context),
            BookingStage.ResolvingAmbiguity => OnAmbiguityAnswer(state, utterance, context),
            BookingStage.OfferingAlternatives => OnAlternativeChosen(state, utterance, move, context),
            BookingStage.CollectingName => OnName(state, utterance, context),
            BookingStage.ConfirmingCancellation or BookingStage.ConfirmingReschedule =>
                OnChangeConfirmation(state, utterance, move, context),
            BookingStage.ConfirmingBooking => OnConfirmation(state, utterance, move, context),
            _ => NoProgress(state, context, _say.DidNotCatchThat())
        };
    }

    /// <summary>Collecting the date and time - the bulk of most calls.</summary>
    private BookingTurn OnWhen(BookingState state, string utterance, DateTimeParseContext context)
    {
        var parsed = _parser.Parse(utterance, context);

        if (!parsed.HasAnything)
            return NoProgress(state, context, _say.DidNotCatchThat());

        var next = Merge(state, parsed);
        if (parsed.DateCertainty == ValueCertainty.Ambiguous) return AskForClearDate(next);

        // A time already gone is a mistake worth naming. The parser reports it rather than
        // correcting it precisely so this line can exist.
        if (parsed.IsInPast)
        {
            return NoProgress(
                next with { Date = null, Time = null, DateCertainty = ValueCertainty.NotProvided, TimeCertainty = ValueCertainty.NotProvided, Stage = BookingStage.CollectingWhen },
                context,
                _say.ThatTimeHasPassed(),
                repeatQuestion: false);
        }

        // Two readings of an hour cannot be settled by guessing. Ask the closed question.
        if (next.TimeCertainty == ValueCertainty.Ambiguous && next.Time is { } ambiguous)
        {
            var hour12 = ambiguous.Hour % 12 == 0 ? 12 : ambiguous.Hour % 12;
            return Progress(
                next with
                {
                    Stage = BookingStage.ResolvingAmbiguity,
                    AmbiguousHour = hour12,
                    AmbiguousMinute = ambiguous.Minute
                },
                new BookingAction.Speak(_say.AskMorningOrEvening(hour12)));
        }

        return AfterWhen(next, context);
    }

    /// <summary>
    /// Decides what to do once date/time have been merged: look up, or ask for the missing half.
    /// A date plus a part of day is enough to look up - "tomorrow morning" is a real request and
    /// making the caller name a clock time before we have even checked is a wasted turn.
    /// </summary>
    private BookingTurn AfterWhen(BookingState state, DateTimeParseContext context)
    {
        if (state.DateCertainty == ValueCertainty.Ambiguous) return AskForClearDate(state);
        if (state.Date is null)
            return Progress(state with { Stage = BookingStage.CollectingWhen },
                new BookingAction.Speak(_say.AskWhichDay()));

        if (state.Time is null && state.DayPart == DayPart.None)
            return Progress(state with { Stage = BookingStage.CollectingWhen },
                new BookingAction.Speak(_say.AskWhatTime(state.Date.Value, Today(context))));

        return Progress(
            state with { Stage = BookingStage.CheckingAvailability },
            new BookingAction.Speak(_say.CheckingAvailability()),
            new BookingAction.LookUpAvailability(state.Date.Value, state.Time, state.DayPart));
    }

    /// <summary>Answering "ten in the morning, or ten at night?".</summary>
    private BookingTurn OnAmbiguityAnswer(BookingState state, string utterance, DateTimeParseContext context)
    {
        // The caller may abandon the question and give a whole new time instead - "no, make it
        // four". A fresh, unambiguous time wins outright.
        var direct = _parser.Parse(utterance, context);
        if (direct.HasTime && direct.TimeCertainty != ValueCertainty.Ambiguous)
            return AfterWhen(ClearAmbiguity(Merge(state, direct)), context);

        // Otherwise the reply is a fragment - "evening", "in the morning" - which carries no
        // time on its own. Re-parsing it glued to the hour in question turns it into one.
        var hour = state.AmbiguousHour ?? 0;
        if (hour > 0)
        {
            var stitched = state.AmbiguousMinute == 0
                ? $"{EnglishBookingPhrases.SpeakHourWord(hour)} {utterance}"
                : $"{hour}:{state.AmbiguousMinute:00} {utterance}";

            var resolved = _parser.Parse(stitched, context);
            if (resolved.HasTime && resolved.TimeCertainty == ValueCertainty.Explicit)
                return AfterWhen(ClearAmbiguity(Merge(state, resolved)), context);
        }

        return NoProgress(state, context, _say.DidNotCatchThat());
    }

    private static BookingState ClearAmbiguity(BookingState state) =>
        state with { AmbiguousHour = null, AmbiguousMinute = 0 };

    /// <summary>Picking one of the slots that were read out.</summary>
    private BookingTurn OnAlternativeChosen(
        BookingState state, string utterance, CallerMove move, DateTimeParseContext context)
    {
        var correction = NegativeCorrection(state, utterance, context);
        if (correction is not null) return correction;
        var parsed = _parser.Parse(utterance, context);
        if (parsed.DateCertainty == ValueCertainty.Ambiguous) return AskForClearDate(Merge(state, parsed));

        // A time that matches something on the table is a choice, however it was phrased.
        if (parsed.HasTime)
        {
            var match = state.Offered.FirstOrDefault(s =>
                TimeOnly.FromDateTime(s.StartsAtLocal) == parsed.Time!.Value
                && (parsed.Date is null || DateOnly.FromDateTime(s.StartsAtLocal) == parsed.Date.Value));

            if (match is not null)
                return Select(state, match, context);
        }

        // A different day entirely - "no, what about Thursday" - is a fresh request, not a refusal.
        if (parsed.HasDate && parsed.Date != state.Date)
            return AfterWhen(Merge(state with { Offered = Array.Empty<OfferedSlot>() }, parsed), context);

        if (move == CallerMove.Affirmative && state.Offered.Count == 1)
            return Select(state, state.Offered[0], context);

        if (move is CallerMove.Negative or CallerMove.Unsure)
            return NoProgress(
                state with
                {
                    Stage = BookingStage.CollectingWhen,
                    Date = null,
                    Time = null,
                    DayPart = DayPart.None,
                    DateCertainty = ValueCertainty.NotProvided,
                    TimeCertainty = ValueCertainty.NotProvided,
                    Offered = Array.Empty<OfferedSlot>()
                },
                context,
                _say.AskWhichDay(),
                repeatQuestion: false);

        return NoProgress(state, context, _say.DidNotCatchThat());
    }

    private BookingTurn OnName(BookingState state, string utterance, DateTimeParseContext context)
    {
        var name = CallerIntent.ExtractName(utterance);
        if (name is null)
            return NoProgress(state, context, _say.DidNotCatchThat());

        return Progress(
            state with { CallerName = name, Stage = BookingStage.ConfirmingBooking },
            new BookingAction.Speak(Readback(state with { CallerName = name }, context)));
    }

    /// <summary>The last gate before anything irreversible happens.</summary>
    private BookingTurn OnConfirmation(
        BookingState state, string utterance, CallerMove move, DateTimeParseContext context)
    {
        // A correction takes priority over the yes/no reading: "no, make it four" is both a
        // refusal and a new request, and only the second half is useful.
        var correction = NegativeCorrection(state, utterance, context);
        if (correction is not null) return correction;
        var parsed = _parser.Parse(utterance, context);
        if (parsed.DateCertainty == ValueCertainty.Ambiguous) return AskForClearDate(Merge(state, parsed));
        if (parsed.HasAnything)
            return OnWhen(state with { Selected = null, Offered = Array.Empty<OfferedSlot>() }, utterance, context);

        if (System.Text.RegularExpressions.Regex.IsMatch(utterance,
            @"\b(?:my name is|name is|call me)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && CallerIntent.ExtractName(utterance) is { } correctedName)
            return Progress(state with { CallerName = correctedName },
                new BookingAction.Speak(Readback(state with { CallerName = correctedName }, context)));

        if (move == CallerMove.Affirmative && state.Intent == BookingIntent.Reschedule)
            return Progress(state with { Stage = BookingStage.Rescheduling },
                new BookingAction.Speak("I am securing the new time before cancelling the old appointment."),
                new BookingAction.RescheduleBooking(state.Existing!.BookingId, state.Selected!.SlotId));

        if (move == CallerMove.Affirmative)
            return Progress(
                state with { Stage = BookingStage.Booking },
                new BookingAction.Speak(_say.BookingNow()),
                new BookingAction.CommitBooking(
                    state.Selected!.SlotId, NameForBooking(state), state.Contact));

        if (move == CallerMove.Negative)
            return NoProgress(
                state with
                {
                    Stage = BookingStage.CollectingWhen,
                    Selected = null,
                    Date = null,
                    Time = null,
                    DayPart = DayPart.None,
                    DateCertainty = ValueCertainty.NotProvided,
                    TimeCertainty = ValueCertainty.NotProvided,
                    Offered = Array.Empty<OfferedSlot>()
                },
                context,
                _say.AskWhichDay(),
                repeatQuestion: false);

        return NoProgress(state, context, _say.DidNotCatchThat());
    }

    // =====================================================================================
    // system events
    // =====================================================================================

    private BookingTurn OnAvailability(
        BookingState state, IReadOnlyList<OfferedSlot> slots, DateTimeParseContext context)
    {
        var today = Today(context);

        // The day filter guards against an orchestrator returning slots for a date nobody
        // asked about. Selecting one would quietly move the appointment to another day; the
        // readback would say the new date, but a caller half-listening would not catch it.
        // Dropping them instead turns that bug into "nothing free", which ends with a person.
        var usable = slots
            .Where(s => state.Date is null || DateOnly.FromDateTime(s.StartsAtLocal) == state.Date.Value)
            .Where(s => state.DayPart == DayPart.None
                        || MatchesDayPart(TimeOnly.FromDateTime(s.StartsAtLocal), state.DayPart))
            .ToList();

        if (usable.Count == 0)
        {
            var emptied = state with
            {
                Stage = BookingStage.CollectingWhen,
                Date = null,
                Time = null,
                DayPart = DayPart.None,
                DateCertainty = ValueCertainty.NotProvided,
                TimeCertainty = ValueCertainty.NotProvided,
                Offered = Array.Empty<OfferedSlot>(),
                EmptyLookups = state.EmptyLookups + 1
            };

            // Repeatedly finding nothing is the platform's failure, not the caller's, and it is
            // exactly the case where a person can look at dates the agent cannot. This is
            // counted on its own because naming another day resets the no-progress counter -
            // the caller is cooperating perfectly while the calendar keeps coming back empty.
            if (emptied.EmptyLookups >= _policy.MaxNoProgressTurns)
                return Transfer(emptied, HandoffReason.NoAvailability);

            return Progress(emptied,
                new BookingAction.Speak(_say.NothingFreeThatDay(state.Date ?? today, today)));
        }

        // The caller named a clock time and it is free: nothing to discuss.
        if (state.Time is { } wanted)
        {
            var exact = usable.FirstOrDefault(s => TimeOnly.FromDateTime(s.StartsAtLocal) == wanted);
            if (exact is not null)
                return Select(state, exact, context);
        }

        var offered = Rank(usable, state.Time).Take(_policy.MaxAlternativesOffered).ToList();

        var line = state.Time is null
            ? _say.OfferTimes(offered, today)
            : _say.OfferAlternatives(offered, today);

        return Progress(
            state with { Stage = BookingStage.OfferingAlternatives, Offered = offered },
            new BookingAction.Speak(line));
    }

    private BookingTurn OnCommitted(BookingState state, string reference, DateTimeParseContext context)
    {
        var booked = state with
        {
            Stage = BookingStage.Booked,
            BookingReference = reference,
            NoProgressTurns = 0
        };

        if (state.HandoffPending)
            return Transfer(booked with { HandoffPending = false }, HandoffReason.CallerAsked);

        return new BookingTurn(booked, new BookingAction[]
        {
            new BookingAction.Speak(_say.Confirmed(state.Selected!, reference, Today(context)), ExpectsReply: false),
            new BookingAction.EndCall("booked")
        });
    }

    private BookingTurn OnRejected(BookingState state, BookingFailure failure, DateTimeParseContext context)
    {
        if (state.HandoffPending)
            return Transfer(state with { HandoffPending = false }, HandoffReason.CallerAsked);

        // Losing a race for a slot is ordinary under concurrency and entirely recoverable:
        // apologise, look again, carry on. It is not a reason to lose the call.
        if (failure == BookingFailure.SlotTaken && state.Date is not null)
        {
            var without = state.Offered.Where(s => s.SlotId != state.Selected?.SlotId).ToList();

            return Progress(
                state with
                {
                    Stage = BookingStage.CheckingAvailability,
                    Selected = null,
                    Offered = without
                },
                new BookingAction.Speak(_say.SlotJustTaken()),
                new BookingAction.LookUpAvailability(state.Date.Value, state.Time, state.DayPart));
        }

        return Transfer(state, HandoffReason.BookingFailed);
    }

    private BookingTurn OnSilence(BookingState state, DateTimeParseContext context)
    {
        if (state.IsWaitingOnSystem)
            return new BookingTurn(state, Array.Empty<BookingAction>());

        return NoProgress(state, context, _say.DidNotCatchThat());
    }

    // =====================================================================================
    // shared moves
    // =====================================================================================

    private BookingTurn Select(BookingState state, OfferedSlot slot, DateTimeParseContext context)
    {
        var chosen = state with
        {
            Selected = slot,
            Date = DateOnly.FromDateTime(slot.StartsAtLocal),
            Time = TimeOnly.FromDateTime(slot.StartsAtLocal)
        };

        if (_policy.RequireName && string.IsNullOrWhiteSpace(chosen.CallerName))
            return Progress(chosen with { Stage = BookingStage.CollectingName },
                new BookingAction.Speak(_say.AskName()));

        return Progress(chosen with { Stage = BookingStage.ConfirmingBooking },
            new BookingAction.Speak(
                Readback(chosen, context)));
    }

    /// <summary>The conversation moved forward, so the patience counter resets.</summary>
    private BookingTurn AskForClearDate(BookingState state) =>
        Progress(state with
        {
            Stage = BookingStage.CollectingWhen, Date = null,
            DateCertainty = ValueCertainty.NotProvided,
            Selected = null, Offered = Array.Empty<OfferedSlot>()
        }, new BookingAction.Speak(_say.AskUnambiguousDate()));

    private static BookingTurn Progress(BookingState state, params BookingAction[] actions) =>
        new(state with { NoProgressTurns = 0 }, actions);

    /// <summary>
    /// The turn added nothing. Counts it, and hands the call to a person once the caller and
    /// the agent have clearly stopped understanding each other.
    /// </summary>
    private BookingTurn NoProgress(
        BookingState state,
        DateTimeParseContext context,
        string line,
        bool repeatQuestion = true)
    {
        var counted = state with { NoProgressTurns = state.NoProgressTurns + 1 };

        if (counted.NoProgressTurns >= _policy.MaxNoProgressTurns)
            return Transfer(counted, HandoffReason.NoProgress);

        var actions = new List<BookingAction> { new BookingAction.Speak(line) };
        if (repeatQuestion)
        {
            var question = CurrentQuestion(counted, context);
            if (question is not null)
                actions.Add(new BookingAction.Speak(question));
        }

        return new BookingTurn(counted, actions);
    }

    /// <summary>The question the caller is currently being asked, so "sorry?" can repeat it.</summary>
    private string? CurrentQuestion(BookingState state, DateTimeParseContext context) => state.Stage switch
    {
        BookingStage.ChoosingIntent => _say.AskIntent(),
        BookingStage.ConfirmingCancellation or BookingStage.ConfirmingReschedule => ChangeReadback(state, context),
        BookingStage.Opening => _say.AskWhen(),
        BookingStage.CollectingWhen when state.Date is null => _say.AskWhichDay(),
        BookingStage.CollectingWhen => _say.AskWhatTime(state.Date!.Value, Today(context)),
        BookingStage.ResolvingAmbiguity when state.AmbiguousHour is { } h => _say.AskMorningOrEvening(h),
        BookingStage.OfferingAlternatives => _say.OfferAlternatives(state.Offered, Today(context)),
        BookingStage.CollectingName => _say.AskName(),
        BookingStage.ConfirmingBooking when state.Selected is not null =>
            Readback(state, context),
        _ => null
    };

    private BookingTurn Transfer(BookingState state, HandoffReason reason) =>
        new(state with { Stage = BookingStage.Transferring, HandoffReason = reason },
            new BookingAction[]
            {
                new BookingAction.Speak(_say.HandingOver(reason), ExpectsReply: false),
                new BookingAction.TransferToHuman(reason)
            });

    private BookingTurn End(BookingState state) =>
        new(state with { Stage = BookingStage.Ended },
            new BookingAction[]
            {
                new BookingAction.Speak(_say.Goodbye(), ExpectsReply: false),
                new BookingAction.EndCall("caller declined")
            });

    // =====================================================================================
    // helpers
    // =====================================================================================

    private static DateOnly Today(DateTimeParseContext context) =>
        DateOnly.FromDateTime(context.NowLocal);

    /// <summary>
    /// What goes on the booking when no name was collected - only reachable with
    /// <see cref="BookingPolicy.RequireName"/> switched off. The contact number is used rather
    /// than a placeholder like "Caller", because an invented name on a real booking is worse
    /// than an ugly one: staff cannot tell it apart from a name someone actually gave.
    /// </summary>
    private static string NameForBooking(BookingState state) =>
        string.IsNullOrWhiteSpace(state.CallerName) ? state.Contact : state.CallerName;

    /// <summary>
    /// Folds a fresh parse into what is already known. Each half is replaced only when the new
    /// utterance actually carried one, so "next Tuesday" followed by "at ten" accumulates
    /// rather than overwriting.
    /// </summary>
    private static BookingState Merge(BookingState state, DateTimeParseResult parsed)
    {
        var next = state;

        if (parsed.HasDate)
            next = next with { Date = parsed.Date, DateCertainty = parsed.DateCertainty };

        if (parsed.HasTime)
            next = next with { Time = parsed.Time, TimeCertainty = parsed.TimeCertainty, DayPart = parsed.DayPart };

        if (parsed.DayPart != DayPart.None)
            next = next with { DayPart = parsed.DayPart };

        return next;
    }

    private static bool MatchesDayPart(TimeOnly time, DayPart part) => part switch
    {
        DayPart.Morning => time.Hour < 12,
        DayPart.Noon => time.Hour is 11 or 12 or 13,
        DayPart.Afternoon => time.Hour is >= 12 and < 17,
        DayPart.Evening => time.Hour is >= 17 and < 21,
        DayPart.Night => time.Hour >= 21 || time.Hour < 5,
        _ => true
    };

    /// <summary>
    /// Nearest to what was asked for first; failing that, earliest.
    ///
    /// Distance is computed from minutes-since-midnight and NOT from subtracting one TimeOnly
    /// from another. TimeOnly subtraction is circular - it returns the elapsed time going
    /// forward around the clock, so 15:00 minus 16:00 is twenty-three hours, not one. Used
    /// naively it ranks every earlier slot as the furthest away, which means an agent offering
    /// alternatives around four o'clock reads out the evening and skips the slot an hour before.
    /// </summary>
    private static IEnumerable<OfferedSlot> Rank(IReadOnlyList<OfferedSlot> slots, TimeOnly? wanted)
    {
        if (wanted is null)
            return slots.OrderBy(s => s.StartsAtLocal);

        var target = wanted.Value.Hour * 60 + wanted.Value.Minute;
        return slots
            .OrderBy(s => Math.Abs((s.StartsAtLocal.Hour * 60 + s.StartsAtLocal.Minute) - target))
            .ThenBy(s => s.StartsAtLocal);
    }
}

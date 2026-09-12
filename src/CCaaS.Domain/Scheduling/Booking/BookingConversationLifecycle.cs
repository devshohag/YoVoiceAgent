using System.Text.RegularExpressions;
using CCaaS.Domain.Scheduling.DateTimeParsing;

namespace CCaaS.Domain.Scheduling.Booking;

public sealed partial class BookingConversationMachine
{
    private static BookingTurn Remember(BookingTurn turn)
    {
        var question = turn.Actions.OfType<BookingAction.Speak>().LastOrDefault(s => s.ExpectsReply)?.Text;
        return question is null ? turn : turn with { State = turn.State with { LastQuestion = question } };
    }

    private string Readback(BookingState state, DateTimeParseContext context)
    {
        var name = string.IsNullOrWhiteSpace(state.CallerName) || state.CallerName == state.Contact ? "you" : state.CallerName;
        var line = _say.ReadBackForConfirmation(state.Selected!, name, Today(context));
        var contact = _say.ContactReadback(state.Contact);
        return state.Intent == BookingIntent.Reschedule
            ? line + " " + contact + " This will replace your old appointment only if the new time is secured."
            : (line + " " + contact).Trim();
    }

    private BookingTurn StartChange(BookingState state, CallerMove move) => Progress(state with
    {
        Intent = move == CallerMove.CancelAppointment ? BookingIntent.Cancel : BookingIntent.Reschedule,
        Stage = BookingStage.LookingUpAppointments, Existing = null, Selected = null,
        Offered = Array.Empty<OfferedSlot>(), Date = null, Time = null,
        DayPart = DayPart.None, DateCertainty = ValueCertainty.NotProvided,
        TimeCertainty = ValueCertainty.NotProvided
    }, new BookingAction.Speak("Let me find your upcoming appointment."),
        new BookingAction.FindUpcomingAppointments(state.Contact));

    private BookingTurn OnAppointmentsFound(BookingState state,
        IReadOnlyList<ExistingAppointment> appointments, DateTimeParseContext context)
    {
        var matches = appointments.Where(a => a.BookingId != Guid.Empty
            && string.Equals(a.Contact, state.Contact, StringComparison.OrdinalIgnoreCase)
            && a.Slot.StartsAtLocal > context.NowLocal).ToArray();
        // Never guess which booking to cancel. A human resolves zero or multiple matches.
        if (matches.Length != 1) return Transfer(state, HandoffReason.BookingFailed);
        var existing = matches[0];
        return Progress(state with
        {
            Existing = existing, CallerName = existing.CallerName,
            Stage = state.Intent == BookingIntent.Cancel
                ? BookingStage.ConfirmingCancellation : BookingStage.ConfirmingReschedule
        }, new BookingAction.Speak(ChangeReadback(state with { Existing = existing }, context)));
    }

    private string ChangeReadback(BookingState state, DateTimeParseContext context)
    {
        var existing = state.Existing!;
        var when = _say.DescribeAppointment(existing.Slot, existing.CallerName == state.Contact ? "you" : existing.CallerName, Today(context));
        return when + " " + _say.ContactReadback(state.Contact) + (state.Intent == BookingIntent.Cancel
            ? " Shall I cancel this appointment?" : " Is this the appointment you want to reschedule?");
    }

    private BookingTurn OnChangeConfirmation(BookingState state, string utterance,
        CallerMove move, DateTimeParseContext context)
    {
        // A yes carrying a correction is not consent to destroy the read-back appointment.
        if (WrongContact(utterance) || _parser.Parse(utterance, context).HasAnything)
            return Transfer(state, HandoffReason.BookingFailed);
        if (move == CallerMove.Negative) return End(state);
        if (move != CallerMove.Affirmative) return NoProgress(state, context, _say.DidNotCatchThat());
        if (state.Intent == BookingIntent.Cancel)
            return Progress(state with { Stage = BookingStage.CancellingAppointment },
                new BookingAction.Speak("I am cancelling that appointment now."),
                new BookingAction.CancelBooking(state.Existing!.BookingId));
        return Progress(state with { Stage = BookingStage.CollectingWhen },
            new BookingAction.Speak("Your current appointment stays in place while we find a new time. " + _say.AskWhen()));
    }

    private BookingTurn OnCancelled(BookingState state)
    {
        state = state with { CancellationConfirmed = true };
        if (state.HandoffPending)
            return Transfer(state with { HandoffPending = false }, HandoffReason.CallerAsked);
        return new BookingTurn(state with { Stage = BookingStage.Ended }, new BookingAction[]
        {
            new BookingAction.Speak("Your appointment has been cancelled.", ExpectsReply: false),
            new BookingAction.EndCall("cancelled")
        });
    }

    private static bool WrongContact(string utterance) => Regex.IsMatch(utterance,
        @"\b(?:wrong (?:phone |contact )?number|not my number|not (?:my |the )?(?:phone|contact)|wrong email)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private BookingTurn? NegativeCorrection(BookingState state, string utterance, DateTimeParseContext context)
    {
        if (WrongContact(utterance)) return Transfer(state, HandoffReason.BookingFailed);
        var negative = Regex.Match(utterance,
            @"\bnot\s+(?<old>.+?)(?:(?:[,;]|\bbut\b|\binstead\b|\bmake it\b)\s*(?<replacement>.+))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!negative.Success) return null;
        var rejected = _parser.Parse(negative.Groups["old"].Value, context);
        if (!rejected.HasAnything) return null;
        var next = state with { Selected = null, Offered = Array.Empty<OfferedSlot>(), Stage = BookingStage.CollectingWhen };
        if (rejected.HasDate) next = next with { Date = null, DateCertainty = ValueCertainty.NotProvided };
        if (rejected.HasTime || rejected.DayPart != DayPart.None)
            next = next with { Time = null, TimeCertainty = ValueCertainty.NotProvided,
                DayPart = DayPart.None, AmbiguousHour = null, AmbiguousMinute = 0 };
        if (negative.Groups["replacement"].Success)
        {
            var replacement = _parser.Parse(negative.Groups["replacement"].Value, context);
            if (replacement.HasAnything) return OnWhen(next, negative.Groups["replacement"].Value, context);
        }
        // Do not merge the negated date/hour back into the request.
        return AfterWhen(next, context);
    }
}

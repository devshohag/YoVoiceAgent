using System.Globalization;

namespace CCaaS.Domain.Scheduling.Booking;

/// <summary>
/// Every line the booking agent can say.
///
/// Kept behind an interface and out of the state machine for three reasons that all pay off
/// later: the wording becomes tenant-configurable without touching flow logic; a Bangla
/// implementation is a new class rather than an edit; and because the set of lines is finite
/// and enumerable, the common ones can be pre-rendered to audio and cached, which removes TTS
/// latency from the turn-taking path entirely.
/// </summary>
public interface IBookingPhrases
{
    string AskWhen();
    string AskWhichDay();
    string AskWhatTime(DateOnly date, DateOnly today);
    string AskMorningOrEvening(int hour12);
    string ThatTimeHasPassed();
    string NothingFreeThatDay(DateOnly date, DateOnly today);
    string OfferAlternatives(IReadOnlyList<OfferedSlot> slots, DateOnly today);
    string OfferTimes(IReadOnlyList<OfferedSlot> slots, DateOnly today);
    string AskName();
    string ReadBackForConfirmation(OfferedSlot slot, string callerName, DateOnly today);
    string Confirmed(OfferedSlot slot, string reference, DateOnly today);
    string SlotJustTaken();
    string DidNotCatchThat();
    string CheckingAvailability();
    string BookingNow();
    string HandingOver(HandoffReason reason);
    string Goodbye();
}

/// <summary>
/// English phrasing, written to be spoken rather than read.
///
/// Times are said the way people say them - "half past four in the afternoon", not "16:30" -
/// because a caller confirming an appointment has to hold the value in their head long enough
/// to notice if it is wrong, and a spoken 24-hour clock defeats that.
/// </summary>
public sealed class EnglishBookingPhrases : IBookingPhrases
{
    public static readonly EnglishBookingPhrases Instance = new();

    private readonly string _businessName;

    public EnglishBookingPhrases(string businessName = "us")
        => _businessName = businessName;

    public string AskWhen() =>
        "What day and time would suit you?";

    public string AskWhichDay() =>
        "Which day would you like to come in?";

    public string AskWhatTime(DateOnly date, DateOnly today) =>
        $"What time {SpeakDateWithPreposition(date, today)}?";

    public string AskMorningOrEvening(int hour12) =>
        $"Just to be sure - {SpeakHourWord(hour12)} in the morning, or {SpeakHourWord(hour12)} in the evening?";

    public string ThatTimeHasPassed() =>
        "That time has already gone by. Which day would you like instead?";

    public string NothingFreeThatDay(DateOnly date, DateOnly today) =>
        $"I'm sorry, there's nothing free {SpeakDateWithPreposition(date, today)}. Would another day work?";

    public string OfferAlternatives(IReadOnlyList<OfferedSlot> slots, DateOnly today)
    {
        if (slots.Count == 0)
            return "I'm sorry, I don't have anything close to that. Would another day work?";

        if (slots.Count == 1)
            return $"That time is taken, but I have {SpeakDateTime(slots[0].StartsAtLocal, today)}. "
                 + "Would that work?";

        var options = slots
            .Select(s => SpeakDateTime(s.StartsAtLocal, today))
            .ToList();

        var joined = string.Join(", or ", options);
        return $"That time is taken. I have {joined}. Which would you prefer?";
    }

    /// <summary>
    /// Used when the caller named a day or a part of day but no clock time - so nothing is
    /// "taken", there is simply a choice to make.
    /// </summary>
    public string OfferTimes(IReadOnlyList<OfferedSlot> slots, DateOnly today)
    {
        if (slots.Count == 0)
            return "I'm sorry, I don't have anything then. Would another day work?";

        if (slots.Count == 1)
            return $"I have {SpeakDateTime(slots[0].StartsAtLocal, today)}. Would that work?";

        var options = slots.Select(s => SpeakTime(TimeOnly.FromDateTime(s.StartsAtLocal))).ToList();
        var joined = string.Join(", or ", options);
        var when = SpeakDateWithPreposition(DateOnly.FromDateTime(slots[0].StartsAtLocal), today);
        return $"{Capitalise(when)} I have {joined}. Which suits you?";
    }

    public string AskName() =>
        "And what name should I put the appointment under?";

    public string ReadBackForConfirmation(OfferedSlot slot, string callerName, DateOnly today) =>
        $"So that's {SpeakDateTime(slot.StartsAtLocal, today)} for {callerName}. Shall I book it?";

    public string Confirmed(OfferedSlot slot, string reference, DateOnly today) =>
        $"You're booked for {SpeakDateTime(slot.StartsAtLocal, today)}. "
        + $"Your reference is {SpeakReference(reference)}. "
        + $"You'll get a text message with the details. Thanks for choosing {_businessName}.";

    public string SlotJustTaken() =>
        "I'm sorry - somebody took that slot just now. Let me find you another.";

    public string DidNotCatchThat() =>
        "Sorry, I didn't catch that. Could you say it again?";

    public string CheckingAvailability() =>
        "Let me check that for you.";

    public string BookingNow() =>
        "Booking that now.";

    public string HandingOver(HandoffReason reason) => reason switch
    {
        HandoffReason.CallerAsked => "Of course - let me put you through to a colleague.",
        HandoffReason.NoAvailability => "Let me put you through to a colleague who can look at more dates.",
        HandoffReason.BookingFailed => "I'm having trouble completing that. Let me put you through to a colleague.",
        _ => "Let me put you through to a colleague who can help."
    };

    public string Goodbye() =>
        "No problem at all. Thanks for your time, and have a good day.";

    // =====================================================================================
    // speaking dates and times
    // =====================================================================================

    /// <summary>
    /// "tomorrow", "Thursday the seventeenth", "Thursday the seventeenth of October".
    /// Relative words are preferred within the next two days because that is how people hear
    /// dates - "tomorrow" lands, "the seventeenth" has to be worked out.
    /// </summary>
    public string SpeakDate(DateOnly date, DateOnly today)
    {
        var delta = date.DayNumber - today.DayNumber;
        if (delta == 0) return "today";
        if (delta == 1) return "tomorrow";

        var weekday = date.DayOfWeek.ToString();
        var day = SpeakOrdinal(date.Day);

        // Inside the coming week the weekday alone is unambiguous and easiest to hear.
        if (delta is > 1 and <= 7)
            return $"{weekday} the {day}";

        var month = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(date.Month);
        return $"{weekday} the {day} of {month}";
    }

    /// <summary>
    /// The date with whatever preposition a sentence needs around it. "Tomorrow" takes none;
    /// a named day takes "on". Without this the agent says "What time on tomorrow?" and
    /// "there's nothing free on today" - phrasings no person has ever used, and the kind of
    /// small wrongness that makes a caller notice they are talking to a machine.
    /// </summary>
    public string SpeakDateWithPreposition(DateOnly date, DateOnly today)
    {
        var spoken = SpeakDate(date, today);
        return spoken is "today" or "tomorrow" ? spoken : $"on {spoken}";
    }

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>"tomorrow at half past four in the afternoon".</summary>
    public string SpeakDateTime(DateTime local, DateOnly today) =>
        $"{SpeakDate(DateOnly.FromDateTime(local), today)} at {SpeakTime(TimeOnly.FromDateTime(local))}";

    /// <summary>
    /// Turns a clock value into the words a person would use. Quarters and halves are spoken as
    /// quarters and halves because "sixteen forty five" is not English.
    /// </summary>
    public string SpeakTime(TimeOnly time)
    {
        if (time is { Hour: 12, Minute: 0 }) return "midday";
        if (time is { Hour: 0, Minute: 0 }) return "midnight";

        var hour12 = time.Hour % 12;
        if (hour12 == 0) hour12 = 12;

        var part = PartOfDayWords(time.Hour);

        return time.Minute switch
        {
            0 => $"{SpeakHourWord(hour12)} o'clock {part}",
            15 => $"quarter past {SpeakHourWord(hour12)} {part}",
            30 => $"half past {SpeakHourWord(hour12)} {part}",
            // The part of day describes when the caller actually comes in, not the hour being
            // counted back from - 11:45 is "quarter to twelve in the morning", and 16:45 is
            // "quarter to five in the afternoon" even though five o'clock itself is evening.
            45 => $"quarter to {SpeakHourWord(NextHour(hour12))} {part}",
            < 10 => $"{SpeakHourWord(hour12)} oh {SpeakNumberWord(time.Minute)} {part}",
            _ => $"{SpeakHourWord(hour12)} {SpeakNumberWord(time.Minute)} {part}"
        };
    }

    private static int NextHour(int hour12) => hour12 == 12 ? 1 : hour12 + 1;

    private static string PartOfDayWords(int hour24) => hour24 switch
    {
        < 12 => "in the morning",
        < 17 => "in the afternoon",
        < 21 => "in the evening",
        _ => "at night"
    };

    /// <summary>
    /// Reads a booking reference out one character at a time, with the digit/letter grouping a
    /// person can actually write down. "APT-20260917-A3F" read as a word is useless on a phone.
    /// </summary>
    public string SpeakReference(string reference)
    {
        var tail = reference.Split('-').LastOrDefault() ?? reference;
        // The tail alone is what customers are asked to quote; the prefix is internal.
        return string.Join(" ", tail.Select(c => char.IsDigit(c) ? SpeakNumberWord(c - '0') : c.ToString()));
    }

    private static readonly string[] Ones =
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
        "seventeen", "eighteen", "nineteen"
    };

    private static readonly string[] Tens =
    {
        "", "", "twenty", "thirty", "forty", "fifty"
    };

    public static string SpeakHourWord(int hour12) => Ones[hour12];

    /// <summary>Whole numbers up to 59 - enough for minutes and days of the month.</summary>
    public static string SpeakNumberWord(int value)
    {
        if (value < 20) return Ones[value];

        var tens = Tens[value / 10];
        var ones = value % 10;
        return ones == 0 ? tens : $"{tens} {Ones[ones]}";
    }

    private static readonly Dictionary<int, string> IrregularOrdinals = new()
    {
        [1] = "first", [2] = "second", [3] = "third", [5] = "fifth",
        [8] = "eighth", [9] = "ninth", [12] = "twelfth", [20] = "twentieth", [30] = "thirtieth"
    };

    public static string SpeakOrdinal(int day)
    {
        if (IrregularOrdinals.TryGetValue(day, out var irregular))
            return irregular;

        if (day < 20)
            return Ones[day] + "th";

        var tens = day / 10 * 10;
        var ones = day % 10;
        if (ones == 0)
            return Tens[tens / 10][..^1] + "ieth";

        return $"{Tens[tens / 10]} {SpeakOrdinal(ones)}";
    }
}

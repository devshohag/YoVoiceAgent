namespace CCaaS.Domain.Scheduling.DateTimeParsing;

/// <summary>
/// How much the parser actually knows about one component of a spoken date/time.
///
/// WHY THIS ENUM EXISTS
/// --------------------
/// A parser that returns only a DateTime throws away the single most useful thing it learned:
/// whether the caller SAID the value or whether the parser guessed it. "Ten thirty" and
/// "ten thirty in the morning" produce the same DateTime and must not produce the same
/// conversation - the first needs reading back for confirmation, the second does not.
///
/// The appointment state machine reads these to decide between three behaviours:
///   Explicit  -> proceed
///   Inferred  -> proceed, but say the value back before booking
///   Ambiguous -> stop and ask a closed question
/// </summary>
public enum ValueCertainty
{
    /// <summary>The utterance carried no value for this component.</summary>
    NotProvided,

    /// <summary>The caller stated it unambiguously - "half past four in the afternoon", "15 September".</summary>
    Explicit,

    /// <summary>
    /// Resolved from context rather than stated. "Four" became 16:00 because 04:00 falls
    /// outside business hours. Safe to proceed with, but must appear in the readback.
    /// </summary>
    Inferred,

    /// <summary>
    /// Two or more readings are genuinely plausible and context cannot separate them -
    /// "eight" when the business is open 08:00-20:00, or "05/06" under an unknown date order.
    /// A value is still returned so the caller can offer it, but it must be confirmed.
    /// </summary>
    Ambiguous
}

/// <summary>Coarse part of day, used to resolve a bare hour into a meridiem.</summary>
public enum DayPart
{
    None,
    Morning,
    Noon,
    Afternoon,
    Evening,
    Night
}

/// <summary>
/// Everything the parser needs from the outside world. Nothing is read from ambient state -
/// no DateTime.Now, no TimeZoneInfo.Local - because a parser that reads the clock itself
/// cannot be tested, and "tomorrow" is exactly the kind of value that must be reproducible.
/// </summary>
/// <param name="NowLocal">
/// Current time in the CONTACT's zone, not the server's. "Tomorrow" means tomorrow where the
/// caller is. Convert before calling; the parser deliberately knows nothing about zones.
/// </param>
/// <param name="BusinessOpen">Earliest bookable local time. Used to resolve bare hours.</param>
/// <param name="BusinessClose">
/// Latest bookable local time. With 09:00-18:00, "four" can only mean 16:00, because 04:00
/// is not a time this business can be visited - which is how a bare hour becomes decidable.
/// </param>
/// <param name="DayFirst">
/// How to read a numeric date like "05/06". True gives 5 June (Bangladesh, UK, most of the
/// world); false gives 6 May (United States). There is no universal answer, so the caller
/// must supply the convention for the market being served. When both readings are valid the
/// result is marked <see cref="ValueCertainty.Ambiguous"/> regardless of this setting.
/// </param>
/// <param name="PreferFuture">
/// When true, a bare weekday that has already passed today rolls to next week - said on a
/// Wednesday afternoon, "Monday" means the coming Monday, not two days ago.
/// </param>
public sealed record DateTimeParseContext(
    DateTime NowLocal,
    TimeOnly BusinessOpen,
    TimeOnly BusinessClose,
    bool DayFirst = true,
    bool PreferFuture = true)
{
    /// <summary>Sensible default for an appointment line: 09:00-18:00, day-first dates.</summary>
    public static DateTimeParseContext ForBusinessHours(DateTime nowLocal) =>
        new(nowLocal, new TimeOnly(9, 0), new TimeOnly(18, 0));
}

/// <summary>
/// The outcome of parsing one utterance.
///
/// Date and Time are independent and either may be null: a conversation collects them across
/// turns ("next Tuesday" ... "at ten"), so a partial result is a normal success, not a
/// failure. Returning null for the whole thing whenever one half is missing would force the
/// state machine to re-parse the accumulated transcript on every turn.
/// </summary>
public sealed record DateTimeParseResult
{
    public static readonly DateTimeParseResult None = new();

    public DateOnly? Date { get; init; }
    public TimeOnly? Time { get; init; }

    public ValueCertainty DateCertainty { get; init; } = ValueCertainty.NotProvided;
    public ValueCertainty TimeCertainty { get; init; } = ValueCertainty.NotProvided;

    /// <summary>Part of day the caller mentioned, if any. Kept for phrasing the readback naturally.</summary>
    public DayPart DayPart { get; init; } = DayPart.None;

    /// <summary>
    /// True when Date and Time together land at or before <c>NowLocal</c>. The parser reports
    /// this rather than silently correcting it: "today at nine" said at 5pm is a mistake worth
    /// asking about, and quietly moving it to tomorrow would book the wrong day.
    /// </summary>
    public bool IsInPast { get; init; }

    /// <summary>
    /// Human-readable trail of how values were resolved. Diagnostic and test material - never
    /// spoken to the caller, and never parsed by other code.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool HasDate => Date.HasValue;
    public bool HasTime => Time.HasValue;
    public bool HasAnything => HasDate || HasTime;

    /// <summary>True when both halves are present and neither needs confirming.</summary>
    public bool IsComplete =>
        HasDate && HasTime
        && DateCertainty is ValueCertainty.Explicit or ValueCertainty.Inferred
        && TimeCertainty is ValueCertainty.Explicit or ValueCertainty.Inferred;

    /// <summary>True when anything at all was guessed or is contested, so a readback is owed.</summary>
    public bool NeedsConfirmation =>
        DateCertainty is ValueCertainty.Inferred or ValueCertainty.Ambiguous
        || TimeCertainty is ValueCertainty.Inferred or ValueCertainty.Ambiguous;

    /// <summary>True when the caller must be asked a closed question before proceeding.</summary>
    public bool IsAmbiguous =>
        DateCertainty == ValueCertainty.Ambiguous || TimeCertainty == ValueCertainty.Ambiguous;

    /// <summary>Combines the two halves. Null unless both are present.</summary>
    public DateTime? ToDateTime() =>
        Date.HasValue && Time.HasValue ? Date.Value.ToDateTime(Time.Value) : null;
}

/// <summary>
/// Turns a spoken date/time phrase into structured values.
///
/// Implementations are per-language. The interface exists so that adding Bangla later is a
/// new class and a registration change rather than an edit to the appointment state machine -
/// which is the whole reason the English-first decision costs nothing later.
/// </summary>
public interface IDateTimeParser
{
    /// <summary>Language this parser handles, as a BCP-47 tag, e.g. "en".</summary>
    string Language { get; }

    /// <summary>
    /// Extracts whatever date and time information <paramref name="utterance"/> contains.
    /// Never throws and never returns null: an unparseable utterance yields
    /// <see cref="DateTimeParseResult.None"/>, because a caller saying something unrelated is
    /// ordinary conversation rather than an error.
    /// </summary>
    /// <param name="utterance">
    /// Raw transcript text. Expect no punctuation, spelled-out numbers and filler words -
    /// speech recognition output, not typed input.
    /// </param>
    DateTimeParseResult Parse(string? utterance, DateTimeParseContext context);
}

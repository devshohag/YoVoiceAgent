namespace CCaaS.Domain.Scheduling.DateTimeParsing;

/// <summary>
/// Word-to-number tables for spoken dates and times.
///
/// Speech recognition transcribes numbers as words far more often than as digits - "ten
/// thirty" rather than "10:30", "the fifteenth" rather than "the 15th". A parser that only
/// reads digits therefore fails on the majority of real utterances.
///
/// Coverage is deliberately bounded to what a clock and a calendar need: hours 1-12,
/// the minute values people actually say, and days 1-31. Arbitrary numerals are not
/// supported because no appointment is ever made "at forty seven minutes past two".
/// </summary>
internal static class SpokenNumbers
{
    /// <summary>Hour names, one through twelve, plus "twelve"-adjacent forms.</summary>
    public static readonly Dictionary<string, int> Hours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12
    };

    /// <summary>
    /// Minute values as spoken. "Quarter" and "half" are included because they appear in
    /// "quarter past" / "half past" constructions, which are the commonest spoken forms.
    /// </summary>
    public static readonly Dictionary<string, int> Minutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["oclock"] = 0, ["o clock"] = 0,
        ["five"] = 5, ["ten"] = 10, ["fifteen"] = 15, ["quarter"] = 15,
        ["twenty"] = 20, ["twenty five"] = 25, ["twentyfive"] = 25,
        ["thirty"] = 30, ["half"] = 30,
        ["thirty five"] = 35, ["forty"] = 40, ["forty five"] = 45,
        ["fifty"] = 50, ["fifty five"] = 55
    };

    /// <summary>
    /// Minutes as a bare trailing word, i.e. the "thirty" in "ten thirty". Excludes "quarter"
    /// and "half", which are never used this way - nobody says "ten half".
    /// </summary>
    public static readonly Dictionary<string, int> TrailingMinutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["oh five"] = 5, ["ohfive"] = 5, ["zero five"] = 5,
        ["ten"] = 10, ["fifteen"] = 15, ["twenty"] = 20, ["twenty five"] = 25,
        ["thirty"] = 30, ["thirty five"] = 35, ["forty"] = 40, ["forty five"] = 45,
        ["fifty"] = 50, ["fifty five"] = 55
    };

    /// <summary>Ordinal and cardinal day names, "first" through "thirty first".</summary>
    public static readonly Dictionary<string, int> Days = BuildDays();

    public static readonly Dictionary<string, DayOfWeek> Weekdays = new(StringComparer.OrdinalIgnoreCase)
    {
        ["monday"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday
    };

    public static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["january"] = 1, ["jan"] = 1,
        ["february"] = 2, ["feb"] = 2,
        ["march"] = 3, ["mar"] = 3,
        ["april"] = 4, ["apr"] = 4,
        ["may"] = 5,
        ["june"] = 6, ["jun"] = 6,
        ["july"] = 7, ["jul"] = 7,
        ["august"] = 8, ["aug"] = 8,
        ["september"] = 9, ["sep"] = 9, ["sept"] = 9,
        ["october"] = 10, ["oct"] = 10,
        ["november"] = 11, ["nov"] = 11,
        ["december"] = 12, ["dec"] = 12
    };

    /// <summary>
    /// Words speech recognition inserts that carry no scheduling meaning. Removed before
    /// pattern matching so "um tomorrow at uh ten" behaves like "tomorrow at ten".
    /// </summary>
    public static readonly HashSet<string> FillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "um", "uh", "umm", "uhh", "er", "erm", "ah", "hmm", "mm",
        "like", "well", "so", "okay", "ok", "yeah", "yes", "please", "thanks", "thank", "you",
        "id", "ill", "lets", "want", "would", "could", "can", "book", "booking", "appointment",
        "schedule", "make", "get", "give", "me", "my", "for", "a", "an", "the", "is", "it",
        "possible", "available", "time", "slot", "do", "have", "any"
    };

    private static Dictionary<string, int> BuildDays()
    {
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5,
            ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9, ["tenth"] = 10,
            ["eleventh"] = 11, ["twelfth"] = 12, ["thirteenth"] = 13, ["fourteenth"] = 14,
            ["fifteenth"] = 15, ["sixteenth"] = 16, ["seventeenth"] = 17, ["eighteenth"] = 18,
            ["nineteenth"] = 19, ["twentieth"] = 20, ["twenty first"] = 21, ["twenty second"] = 22,
            ["twenty third"] = 23, ["twenty fourth"] = 24, ["twenty fifth"] = 25,
            ["twenty sixth"] = 26, ["twenty seventh"] = 27, ["twenty eighth"] = 28,
            ["twenty ninth"] = 29, ["thirtieth"] = 30, ["thirty first"] = 31
        };

        // Hyphenated forms arrive from some recognisers ("twenty-first"); the normaliser turns
        // hyphens into spaces, but the un-spaced variant is cheap to accept as well.
        foreach (var (word, value) in ordinals.ToList())
            if (word.Contains(' '))
                ordinals[word.Replace(" ", "")] = value;

        return ordinals;
    }
}

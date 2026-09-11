using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CCaaS.Domain.Scheduling.DateTimeParsing;

/// <summary>
/// Parses English spoken dates and times out of a call transcript.
///
/// DESIGN NOTES
/// ------------
/// 1. The input is speech, not typing. There is no punctuation, numbers arrive as words as
///    often as digits, meridiems arrive as "a m", and filler words are everywhere. The
///    normaliser deals with that before any pattern runs.
///
/// 2. A bare hour is decidable, and business hours are what decide it. "Four" can only mean
///    16:00 for a business open 09:00-18:00, because 04:00 is not a time anyone can visit.
///    This single rule removes most of the clarifying questions a naive parser would ask.
///
/// 3. Where inference happens, it is reported rather than hidden. See
///    <see cref="ValueCertainty"/> - the caller needs to know the difference between what was
///    said and what was guessed, because one needs a readback and the other does not.
///
/// 4. Patterns are ordered most-specific-first and each consumes the text it matched, so
///    "the 15th at 10:30" cannot have its "15" re-read as minutes.
///
/// WEEKDAY CONVENTION
/// ------------------
/// English is genuinely ambiguous about "next Monday" - said on a Wednesday it can mean in
/// five days or in twelve. This parser resolves BOTH "next Monday" and bare "Monday" to the
/// next occurrence strictly in the future, which is what callers overwhelmingly mean when
/// booking, and is the same convention every calendar application uses. The difference is
/// only that a bare weekday may resolve to today if the time has not yet passed.
/// </summary>
public sealed class EnglishDateTimeParser : IDateTimeParser
{
    public string Language => "en";

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // ---- normalisation -------------------------------------------------------------------

    private static readonly Regex MeridiemAm = new(@"\ba\.?\s*m\.?\b", Opts);
    private static readonly Regex MeridiemPm = new(@"\bp\.?\s*m\.?\b", Opts);
    private static readonly Regex LoneDot = new(@"(?<!\d)\.(?!\d)", Opts);
    private static readonly Regex Whitespace = new(@"\s+", Opts);

    // ---- relative offsets ("in half an hour") --------------------------------------------

    private static readonly Regex InHalfHour = new(@"\bin\s+half\s+a?n?\s*hour\b", Opts);
    private static readonly Regex InOneHour = new(@"\bin\s+(?:a|an|one)\s+hour\b", Opts);
    private static readonly Regex InMinutes = new(@"\bin\s+(\d{1,3}|[a-z]+)\s+min(?:ute)?s?\b", Opts);
    private static readonly Regex InHours = new(@"\bin\s+(\d{1,2}|[a-z]+)\s+h(?:ou)?rs?\b", Opts);

    // ---- dates ---------------------------------------------------------------------------

    private static readonly Regex IsoDate = new(@"\b(\d{4})-(\d{1,2})-(\d{1,2})\b", Opts);

    // The negative lookahead keeps "10.30 am" out of the date patterns. European dates and
    // dotted times share a separator, and the meridiem is the only thing that tells them
    // apart - without it, a time would be silently read as the 10th of March.
    private static readonly Regex NumericDate =
        new(@"\b(\d{1,2})[/\-.](\d{1,2})(?:[/\-.](\d{2,4}))?\b(?!\s*(?:am|pm)\b)", Opts);
    private static readonly Regex DayAfterTomorrow = new(@"\b(?:day\s+after\s+tomorrow|overmorrow)\b", Opts);
    private static readonly Regex Tomorrow = new(@"\btomorrow\b", Opts);
    private static readonly Regex Today = new(@"\btoday\b", Opts);
    private static readonly Regex Tonight = new(@"\btonight\b", Opts);
    private static readonly Regex Yesterday = new(@"\byesterday\b", Opts);
    private static readonly Regex NextWeek = new(@"\bnext\s+week\b", Opts);
    private static readonly Regex InDays = new(@"\bin\s+(\d{1,3}|[a-z]+)\s+days?\b", Opts);

    private static readonly Regex NextWeekday = new(@"\bnext\s+([a-z]+)\b", Opts);
    private static readonly Regex QualifiedWeekday = new(@"\b(?:this|coming|upcoming|on)\s+([a-z]+)\b", Opts);
    private static readonly Regex BareWeekday = new(@"\b([a-z]+)day\b|\b(mon|tue|tues|wed|thu|thur|thurs|fri|sat|sun)\b", Opts);

    private static readonly Regex MonthThenDay = new(@"\b([a-z]+)\s+(\d{1,2})(?:st|nd|rd|th)?(?:\s+(\d{4}))?\b", Opts);
    private static readonly Regex DayThenMonth = new(@"\b(\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?([a-z]+)(?:\s+(\d{4}))?\b", Opts);
    private static readonly Regex NumericOrdinalDay = new(@"\b(\d{1,2})(?:st|nd|rd|th)\b", Opts);

    // ---- times ---------------------------------------------------------------------------

    private static readonly Regex Noon = new(@"\b(?:noon|midday)\b", Opts);
    private static readonly Regex Midnight = new(@"\bmidnight\b", Opts);
    // A dot is accepted alongside the colon here because the meridiem disambiguates it;
    // HourMinute24 below stays colon-only so "15.10" remains readable as a date.
    private static readonly Regex HourMinuteMeridiem = new(@"\b(\d{1,2})[:.](\d{2})\s*(am|pm)\b", Opts);
    private static readonly Regex HourMeridiem = new(@"\b(\d{1,2})\s*(am|pm)\b", Opts);
    private static readonly Regex HourMinute24 = new(@"\b(\d{1,2}):(\d{2})\b", Opts);

    private static readonly HashSet<string> PastWords =
        new(StringComparer.OrdinalIgnoreCase) { "past", "after" };

    private static readonly HashSet<string> ToWords =
        new(StringComparer.OrdinalIgnoreCase) { "to", "till", "before" };
    private static readonly Regex OClock = new(@"\b(\d{1,2}|[a-z]+)\s+oclock\b", Opts);
    private static readonly Regex WordHourWordMinute = new(@"\b([a-z]+)\s+([a-z]+(?:\s+[a-z]+)?)\b", Opts);
    private static readonly Regex AtHourSpaceMinute = new(@"\b(?:at|around|about|by)\s+(\d{1,2})\s+(\d{2})\b", Opts);
    private static readonly Regex AtCompactTime = new(@"\b(?:at|around|about|by)\s+(\d{3,4})\b", Opts);
    private static readonly Regex MarkedBareHour = new(@"\b(?:at|around|about|by|from)\s+(\d{1,2}|[a-z]+)\b", Opts);
    private static readonly Regex UnmarkedBareHour = new(@"\b(\d{1,2}|[a-z]+)\b", Opts);

    // ---- day parts -----------------------------------------------------------------------

    // Only the word "morning" - NOT "am". "am" is a meridiem marker handled by the explicit
    // time patterns, and treating it as a part of day would read "I am free at four" as a
    // morning appointment and book 04:00.
    private static readonly Regex MorningPart = new(@"\bmorning\b", Opts);
    private static readonly Regex NoonPart = new(@"\b(?:noon|midday)\b", Opts);
    private static readonly Regex AfternoonPart = new(@"\bafternoon\b", Opts);
    private static readonly Regex EveningPart = new(@"\bevening\b", Opts);
    private static readonly Regex NightPart = new(@"\b(?:night|tonight)\b", Opts);

    public DateTimeParseResult Parse(string? utterance, DateTimeParseContext context)
    {
        if (string.IsNullOrWhiteSpace(utterance))
            return DateTimeParseResult.None;

        var normalized = Normalize(utterance);
        if (normalized.Length == 0)
            return DateTimeParseResult.None;

        var notes = new List<string>();

        // Relative offsets are matched before filler removal, because the filler list strips
        // articles and "in an hour" would otherwise lose the word it depends on.
        var offset = TryRelativeOffset(normalized, context, notes);
        if (offset is not null)
            return offset;

        var text = RemoveFiller(normalized);

        var dayPartEarly = PeekDayPart(normalized);
        var date = ExtractDate(ref text, context, notes);
        var dayPart = ExtractDayPart(ref text);
        if (dayPart == DayPart.None)
            dayPart = dayPartEarly;

        var time = ExtractTime(text, dayPart, context, notes);

        return Assemble(date, time, dayPart, context, notes);
    }

    // =====================================================================================
    // normalisation
    // =====================================================================================

    /// <summary>
    /// Turns raw transcript text into a predictable shape: lower case, "a m"/"p.m." collapsed
    /// to "am"/"pm", apostrophes gone so "o'clock" becomes "oclock", sentence punctuation
    /// removed but digit separators kept so "15/09" and "10:30" survive.
    /// </summary>
    private static string Normalize(string raw)
    {
        var text = raw.ToLowerInvariant();

        text = MeridiemAm.Replace(text, " am ");
        text = MeridiemPm.Replace(text, " pm ");
        text = text.Replace("'", "").Replace("’", "");
        text = LoneDot.Replace(text, " ");

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || c is ':' or '/' or '-' or '.' or ' ')
                builder.Append(c);
            else
                builder.Append(' ');
        }

        return Whitespace.Replace(builder.ToString(), " ").Trim();
    }

    private static string RemoveFiller(string text)
    {
        var kept = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !SpokenNumbers.FillerWords.Contains(word))
            .ToArray();

        return kept.Length == 0 ? string.Empty : string.Join(' ', kept);
    }

    /// <summary>Blanks out a matched span so later, looser patterns cannot re-read it.</summary>
    private static string Consume(string text, Match match) =>
        Whitespace.Replace(
            text.Remove(match.Index, match.Length).Insert(match.Index, new string(' ', match.Length)),
            " ").Trim();

    // =====================================================================================
    // relative offsets
    // =====================================================================================

    private static DateTimeParseResult? TryRelativeOffset(
        string text, DateTimeParseContext context, List<string> notes)
    {
        TimeSpan? offset = null;

        if (InHalfHour.IsMatch(text))
        {
            offset = TimeSpan.FromMinutes(30);
        }
        else if (InOneHour.IsMatch(text))
        {
            offset = TimeSpan.FromHours(1);
        }
        else
        {
            var minutes = InMinutes.Match(text);
            if (minutes.Success && TryReadCount(minutes.Groups[1].Value, out var m))
                offset = TimeSpan.FromMinutes(m);

            if (offset is null)
            {
                var hours = InHours.Match(text);
                if (hours.Success && TryReadCount(hours.Groups[1].Value, out var h))
                    offset = TimeSpan.FromHours(h);
            }
        }

        if (offset is null)
            return null;

        var target = context.NowLocal.Add(offset.Value);
        notes.Add($"relative offset of {offset.Value.TotalMinutes:0} minutes from now");

        return new DateTimeParseResult
        {
            Date = DateOnly.FromDateTime(target),
            Time = TimeOnly.FromDateTime(target),
            DateCertainty = ValueCertainty.Explicit,
            TimeCertainty = ValueCertainty.Explicit,
            IsInPast = false,
            Notes = notes
        };
    }

    /// <summary>Reads a count that may be digits ("30") or a word ("two").</summary>
    private static bool TryReadCount(string token, out int value)
    {
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return value > 0;

        if (SpokenNumbers.Hours.TryGetValue(token, out value))
            return true;

        if (SpokenNumbers.Minutes.TryGetValue(token, out value))
            return value > 0;

        value = 0;
        return false;
    }

    // =====================================================================================
    // date extraction
    // =====================================================================================

    private readonly record struct DatePart(
        DateOnly? Date,
        ValueCertainty Certainty,
        bool RollsForwardWhenPast);

    private static DatePart ExtractDate(ref string text, DateTimeParseContext context, List<string> notes)
    {
        var today = DateOnly.FromDateTime(context.NowLocal);

        // ISO first: unambiguous by construction, so nothing else may claim those digits.
        var iso = IsoDate.Match(text);
        if (iso.Success
            && TryBuildDate(int.Parse(iso.Groups[1].Value), int.Parse(iso.Groups[2].Value),
                int.Parse(iso.Groups[3].Value), out var isoDate))
        {
            text = Consume(text, iso);
            return new DatePart(isoDate, ValueCertainty.Explicit, false);
        }

        if (DayAfterTomorrow.Match(text) is { Success: true } dat)
        {
            text = Consume(text, dat);
            return new DatePart(today.AddDays(2), ValueCertainty.Explicit, false);
        }

        if (Tomorrow.Match(text) is { Success: true } tom)
        {
            text = Consume(text, tom);
            return new DatePart(today.AddDays(1), ValueCertainty.Explicit, false);
        }

        if (Today.Match(text) is { Success: true } tod)
        {
            text = Consume(text, tod);
            return new DatePart(today, ValueCertainty.Explicit, false);
        }

        if (Tonight.Match(text) is { Success: true })
        {
            // "tonight" is consumed by the day-part pass, not here - it carries both meanings.
            return new DatePart(today, ValueCertainty.Explicit, false);
        }

        if (Yesterday.Match(text) is { Success: true } yest)
        {
            text = Consume(text, yest);
            notes.Add("utterance referred to a past date");
            return new DatePart(today.AddDays(-1), ValueCertainty.Explicit, false);
        }

        var inDays = InDays.Match(text);
        if (inDays.Success && TryReadCount(inDays.Groups[1].Value, out var dayCount))
        {
            text = Consume(text, inDays);
            return new DatePart(today.AddDays(dayCount), ValueCertainty.Explicit, false);
        }

        if (NextWeek.Match(text) is { Success: true } nw)
        {
            text = Consume(text, nw);
            return new DatePart(today.AddDays(7), ValueCertainty.Explicit, false);
        }

        var nextDow = NextWeekday.Match(text);
        if (nextDow.Success && SpokenNumbers.Weekdays.TryGetValue(nextDow.Groups[1].Value, out var nextTarget))
        {
            text = Consume(text, nextDow);
            var resolved = NextOccurrence(today, nextTarget, includeToday: false);
            notes.Add($"'next {nextTarget}' resolved to the next occurrence, {resolved:yyyy-MM-dd}");
            return new DatePart(resolved, ValueCertainty.Explicit, false);
        }

        var qualifiedDow = QualifiedWeekday.Match(text);
        if (qualifiedDow.Success && SpokenNumbers.Weekdays.TryGetValue(qualifiedDow.Groups[1].Value, out var qualTarget))
        {
            text = Consume(text, qualifiedDow);
            return new DatePart(NextOccurrence(today, qualTarget, includeToday: true),
                ValueCertainty.Explicit, true);
        }

        var monthDay = MatchMonthAndDay(text, context, out var monthDate, out var monthMatch, out var monthAmbiguous);
        if (monthDay)
        {
            text = Consume(text, monthMatch);
            return new DatePart(monthDate,
                monthAmbiguous ? ValueCertainty.Ambiguous : ValueCertainty.Explicit, false);
        }

        var numeric = NumericDate.Match(text);
        if (numeric.Success)
        {
            var resolved = ResolveNumericDate(numeric, context, notes, out var ambiguous);
            if (resolved is not null)
            {
                text = Consume(text, numeric);
                return new DatePart(resolved, ambiguous ? ValueCertainty.Ambiguous : ValueCertainty.Explicit, false);
            }
        }

        var ordinal = NumericOrdinalDay.Match(text);
        if (ordinal.Success)
        {
            var day = int.Parse(ordinal.Groups[1].Value, CultureInfo.InvariantCulture);
            var resolved = ResolveDayOfMonth(day, today, notes);
            if (resolved is not null)
            {
                text = Consume(text, ordinal);
                return new DatePart(resolved, ValueCertainty.Explicit, false);
            }
        }

        if (MatchWordOrdinalDay(text, today, notes, out var wordDate, out var wordPhrase))
        {
            text = ConsumePhrase(text, wordPhrase);
            return new DatePart(wordDate, ValueCertainty.Explicit, false);
        }

        var bareDow = MatchBareWeekday(text, out var bareTarget, out var bareMatch);
        if (bareDow)
        {
            text = Consume(text, bareMatch);
            return new DatePart(NextOccurrence(today, bareTarget, includeToday: true),
                ValueCertainty.Explicit, true);
        }

        return new DatePart(null, ValueCertainty.NotProvided, false);
    }

    private static bool MatchBareWeekday(string text, out DayOfWeek target, out Match match)
    {
        foreach (Match candidate in BareWeekday.Matches(text))
        {
            var token = candidate.Value.Trim();
            if (SpokenNumbers.Weekdays.TryGetValue(token, out target))
            {
                match = candidate;
                return true;
            }
        }

        target = default;
        match = Match.Empty;
        return false;
    }

    private static bool MatchMonthAndDay(
        string text, DateTimeParseContext context,
        out DateOnly? date, out Match match, out bool ambiguous)
    {
        date = null;
        ambiguous = false;

        foreach (Match candidate in MonthThenDay.Matches(text))
        {
            if (!SpokenNumbers.Months.TryGetValue(candidate.Groups[1].Value, out var month))
                continue;

            var day = int.Parse(candidate.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = ReadYear(candidate.Groups[3].Value, context, month, day);
            if (!TryBuildDate(year, month, day, out var built))
                continue;

            date = built;
            match = candidate;
            return true;
        }

        foreach (Match candidate in DayThenMonth.Matches(text))
        {
            if (!SpokenNumbers.Months.TryGetValue(candidate.Groups[2].Value, out var month))
                continue;

            var day = int.Parse(candidate.Groups[1].Value, CultureInfo.InvariantCulture);
            var year = ReadYear(candidate.Groups[3].Value, context, month, day);
            if (!TryBuildDate(year, month, day, out var built))
                continue;

            date = built;
            match = candidate;
            return true;
        }

        match = Match.Empty;
        return false;
    }

    private static bool MatchWordOrdinalDay(
        string text, DateOnly today, List<string> notes, out DateOnly? date, out string phrase)
    {
        // Two-word ordinals ("twenty first") must be tried before single words, otherwise
        // "twenty" would win and produce the 20th.
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            for (var length = Math.Min(2, words.Length - i); length >= 1; length--)
            {
                var candidate = string.Join(' ', words.Skip(i).Take(length));
                if (!SpokenNumbers.Days.TryGetValue(candidate, out var day))
                    continue;

                var resolved = ResolveDayOfMonth(day, today, notes);
                if (resolved is null)
                    continue;

                date = resolved;
                phrase = candidate;
                return true;
            }
        }

        date = null;
        phrase = string.Empty;
        return false;
    }

    /// <summary>Blanks the first occurrence of a literal phrase, mirroring <see cref="Consume"/>.</summary>
    private static string ConsumePhrase(string text, string phrase)
    {
        var index = text.IndexOf(phrase, StringComparison.Ordinal);
        if (index < 0)
            return text;

        return Whitespace.Replace(
            text.Remove(index, phrase.Length).Insert(index, new string(' ', phrase.Length)),
            " ").Trim();
    }

    /// <summary>
    /// A day number with no month means the soonest month in which that day is still ahead.
    /// Said on the 20th, "the fifth" means next month's fifth - because a caller booking an
    /// appointment is never asking for a date that has already gone.
    /// </summary>
    private static DateOnly? ResolveDayOfMonth(int day, DateOnly today, List<string> notes)
    {
        if (day is < 1 or > 31)
            return null;

        if (day >= today.Day && day <= DateTime.DaysInMonth(today.Year, today.Month))
            return new DateOnly(today.Year, today.Month, day);

        var nextMonth = today.AddMonths(1);
        if (day > DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month))
            return null;

        notes.Add($"day {day} has passed this month, resolved to {nextMonth.Year:0000}-{nextMonth.Month:00}-{day:00}");
        return new DateOnly(nextMonth.Year, nextMonth.Month, day);
    }

    /// <summary>
    /// Resolves "5/6" style dates. The reading depends on convention, so
    /// <see cref="DateTimeParseContext.DayFirst"/> decides - and when BOTH readings are valid
    /// calendar dates the result is flagged ambiguous, because no amount of context can tell
    /// 5 June from 6 May and guessing silently is how the wrong appointment gets booked.
    /// </summary>
    private static DateOnly? ResolveNumericDate(
        Match match, DateTimeParseContext context, List<string> notes, out bool ambiguous)
    {
        ambiguous = false;

        var first = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var second = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var yearText = match.Groups[3].Value;

        var dayFirstValid = TryBuildDate(ReadYear(yearText, context, second, first), second, first, out var asDayFirst);
        var monthFirstValid = TryBuildDate(ReadYear(yearText, context, first, second), first, second, out var asMonthFirst);

        if (!dayFirstValid && !monthFirstValid)
            return null;

        if (dayFirstValid && monthFirstValid && first != second)
        {
            ambiguous = true;
            notes.Add($"'{match.Value}' reads as both {asDayFirst:yyyy-MM-dd} and {asMonthFirst:yyyy-MM-dd}; " +
                      $"used the {(context.DayFirst ? "day-first" : "month-first")} convention");
        }

        if (context.DayFirst)
            return dayFirstValid ? asDayFirst : asMonthFirst;

        return monthFirstValid ? asMonthFirst : asDayFirst;
    }

    /// <summary>
    /// Picks a year for a date given without one: this year if the date is still ahead,
    /// otherwise next year. Two-digit years are read as 2000-2099.
    /// </summary>
    private static int ReadYear(string yearText, DateTimeParseContext context, int month, int day)
    {
        if (!string.IsNullOrEmpty(yearText)
            && int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var explicitYear))
            return explicitYear < 100 ? 2000 + explicitYear : explicitYear;

        var today = DateOnly.FromDateTime(context.NowLocal);
        if (!TryBuildDate(today.Year, month, day, out var thisYear))
            return today.Year;

        return thisYear < today ? today.Year + 1 : today.Year;
    }

    private static bool TryBuildDate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1)
            return false;
        if (day > DateTime.DaysInMonth(year, month))
            return false;

        date = new DateOnly(year, month, day);
        return true;
    }

    private static DateOnly NextOccurrence(DateOnly from, DayOfWeek target, bool includeToday)
    {
        var delta = ((int)target - (int)from.DayOfWeek + 7) % 7;
        if (delta == 0 && !includeToday)
            delta = 7;
        return from.AddDays(delta);
    }

    // =====================================================================================
    // day part
    // =====================================================================================

    private static DayPart PeekDayPart(string text)
    {
        if (NightPart.IsMatch(text)) return DayPart.Night;
        if (AfternoonPart.IsMatch(text)) return DayPart.Afternoon;
        if (EveningPart.IsMatch(text)) return DayPart.Evening;
        if (NoonPart.IsMatch(text)) return DayPart.Noon;
        if (MorningPart.IsMatch(text)) return DayPart.Morning;
        return DayPart.None;
    }

    private static DayPart ExtractDayPart(ref string text)
    {
        // "noon" and "midnight" are absolute times, so they are left for the time pass and
        // only reported as a part-of-day hint.
        if (NightPart.Match(text) is { Success: true } night)
        {
            text = Consume(text, night);
            return DayPart.Night;
        }

        if (AfternoonPart.Match(text) is { Success: true } afternoon)
        {
            text = Consume(text, afternoon);
            return DayPart.Afternoon;
        }

        if (EveningPart.Match(text) is { Success: true } evening)
        {
            text = Consume(text, evening);
            return DayPart.Evening;
        }

        if (NoonPart.IsMatch(text))
            return DayPart.Noon;

        if (MorningPart.Match(text) is { Success: true } morning)
        {
            // "am" doubles as the meridiem marker, so only the word "morning" is consumed.
            if (string.Equals(morning.Value, "morning", StringComparison.OrdinalIgnoreCase))
                text = Consume(text, morning);
            return DayPart.Morning;
        }

        return DayPart.None;
    }

    // =====================================================================================
    // time extraction
    // =====================================================================================

    private readonly record struct TimePart(TimeOnly? Time, ValueCertainty Certainty);

    private static TimePart ExtractTime(
        string text, DayPart dayPart, DateTimeParseContext context, List<string> notes)
    {
        if (Midnight.IsMatch(text))
            return new TimePart(new TimeOnly(0, 0), ValueCertainty.Explicit);

        if (Noon.IsMatch(text))
            return new TimePart(new TimeOnly(12, 0), ValueCertainty.Explicit);

        var withMeridiem = HourMinuteMeridiem.Match(text);
        if (withMeridiem.Success)
        {
            var hour = int.Parse(withMeridiem.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(withMeridiem.Groups[2].Value, CultureInfo.InvariantCulture);
            var pm = withMeridiem.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            return BuildExplicit(hour, minute, pm);
        }

        var hourMeridiem = HourMeridiem.Match(text);
        if (hourMeridiem.Success)
        {
            var hour = int.Parse(hourMeridiem.Groups[1].Value, CultureInfo.InvariantCulture);
            var pm = hourMeridiem.Groups[2].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            return BuildExplicit(hour, 0, pm);
        }

        var hm24 = HourMinute24.Match(text);
        if (hm24.Success)
        {
            var hour = int.Parse(hm24.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(hm24.Groups[2].Value, CultureInfo.InvariantCulture);
            if (minute < 60)
                return Resolve(hour, minute, dayPart, context, notes);
        }

        var pastTo = MatchPastOrTo(text, dayPart, context, notes);
        if (pastTo is not null)
            return pastTo.Value;

        var oclock = OClock.Match(text);
        if (oclock.Success && TryReadHour(oclock.Groups[1].Value, out var oclockHour))
            return Resolve(oclockHour, 0, dayPart, context, notes);

        var spaced = AtHourSpaceMinute.Match(text);
        if (spaced.Success)
        {
            var hour = int.Parse(spaced.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(spaced.Groups[2].Value, CultureInfo.InvariantCulture);
            if (minute < 60)
                return Resolve(hour, minute, dayPart, context, notes);
        }

        var compact = AtCompactTime.Match(text);
        if (compact.Success)
        {
            var digits = compact.Groups[1].Value;
            var hour = int.Parse(digits[..^2], CultureInfo.InvariantCulture);
            var minute = int.Parse(digits[^2..], CultureInfo.InvariantCulture);
            if (minute < 60)
                return Resolve(hour, minute, dayPart, context, notes);
        }

        var wordPair = MatchWordHourAndMinute(text, dayPart, context, notes);
        if (wordPair is not null)
            return wordPair.Value;

        var marked = MarkedBareHour.Match(text);
        if (marked.Success && TryReadHour(marked.Groups[1].Value, out var markedHour))
            return Resolve(markedHour, 0, dayPart, context, notes);

        foreach (Match candidate in UnmarkedBareHour.Matches(text))
        {
            if (TryReadHour(candidate.Groups[1].Value, out var bareHour))
                return Resolve(bareHour, 0, dayPart, context, notes);
        }

        // A part of day on its own carries no clock time, but it is still information the
        // state machine can use to ask a narrower question.
        return new TimePart(null, ValueCertainty.NotProvided);
    }

    /// <summary>
    /// Reads "half past four", "quarter to five", "twenty five past ten".
    ///
    /// Scanned word by word rather than by one regex: a regex whose minute group can span two
    /// words takes them greedily, so "at half past four" yields the minute phrase "at half",
    /// which matches nothing, and the whole construction is missed. Anchoring on the
    /// past/to word and then looking outward gets the right span every time.
    /// </summary>
    private static TimePart? MatchPastOrTo(
        string text, DayPart dayPart, DateTimeParseContext context, List<string> notes)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < words.Length; i++)
        {
            var isPast = PastWords.Contains(words[i]);
            var isTo = ToWords.Contains(words[i]);
            if (!isPast && !isTo)
                continue;

            var minute = 0;
            var found = false;

            // Longest minute phrase first: "twenty five" must win over "five".
            for (var length = 2; length >= 1 && !found; length--)
            {
                if (i - length < 0)
                    continue;

                var phrase = string.Join(' ', words.Skip(i - length).Take(length));
                if (SpokenNumbers.Minutes.TryGetValue(phrase, out var value) && value > 0)
                {
                    minute = value;
                    found = true;
                }
            }

            if (!found || i + 1 >= words.Length)
                continue;

            if (!TryReadHour(words[i + 1], out var hour))
                continue;

            if (isTo)
            {
                minute = 60 - minute;
                hour = hour == 1 ? 12 : hour - 1;
            }

            return Resolve(hour, minute, dayPart, context, notes);
        }

        return null;
    }

    private static TimePart? MatchWordHourAndMinute(
        string text, DayPart dayPart, DateTimeParseContext context, List<string> notes)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length - 1; i++)
        {
            if (!SpokenNumbers.Hours.TryGetValue(words[i], out var hour))
                continue;

            for (var length = Math.Min(2, words.Length - i - 1); length >= 1; length--)
            {
                var phrase = string.Join(' ', words.Skip(i + 1).Take(length));
                if (SpokenNumbers.TrailingMinutes.TryGetValue(phrase, out var minute))
                    return Resolve(hour, minute, dayPart, context, notes);
            }
        }

        return null;
    }

    private static bool TryReadHour(string token, out int hour)
    {
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out hour))
            return hour is >= 0 and <= 23;

        return SpokenNumbers.Hours.TryGetValue(token, out hour);
    }

    private static TimePart BuildExplicit(int hour12, int minute, bool pm)
    {
        if (hour12 is < 1 or > 12 || minute is < 0 or > 59)
            return new TimePart(null, ValueCertainty.NotProvided);

        var hour = hour12 % 12 + (pm ? 12 : 0);
        return new TimePart(new TimeOnly(hour, minute), ValueCertainty.Explicit);
    }

    /// <summary>
    /// Turns an hour the caller did not qualify into a real time.
    ///
    /// Order of authority: a stated part of day wins; otherwise business hours decide; only
    /// when business hours admit both readings, or neither, is the value contested.
    /// </summary>
    private static TimePart Resolve(
        int hour, int minute, DayPart dayPart, DateTimeParseContext context, List<string> notes)
    {
        if (minute is < 0 or > 59)
            return new TimePart(null, ValueCertainty.NotProvided);

        // 13-23 can only be one thing, and 0 is already midnight.
        if (hour is >= 13 and <= 23 || hour == 0)
            return new TimePart(new TimeOnly(hour, minute), ValueCertainty.Explicit);

        if (hour is < 1 or > 12)
            return new TimePart(null, ValueCertainty.NotProvided);

        if (dayPart != DayPart.None)
        {
            var fromDayPart = ApplyDayPart(hour, minute, dayPart);
            notes.Add($"hour {hour} resolved to {fromDayPart:HH\\:mm} from the stated part of day");
            return new TimePart(fromDayPart, ValueCertainty.Explicit);
        }

        var am = new TimeOnly(hour % 12, minute);
        var pm = new TimeOnly(hour % 12 + 12, minute);

        var amOpen = IsWithinBusinessHours(am, context);
        var pmOpen = IsWithinBusinessHours(pm, context);

        if (amOpen && !pmOpen)
        {
            notes.Add($"hour {hour} resolved to {am:HH\\:mm}; {pm:HH\\:mm} is outside business hours");
            return new TimePart(am, ValueCertainty.Inferred);
        }

        if (pmOpen && !amOpen)
        {
            notes.Add($"hour {hour} resolved to {pm:HH\\:mm}; {am:HH\\:mm} is outside business hours");
            return new TimePart(pm, ValueCertainty.Inferred);
        }

        if (amOpen && pmOpen)
        {
            notes.Add($"hour {hour} is open to both {am:HH\\:mm} and {pm:HH\\:mm} within business hours");
            return new TimePart(pm, ValueCertainty.Ambiguous);
        }

        notes.Add($"hour {hour} falls outside business hours either way; offered {pm:HH\\:mm}");
        return new TimePart(pm, ValueCertainty.Ambiguous);
    }

    private static TimeOnly ApplyDayPart(int hour12, int minute, DayPart dayPart) => dayPart switch
    {
        DayPart.Morning => new TimeOnly(hour12 == 12 ? 0 : hour12, minute),
        DayPart.Noon => new TimeOnly(12, minute),
        DayPart.Afternoon => new TimeOnly(hour12 == 12 ? 12 : hour12 + 12, minute),
        DayPart.Evening => new TimeOnly(hour12 == 12 ? 12 : hour12 + 12, minute),
        // Small hours belong to the night as much as late evening does: "two at night" is 02:00.
        DayPart.Night => hour12 <= 4
            ? new TimeOnly(hour12, minute)
            : new TimeOnly(hour12 == 12 ? 0 : hour12 + 12, minute),
        _ => new TimeOnly(hour12 % 12, minute)
    };

    private static bool IsWithinBusinessHours(TimeOnly candidate, DateTimeParseContext context) =>
        candidate >= context.BusinessOpen && candidate <= context.BusinessClose;

    // =====================================================================================
    // assembly
    // =====================================================================================

    private static DateTimeParseResult Assemble(
        DatePart date, TimePart time, DayPart dayPart,
        DateTimeParseContext context, List<string> notes)
    {
        var resolvedDate = date.Date;
        var inPast = false;

        if (resolvedDate is not null && time.Time is not null)
        {
            var combined = resolvedDate.Value.ToDateTime(time.Time.Value);

            // Only a weekday said without a calendar date rolls forward. An explicit date the
            // caller actually named is never moved - that would book a different day from the
            // one they asked for.
            if (combined <= context.NowLocal && date.RollsForwardWhenPast && context.PreferFuture)
            {
                resolvedDate = resolvedDate.Value.AddDays(7);
                combined = resolvedDate.Value.ToDateTime(time.Time.Value);
                notes.Add($"weekday had already passed today, rolled forward to {resolvedDate:yyyy-MM-dd}");
            }

            inPast = combined <= context.NowLocal;
        }
        else if (resolvedDate is not null)
        {
            inPast = resolvedDate.Value < DateOnly.FromDateTime(context.NowLocal);
        }

        if (resolvedDate is null && time.Time is null)
            return DateTimeParseResult.None with { Notes = notes };

        return new DateTimeParseResult
        {
            Date = resolvedDate,
            Time = time.Time,
            DateCertainty = resolvedDate is null ? ValueCertainty.NotProvided : date.Certainty,
            TimeCertainty = time.Time is null ? ValueCertainty.NotProvided : time.Certainty,
            DayPart = dayPart,
            IsInPast = inPast,
            Notes = notes
        };
    }
}

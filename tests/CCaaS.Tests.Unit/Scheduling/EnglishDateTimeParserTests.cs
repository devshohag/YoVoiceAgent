using System.Globalization;
using CCaaS.Domain.Scheduling.DateTimeParsing;
using Xunit;

namespace CCaaS.Tests.Unit.Scheduling;

// Task 2.2 - English spoken date/time normalisation.
//
// WHY THESE TESTS LOOK LIKE THIS
// ------------------------------
// Every case below is a real thing a caller says out loud, written the way speech recognition
// actually emits it: no punctuation, numbers as words, meridiems split apart ("a m"), filler
// everywhere. A parser that only handles "2026-09-17 16:00" passes a unit suite and then fails
// every single live call, so the suite is deliberately built out of transcripts rather than
// out of formats.
//
// "Now" is pinned to Wednesday 16 September 2026, 10:00, business hours 09:00-18:00. It is
// fixed because "tomorrow" is only testable against a known today, and Wednesday is chosen so
// that weekday roll-forward has room on both sides of the week.
//
// Two bugs were caught by this suite before the code ever ran in production:
//   1. "10.30 a.m." was read as the 10th of March, because a dotted European date and a dotted
//      time are the same string until you look at the meridiem.
//   2. "half past four" returned nothing, because a regex whose minute group could span two
//      words swallowed "at half" and then matched nothing at all.
// Both are covered below and must stay covered.
public class EnglishDateTimeParserTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 10, 0, 0); // Wednesday
    private static readonly EnglishDateTimeParser Parser = new();

    private static DateTimeParseContext Ctx() => DateTimeParseContext.ForBusinessHours(Now);

    private static DateTimeParseResult Parse(string utterance) => Parser.Parse(utterance, Ctx());

    private static DateOnly D(string iso) =>
        DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TimeOnly T(string hhmm) =>
        TimeOnly.ParseExact(hhmm, "HH\\:mm", CultureInfo.InvariantCulture);

    private static string Trail(DateTimeParseResult r) => string.Join(" | ", r.Notes);

    // ================================================================= relative day references

    [Theory]
    [InlineData("tomorrow", "2026-09-17")]
    [InlineData("today", "2026-09-16")]
    [InlineData("day after tomorrow", "2026-09-18")]
    [InlineData("in 3 days", "2026-09-19")]
    [InlineData("next week", "2026-09-23")]
    public void Parse_RelativeDay_ResolvesAgainstToday(string utterance, string expected)
    {
        var result = Parse(utterance);

        Assert.Equal(D(expected), result.Date);
        Assert.Equal(ValueCertainty.Explicit, result.DateCertainty);
        Assert.Null(result.Time);
        Assert.Equal(ValueCertainty.NotProvided, result.TimeCertainty);
    }

    [Theory]
    [InlineData("the day after tomorrow at 3pm", "2026-09-18", "15:00")]
    [InlineData("in two days at 11am", "2026-09-18", "11:00")]
    public void Parse_RelativeDayWithTime_ResolvesBothHalves(string utterance, string date, string time)
    {
        var result = Parse(utterance);

        Assert.Equal(D(date), result.Date);
        Assert.Equal(T(time), result.Time);
        Assert.Equal(ValueCertainty.Explicit, result.TimeCertainty);
    }

    [Fact]
    public void Parse_Yesterday_IsReportedAsPastRatherThanSilentlyMovedForward()
    {
        // The parser never "fixes" a past date. A caller who says yesterday has made a mistake
        // worth asking about, and quietly booking tomorrow instead is the worse failure.
        var result = Parse("yesterday");

        Assert.Equal(D("2026-09-15"), result.Date);
        Assert.True(result.IsInPast);
    }

    // =============================================================================== weekdays

    [Theory]
    [InlineData("monday", "2026-09-21")]
    [InlineData("on friday", "2026-09-18")]
    [InlineData("coming tuesday", "2026-09-22")]
    [InlineData("sat", "2026-09-19")]
    [InlineData("next monday", "2026-09-21")]
    public void Parse_Weekday_ResolvesToTheNextOccurrence(string utterance, string expected)
    {
        Assert.Equal(D(expected), Parse(utterance).Date);
    }

    [Fact]
    public void Parse_NextSameWeekday_IsStrictlyInTheFuture()
    {
        // Said on a Wednesday, "next Wednesday" is never today. Bare "wednesday" may still be
        // today; that difference is the entire convention and is asserted in both directions.
        Assert.Equal(D("2026-09-23"), Parse("next wednesday").Date);
    }

    [Fact]
    public void Parse_BareWeekdayToday_StaysTodayWhenTheTimeHasNotPassed()
    {
        var result = Parse("wednesday at 3pm");

        Assert.Equal(D("2026-09-16"), result.Date);
        Assert.Equal(T("15:00"), result.Time);
        Assert.False(result.IsInPast);
    }

    [Fact]
    public void Parse_BareWeekdayToday_RollsForwardWhenTheTimeHasAlreadyPassed()
    {
        // 09:00 on the Wednesday it is already 10:00 on cannot be meant, so the weekday - and
        // only a weekday, never a named calendar date - advances a week.
        var result = Parse("wednesday at 9am");

        Assert.Equal(D("2026-09-23"), result.Date);
        Assert.Equal(T("09:00"), result.Time);
        Assert.False(result.IsInPast);
    }

    // ========================================================================= calendar dates

    [Theory]
    [InlineData("2026-12-05", "2026-12-05")]
    [InlineData("september 25", "2026-09-25")]
    [InlineData("25 september", "2026-09-25")]
    [InlineData("25th september 2027", "2027-09-25")]
    [InlineData("dec 1", "2026-12-01")]
    [InlineData("the 25th", "2026-09-25")]
    [InlineData("the twenty fifth", "2026-09-25")]
    public void Parse_CalendarDate_IsExplicit(string utterance, string expected)
    {
        var result = Parse(utterance);

        Assert.Equal(D(expected), result.Date);
        Assert.Equal(ValueCertainty.Explicit, result.DateCertainty);
    }

    [Theory]
    [InlineData("the 5th", "2026-10-05")]
    [InlineData("the first", "2026-10-01")]
    public void Parse_DayOfMonthAlreadyPast_RollsToTheNextMonth(string utterance, string expected)
    {
        // A day number with no month means the soonest month in which that day is still ahead.
        // Said on the 16th, "the fifth" is October's, not September's.
        Assert.Equal(D(expected), Parse(utterance).Date);
    }

    [Fact]
    public void Parse_NumericDateWithImpossibleMonth_IsUnambiguous()
    {
        // 15 cannot be a month, so there is only one reading and nothing to confirm.
        var result = Parse("15/10");

        Assert.Equal(D("2026-10-15"), result.Date);
        Assert.Equal(ValueCertainty.Explicit, result.DateCertainty);
    }

    [Fact]
    public void Parse_NumericDateValidBothWays_IsFlaggedAmbiguous()
    {
        // 05/06 is 5 June to most of the world and 6 May to the United States. Context cannot
        // separate them, so the convention picks a value AND the result demands confirmation.
        var dayFirst = Parser.Parse("05/06", new DateTimeParseContext(Now, T("09:00"), T("18:00")));
        var monthFirst = Parser.Parse("05/06", new DateTimeParseContext(Now, T("09:00"), T("18:00"), DayFirst: false));

        Assert.Equal(D("2027-06-05"), dayFirst.Date);
        Assert.Equal(ValueCertainty.Ambiguous, dayFirst.DateCertainty);
        Assert.True(dayFirst.IsAmbiguous, Trail(dayFirst));

        Assert.Equal(D("2027-05-06"), monthFirst.Date);
        Assert.Equal(ValueCertainty.Ambiguous, monthFirst.DateCertainty);
    }

    [Fact]
    public void Parse_NumericDateWithYear_KeepsTheStatedYearAndStillFlagsTheOrder()
    {
        var result = Parse("1/12/2026");

        Assert.Equal(D("2026-12-01"), result.Date);
        Assert.Equal(ValueCertainty.Ambiguous, result.DateCertainty);
    }

    // ========================================================================== explicit times

    [Theory]
    [InlineData("tomorrow at 10:30 am", "10:30")]
    [InlineData("tomorrow at 10:30am", "10:30")]
    [InlineData("tomorrow at 10.30 a.m.", "10:30")]   // bug 1 regression - dotted time, not 10 March
    [InlineData("tomorrow at 3 pm", "15:00")]
    [InlineData("tomorrow at 3pm", "15:00")]
    [InlineData("tomorrow 12 pm", "12:00")]
    [InlineData("tomorrow 12 am", "00:00")]
    [InlineData("tomorrow at noon", "12:00")]
    [InlineData("tomorrow at midnight", "00:00")]
    [InlineData("tomorrow at 14:45", "14:45")]
    [InlineData("tomorrow at 16:00", "16:00")]
    public void Parse_StatedTime_IsExplicitAndNeedsNoReadback(string utterance, string expected)
    {
        var result = Parse(utterance);

        Assert.Equal(D("2026-09-17"), result.Date);
        Assert.Equal(T(expected), result.Time);
        Assert.Equal(ValueCertainty.Explicit, result.TimeCertainty);
        Assert.True(result.IsComplete);
        Assert.False(result.NeedsConfirmation, Trail(result));
    }

    // ============================================== bare hours resolved by business hours

    [Theory]
    [InlineData("tomorrow at four", "16:00")]
    [InlineData("tomorrow at 4", "16:00")]
    [InlineData("tomorrow at one", "13:00")]
    [InlineData("tomorrow at ten", "10:00")]
    [InlineData("tomorrow at nine", "09:00")]
    [InlineData("tomorrow at eleven", "11:00")]
    [InlineData("tomorrow at twelve", "12:00")]
    [InlineData("tomorrow at 10:30", "10:30")]
    [InlineData("tomorrow at 4:15", "16:15")]
    public void Parse_BareHour_IsDecidedByBusinessHoursAndReportedAsInferred(string utterance, string expected)
    {
        // 04:00 is not a time this business can be visited, so "four" can only be 16:00. The
        // value is usable, but the state machine still owes the caller a readback.
        var result = Parse(utterance);

        Assert.Equal(T(expected), result.Time);
        Assert.Equal(ValueCertainty.Inferred, result.TimeCertainty);
        Assert.True(result.IsComplete);
        Assert.True(result.NeedsConfirmation);
        Assert.False(result.IsAmbiguous);
    }

    [Fact]
    public void Parse_BareHourOpenBothWays_IsAmbiguousNotGuessed()
    {
        // Open 08:00-20:00, "eight" is genuinely both. A value is still offered so the agent
        // has something to say, but it must be confirmed with a closed question.
        var wide = new DateTimeParseContext(Now, T("08:00"), T("20:00"));

        var ambiguous = Parser.Parse("tomorrow at eight", wide);
        Assert.Equal(T("20:00"), ambiguous.Time);
        Assert.Equal(ValueCertainty.Ambiguous, ambiguous.TimeCertainty);
        Assert.True(ambiguous.IsAmbiguous);

        // "seven" is not: 07:00 is still shut, so only 19:00 survives.
        var inferred = Parser.Parse("tomorrow at seven", wide);
        Assert.Equal(T("19:00"), inferred.Time);
        Assert.Equal(ValueCertainty.Inferred, inferred.TimeCertainty);
    }

    // ================================================================================ day parts

    [Theory]
    [InlineData("tomorrow at four in the afternoon", "16:00", DayPart.Afternoon)]
    [InlineData("tomorrow at ten in the morning", "10:00", DayPart.Morning)]
    [InlineData("tomorrow morning at eight", "08:00", DayPart.Morning)]
    [InlineData("tomorrow evening at seven", "19:00", DayPart.Evening)]
    public void Parse_StatedPartOfDay_OutranksBusinessHoursAndMakesTheTimeExplicit(
        string utterance, string expected, DayPart part)
    {
        // "eight in the morning" is 08:00 even though the business opens at 09:00 - the caller
        // said it, so the parser reports it rather than arguing with them.
        var result = Parse(utterance);

        Assert.Equal(T(expected), result.Time);
        Assert.Equal(ValueCertainty.Explicit, result.TimeCertainty);
        Assert.Equal(part, result.DayPart);
    }

    [Fact]
    public void Parse_Tonight_CarriesBothTodayAndTheNightPart()
    {
        var result = Parse("tonight at nine");

        Assert.Equal(D("2026-09-16"), result.Date);
        Assert.Equal(T("21:00"), result.Time);
        Assert.Equal(DayPart.Night, result.DayPart);
    }

    [Fact]
    public void Parse_SmallHoursAtNight_StayInTheSmallHours()
    {
        // "Two at night" is 02:00, not 14:00. Night is the one part of day that wraps.
        var result = Parse("tomorrow at two at night");

        Assert.Equal(T("02:00"), result.Time);
        Assert.Equal(DayPart.Night, result.DayPart);
    }

    [Fact]
    public void Parse_TheWordAm_IsNotTreatedAsAPartOfDay()
    {
        // Regression: treating "am" as a morning marker read "I am free at four" as a morning
        // appointment and booked 04:00 - a time the business is shut.
        var result = Parse("i am free tomorrow at four");

        Assert.Equal(D("2026-09-17"), result.Date);
        Assert.Equal(T("16:00"), result.Time);
        Assert.Equal(DayPart.None, result.DayPart);
    }

    // ======================================================================== spoken clock forms

    [Theory]
    [InlineData("tomorrow at half past four", "16:30")]        // bug 2 regression
    [InlineData("tomorrow at quarter past five", "17:15")]
    [InlineData("tomorrow at quarter to five", "16:45")]
    [InlineData("tomorrow at ten past ten", "10:10")]
    [InlineData("tomorrow at twenty five past ten", "10:25")]
    [InlineData("tomorrow at five to one", "12:55")]
    public void Parse_PastAndToConstructions_AreRead(string utterance, string expected)
    {
        Assert.Equal(T(expected), Parse(utterance).Time);
    }

    [Theory]
    [InlineData("tomorrow at ten oclock", "10:00")]
    [InlineData("tomorrow at ten o'clock", "10:00")]
    [InlineData("tomorrow ten thirty", "10:30")]
    [InlineData("tomorrow ten fifteen", "10:15")]
    [InlineData("tomorrow at 10 30", "10:30")]
    [InlineData("tomorrow at 1030", "10:30")]
    [InlineData("tomorrow at 1430", "14:30")]
    public void Parse_OtherSpokenClockShapes_AreRead(string utterance, string expected)
    {
        Assert.Equal(T(expected), Parse(utterance).Time);
    }

    // ========================================================================= relative offsets

    [Theory]
    [InlineData("in 30 minutes", "10:30")]
    [InlineData("in half an hour", "10:30")]
    [InlineData("in an hour", "11:00")]
    [InlineData("in 2 hours", "12:00")]
    [InlineData("in 45 mins", "10:45")]
    public void Parse_RelativeOffset_ProducesAnAbsoluteDateAndTime(string utterance, string expected)
    {
        var result = Parse(utterance);

        Assert.Equal(D("2026-09-16"), result.Date);
        Assert.Equal(T(expected), result.Time);
        Assert.Equal(ValueCertainty.Explicit, result.TimeCertainty);
        Assert.False(result.IsInPast);
    }

    // ========================================================================= partial results

    [Fact]
    public void Parse_DateWithoutTime_IsASuccessNotAFailure()
    {
        // A conversation collects the two halves across turns. Returning nothing because one
        // half is missing would force the state machine to re-parse the whole transcript.
        var result = Parse("next tuesday");

        Assert.Equal(D("2026-09-22"), result.Date);
        Assert.Null(result.Time);
        Assert.Equal(ValueCertainty.NotProvided, result.TimeCertainty);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void Parse_TimeWithoutDate_IsASuccessNotAFailure()
    {
        var result = Parse("at ten");

        Assert.Null(result.Date);
        Assert.Equal(ValueCertainty.NotProvided, result.DateCertainty);
        Assert.Equal(T("10:00"), result.Time);
        Assert.Equal(ValueCertainty.Inferred, result.TimeCertainty);
    }

    [Fact]
    public void Parse_BareSpokenTime_NeedsNoLeadingPreposition()
    {
        Assert.Equal(T("16:30"), Parse("four thirty").Time);
    }

    // ============================================================================ speech noise

    [Fact]
    public void Parse_FillerHeavyUtterance_StillYieldsTheAppointment()
    {
        var result = Parse("um yeah can i book an appointment for tomorrow at uh four please");

        Assert.Equal(D("2026-09-17"), result.Date);
        Assert.Equal(T("16:00"), result.Time);
    }

    [Fact]
    public void Parse_PartOfDayWithoutAClockTime_KeepsThePartForANarrowerQuestion()
    {
        var result = Parse("id like a slot tomorrow morning");

        Assert.Equal(D("2026-09-17"), result.Date);
        Assert.Equal(DayPart.Morning, result.DayPart);
    }

    [Fact]
    public void Parse_QuestionShapedUtterance_StillYieldsTheDate()
    {
        Assert.Equal(D("2026-09-21"), Parse("is there anything available next monday").Date);
    }

    // ================================================================== nothing to parse

    [Theory]
    [InlineData("hello how are you")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_UtteranceWithNoDateOrTime_ReturnsNothingWithoutThrowing(string? utterance)
    {
        // A caller saying something unrelated is ordinary conversation, not an error.
        var result = Parser.Parse(utterance, Ctx());

        Assert.Null(result.Date);
        Assert.Null(result.Time);
        Assert.False(result.HasAnything);
    }

    // ==================================================================== past-time reporting

    [Theory]
    [InlineData("today at nine")]
    [InlineData("today at 9am")]
    public void Parse_TodayAtATimeAlreadyGone_IsFlaggedRatherThanCorrected(string utterance)
    {
        // Moving this to tomorrow would book the wrong day. The agent should ask.
        var result = Parse(utterance);

        Assert.Equal(D("2026-09-16"), result.Date);
        Assert.Equal(T("09:00"), result.Time);
        Assert.True(result.IsInPast);
    }

    // ========================================================================== plumbing

    [Fact]
    public void Parser_ReportsItsLanguage()
    {
        Assert.Equal("en", Parser.Language);
    }

    [Fact]
    public void Parse_NeverReadsAmbientState()
    {
        // Same utterance, two different "now"s, two different answers. If this ever returns the
        // same date twice the parser has started reading DateTime.Now and is no longer testable.
        var wednesday = Parser.Parse("tomorrow", DateTimeParseContext.ForBusinessHours(Now));
        var friday = Parser.Parse("tomorrow", DateTimeParseContext.ForBusinessHours(Now.AddDays(2)));

        Assert.Equal(D("2026-09-17"), wednesday.Date);
        Assert.Equal(D("2026-09-19"), friday.Date);
    }
}

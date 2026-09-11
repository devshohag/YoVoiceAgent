using System.Globalization;
using CCaaS.Domain.Scheduling.Booking;
using Xunit;

namespace CCaaS.Tests.Unit.Scheduling;

// Task 2.3 - how the agent says dates and times out loud.
//
// These are not cosmetic. A caller confirming an appointment has one pass at the value, by ear,
// with no screen - so the readback has to be the phrasing a person would use. "Sixteen thirty"
// is not English, and a caller who has to decode it is a caller who stops listening for whether
// it is the right time.
public class BookingPhrasesTests
{
    private static readonly EnglishBookingPhrases Say = EnglishBookingPhrases.Instance;
    private static readonly DateOnly Today = new(2026, 9, 16);   // Wednesday

    private static DateOnly D(string iso) =>
        DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static OfferedSlot Slot(string localIso)
    {
        var local = DateTime.ParseExact(localIso, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        return new OfferedSlot(Guid.NewGuid(), local, local.AddHours(-6), "Provider A");
    }

    [Theory]
    [InlineData(16, 0, "four o'clock in the afternoon")]
    [InlineData(16, 15, "quarter past four in the afternoon")]
    [InlineData(16, 30, "half past four in the afternoon")]
    [InlineData(19, 0, "seven o'clock in the evening")]
    [InlineData(21, 30, "half past nine at night")]
    [InlineData(9, 5, "nine oh five in the morning")]
    [InlineData(10, 20, "ten twenty in the morning")]
    public void SpeakTime_UsesTheFormsPeopleActuallySay(int hour, int minute, string expected)
    {
        Assert.Equal(expected, Say.SpeakTime(new TimeOnly(hour, minute)));
    }

    [Theory]
    [InlineData(11, 45, "quarter to twelve in the morning")]
    [InlineData(16, 45, "quarter to five in the afternoon")]
    public void SpeakTime_QuarterTo_DescribesWhenTheCallerActuallyComesIn(
        int hour, int minute, string expected)
    {
        // The part of day belongs to the appointment, not to the hour being counted back from.
        // 16:45 is an afternoon appointment even though five o'clock itself is evening, and
        // 11:45 is a morning one even though it counts back from midday.
        Assert.Equal(expected, Say.SpeakTime(new TimeOnly(hour, minute)));
    }

    [Theory]
    [InlineData(12, 0, "midday")]
    [InlineData(0, 0, "midnight")]
    public void SpeakTime_NamesTheTwoTimesThatHaveNames(int hour, int minute, string expected)
    {
        // "Twelve o'clock in the afternoon" invites exactly the mistake it sounds like.
        Assert.Equal(expected, Say.SpeakTime(new TimeOnly(hour, minute)));
    }

    [Theory]
    [InlineData("2026-09-16", "today")]
    [InlineData("2026-09-17", "tomorrow")]
    [InlineData("2026-09-21", "Monday the twenty first")]
    [InlineData("2026-10-03", "Saturday the third of October")]
    public void SpeakDate_PrefersTheWordsThatLandFastest(string iso, string expected)
    {
        // "Tomorrow" lands; "the seventeenth" has to be worked out. Within the coming week the
        // weekday alone is unambiguous, and only beyond that is the month worth the breath.
        Assert.Equal(expected, Say.SpeakDate(DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture), Today));
    }

    [Theory]
    [InlineData("2026-09-17", "What time tomorrow?")]
    [InlineData("2026-09-16", "What time today?")]
    [InlineData("2026-09-21", "What time on Monday the twenty first?")]
    public void AskWhatTime_PutsThePrepositionWhereEnglishWantsIt(string iso, string expected)
    {
        // "What time on tomorrow?" is the kind of small wrongness that makes a caller notice
        // they are talking to a machine. A relative day takes no preposition; a named one does.
        Assert.Equal(expected, Say.AskWhatTime(D(iso), Today));
    }

    [Theory]
    [InlineData("2026-09-16", "nothing free today")]
    [InlineData("2026-09-17", "nothing free tomorrow")]
    [InlineData("2026-09-21", "nothing free on Monday the twenty first")]
    public void NothingFree_ReadsAsASentence(string iso, string expected)
    {
        Assert.Contains(expected, Say.NothingFreeThatDay(D(iso), Today));
    }

    [Fact]
    public void OfferTimes_OpensWithTheDayInTheRightShape()
    {
        // Two slots on the same day: the day is said once, up front, then the times.
        var slots = new[] { Slot("2026-09-17T09:30:00"), Slot("2026-09-17T11:00:00") };

        var line = Say.OfferTimes(slots, Today);

        Assert.StartsWith("Tomorrow I have", line);
        Assert.Contains("half past nine in the morning", line);
        Assert.Contains("eleven o'clock in the morning", line);
    }

    [Fact]
    public void OfferTimes_OnANamedDay_KeepsThePreposition()
    {
        var slots = new[] { Slot("2026-09-21T09:30:00"), Slot("2026-09-21T11:00:00") };

        Assert.StartsWith("On Monday the twenty first I have", Say.OfferTimes(slots, Today));
    }

    [Theory]
    [InlineData(1, "first")]
    [InlineData(3, "third")]
    [InlineData(11, "eleventh")]
    [InlineData(12, "twelfth")]
    [InlineData(13, "thirteenth")]
    [InlineData(20, "twentieth")]
    [InlineData(21, "twenty first")]
    [InlineData(22, "twenty second")]
    [InlineData(31, "thirty first")]
    public void SpeakOrdinal_HandlesTheIrregularOnes(int day, string expected)
    {
        Assert.Equal(expected, EnglishBookingPhrases.SpeakOrdinal(day));
    }

    [Fact]
    public void SpeakOrdinal_CoversEveryDayAMonthCanHave()
    {
        // A crash while reading back a date ends the call at its most delicate moment.
        for (var day = 1; day <= 31; day++)
            Assert.False(string.IsNullOrWhiteSpace(EnglishBookingPhrases.SpeakOrdinal(day)));
    }

    [Fact]
    public void SpeakReference_ReadsTheTailOutCharacterByCharacter()
    {
        // A reference said as a word cannot be written down. Only the tail is spoken because
        // that is the part customers are asked to quote back.
        Assert.Equal("seven K two", Say.SpeakReference("APT-20260917-7K2"));
    }

    [Fact]
    public void ReadBack_CarriesTheDateTheTimeAndTheName()
    {
        // Every value that would be wrong on the voucher has to be in the sentence the caller
        // says yes to. Whatever the wording, these three must appear.
        var slot = new OfferedSlot(Guid.NewGuid(),
            new DateTime(2026, 9, 17, 16, 30, 0), new DateTime(2026, 9, 17, 10, 30, 0), "Provider A");

        var line = Say.ReadBackForConfirmation(slot, "Rahim Uddin", Today);

        Assert.Contains("tomorrow", line);
        Assert.Contains("half past four", line);
        Assert.Contains("Rahim Uddin", line);
    }

    [Fact]
    public void Confirmation_IsAClosingLineNotAQuestion()
    {
        var slot = new OfferedSlot(Guid.NewGuid(),
            new DateTime(2026, 9, 17, 16, 0, 0), new DateTime(2026, 9, 17, 10, 0, 0), "Provider A");

        var line = Say.Confirmed(slot, "APT-20260917-7K2", Today);

        Assert.Contains("tomorrow", line);
        Assert.Contains("four o'clock", line);
        Assert.DoesNotContain("?", line);
    }

    [Fact]
    public void EveryHandoffReason_HasSomethingToSay()
    {
        // A missing arm here would hand the caller to a human in silence.
        foreach (HandoffReason reason in Enum.GetValues<HandoffReason>())
            Assert.False(string.IsNullOrWhiteSpace(Say.HandingOver(reason)));
    }

    [Fact]
    public void OfferingNothing_StillAsksAUsefulQuestion()
    {
        // An empty list reaches these only through a bug, and a dead-air response would turn
        // that bug into an abandoned call.
        Assert.False(string.IsNullOrWhiteSpace(Say.OfferAlternatives(Array.Empty<OfferedSlot>(), Today)));
        Assert.False(string.IsNullOrWhiteSpace(Say.OfferTimes(Array.Empty<OfferedSlot>(), Today)));
    }
}

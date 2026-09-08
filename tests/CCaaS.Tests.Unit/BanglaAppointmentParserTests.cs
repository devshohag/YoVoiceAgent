using CCaaS.Application.Appointment.Voice;
using Xunit;

public sealed class BanglaAppointmentParserTests
{
    private static readonly DateOnly Today = new(2026,9,8);
    [Theory]
    [InlineData("আগামীকাল সকাল দশটায়", "2026-09-09", 10, 0)]
    [InlineData("আগামী পরশু বিকাল সাড়ে চারটা", "2026-09-10", 16, 30)]
    [InlineData("সামনের সোমবার সকাল ১০টা", "2026-09-14", 10, 0)]
    [InlineData("১৫ সেপ্টেম্বর বিকাল পৌনে পাঁচটা", "2026-09-15", 16, 45)]
    [InlineData("১৫/০৯ দুপুর দেড়টা", "2026-09-15", 13, 30)]
    [InlineData("১৫ তারিখ দুপুর আড়াইটা", "2026-09-15", 14, 30)]
    [InlineData("আগামীকাল রাত বারোটা", "2026-09-09", 0, 0)]
    [InlineData("আগামীকাল রাত দশটা", "2026-09-09", 22, 0)]
    [InlineData("2026-09-15 10:00 am", "2026-09-15", 10, 0)]
    public void Parses_business_dates_and_times(string text, string date, int hour, int minute)
    {
        var p = BanglaAppointmentParser.Parse(text, Today);
        Assert.Null(p.Error);
        Assert.Equal(DateOnly.Parse(date), p.Date);
        Assert.Equal(new TimeOnly(hour, minute), p.Time);
        Assert.False(p.AmbiguousTime);
    }
    [Theory]
    [InlineData("2026-09-15")]
    [InlineData("01712345678")]
    [InlineData("১৫/০৯")]
    public void Date_and_phone_digits_are_not_time(string text) => Assert.Null(BanglaAppointmentParser.Parse(text, Today).Time);
    [Theory]
    [InlineData("গতকাল সকাল দশটা")]
    [InlineData("2026-02-31")]
    [InlineData("আগামীকাল 25:00")]
    [InlineData("0171234567")]
    public void Rejects_invalid_or_past_input(string text) => Assert.NotNull(BanglaAppointmentParser.Parse(text, Today).Error);
    [Fact]
    public void Phone_is_normalized() => Assert.Equal("01712345678", BanglaAppointmentParser.Parse("+৮৮০১৭১২৩৪৫৬৭৮", Today).Phone);
    [Fact]
    public void Name_stops_before_contact() => Assert.Equal("Shohag", BanglaAppointmentParser.Parse("my name is Shohag and contact 01712345678", Today).Name);
}

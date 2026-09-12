using CCaaS.Domain.Scheduling.Booking;
using CCaaS.Domain.Scheduling.DateTimeParsing;

namespace CCaaS.Tests.Unit.Scheduling;

public sealed class BookingReadbackSafetyTests
{
    [Theory]
    [InlineData("+8801712345678", "Is the number ending seven eight correct?")]
    [InlineData("+14155552671", "Is the number ending seven one correct?")]
    [InlineData("+442079460000", "Is the number ending zero zero correct?")]
    [InlineData("person123456@example.com", "Is the contact we have on file correct?")]
    public void Contact_readback_reveals_only_the_suffix(string contact, string expected)
        => Assert.Equal(expected, EnglishBookingPhrases.SpeakContactSuffix(contact));

    [Theory]
    [InlineData("say that again", CallerMove.Repeat)]
    [InlineData("Sorry, what?", CallerMove.Repeat)]
    [InlineData("can you repeat that", CallerMove.Repeat)]
    [InlineData("cancel my appointment", CallerMove.CancelAppointment)]
    [InlineData("reschedule my appointment", CallerMove.RescheduleAppointment)]
    [InlineData("cancel it", CallerMove.WantsToEnd)]
    [InlineData("human please, cancel my appointment", CallerMove.WantsHuman)]
    public void Lifecycle_intents_have_explicit_precedence(string text, CallerMove expected)
        => Assert.Equal(expected, CallerIntent.Detect(text));

    [Fact]
    public void Unsolicited_success_cannot_create_a_booking_or_cancel_an_appointment()
    {
        var machine = new BookingConversationMachine(new EnglishDateTimeParser());
        var state = machine.Begin("+8801712345678").State;
        var context = new DateTimeParseContext(new DateTime(2026, 9, 16, 10, 0, 0), new TimeOnly(9, 0), new TimeOnly(18, 0));
        BookingInput[] events = { new BookingInput.BookingCommitted("late"),
            new BookingInput.AppointmentCancelled(), new BookingInput.AppointmentRescheduled("late") };
        foreach (var input in events)
        {
            var turn = machine.Advance(state, input, context);
            Assert.Equal(state, turn.State);
            Assert.Empty(turn.Actions);
        }
    }
}

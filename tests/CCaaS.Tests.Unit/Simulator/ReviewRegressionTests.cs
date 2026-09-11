using CCaaS.Domain.Scheduling.Booking;

namespace CCaaS.Tests.Unit.Simulator;

public sealed class ReviewRegressionTests
{
    [Theory]
    [InlineData("Your appointment is confirmed.")]
    [InlineData("I've booked your appointment.")]
    [InlineData("I’ve booked your appointment.")]
    [InlineData("Your booking has been confirmed.")]
    public void Premature_confirmation_mutations_fail_the_invariant(string line)
    {
        var frame = new SimulationFrame(0, "begin", BookingState.Start("+8801712345678"),
            new[] { "Speak" }, new[] { line });
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            TextConversationSimulatorTests.AssertInvariants(Scenario(), new[] { frame }));
    }

    [Fact]
    public void Omitting_date_from_readback_fails_the_invariant()
    {
        var slot = new OfferedSlot(Guid.NewGuid(), new DateTime(2026, 9, 17, 16, 0, 0),
            new DateTime(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc), "Provider");
        var state = BookingState.Start("+8801712345678") with
            { Stage = BookingStage.ConfirmingBooking, Selected = slot, CallerName = "Rahim" };
        var line = EnglishBookingPhrases.Instance.SpeakTime(new TimeOnly(16, 0)) + " for Rahim";
        var frame = new SimulationFrame(1, "name", state, new[] { "Speak" }, new[] { line });
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            TextConversationSimulatorTests.AssertInvariants(Scenario(), new[] { frame }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Welcome to the booking line. ")]
    public void Welcome_text_and_whitespace_are_handled(string welcome)
    {
        var scenario = new ConversationScenario
        {
            Name = "welcome", NowLocal = new DateTime(2026, 9, 16, 10, 0, 0), Contact = "+8801712345678",
            Welcome = welcome, Steps = new() { new() { Kind = "silence", Stage = BookingStage.CollectingWhen, Actions = new[] { "Speak", "Speak" } } }
        };
        var frame = new TextConversationSimulator().Run(scenario)[0];
        Assert.Equal(string.IsNullOrWhiteSpace(welcome) ? 1 : 2, frame.SpokenLines.Length);
        if (!string.IsNullOrWhiteSpace(welcome)) Assert.Equal(welcome.Trim(), frame.SpokenLines[0]);
    }

    private static ConversationScenario Scenario() => new()
    { Name = "mutated", NowLocal = new DateTime(2026, 9, 16, 10, 0, 0), Contact = "+8801712345678", Steps = new() };
}

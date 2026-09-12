using CCaaS.Application.Routing;
using CCaaS.Domain.Ai;

namespace CCaaS.Tests.Unit.Routing;

public sealed class DeterministicTurnRouterTests
{
    [Fact]
    public void Active_appointment_state_stays_on_deterministic_route()
    {
        var history = new[] { new AiConversationTurn
        {
            Speaker = AiSpeaker.System,
            Text = "[APPOINTMENT_STATE]{\"stage\":\"date-time\"}"
        } };

        Assert.Equal(TurnRoute.Appointment, DeterministicTurnRouter.Select(history, "four o'clock"));
    }

    [Theory]
    [InlineData("I want to book an appointment")]
    [InlineData("Can I schedule a doctor visit?")]
    public void Appointment_intent_uses_deterministic_route(string input)
    {
        Assert.Equal(TurnRoute.Appointment, DeterministicTurnRouter.Select(Array.Empty<AiConversationTurn>(), input));
    }

    [Fact]
    public void Human_request_has_priority_over_appointment_keywords()
    {
        Assert.Equal(TurnRoute.HumanHandoff,
            DeterministicTurnRouter.Select(Array.Empty<AiConversationTurn>(), "book an appointment with a human"));
    }

    [Fact]
    public void Unrelated_speech_uses_general_route()
    {
        Assert.Equal(TurnRoute.General,
            DeterministicTurnRouter.Select(Array.Empty<AiConversationTurn>(), "What are your opening hours?"));
    }
}
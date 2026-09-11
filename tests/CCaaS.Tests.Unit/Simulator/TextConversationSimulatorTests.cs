using System.Text.Json;
using CCaaS.Domain.Scheduling.Booking;
using Xunit.Abstractions;

namespace CCaaS.Tests.Unit.Simulator;

public sealed class TextConversationSimulatorTests
{
    private readonly ITestOutputHelper _output;
    public TextConversationSimulatorTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Scenarios()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Simulator", "scenarios");
        var files = Directory.GetFiles(directory, "*.yaml").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new InvalidDataException("No simulator scenarios copied to test output.");
        return files.Select(path => new object[] { Path.GetFileName(path) });
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Transcript_matches_expected_states_slots_and_database_actions(string file)
    {
        var scenario = TextConversationSimulator.Load(
            Path.Combine(AppContext.BaseDirectory, "Simulator", "scenarios", file));
        var frames = new TextConversationSimulator().Run(scenario);
        Assert.Equal(BookingStage.CollectingWhen, frames[0].State.Stage);
        Assert.Equal(new[] { "Speak" }, frames[0].Actions);

        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var expected = scenario.Steps[i];
            var actual = frames[i + 1];
            var snapshot = TextConversationSimulator.Snapshot(actual.State);
            _output.WriteLine($"{scenario.Name} / {actual.Step}: {actual.Input}");
            _output.WriteLine($"State: {actual.State.Stage}; slots: {JsonSerializer.Serialize(snapshot)}");
            _output.WriteLine($"Actions: {string.Join(" | ", actual.Actions)}");
            foreach (var line in actual.SpokenLines) _output.WriteLine($"Agent: {line}");
            Assert.Equal(expected.Stage, actual.State.Stage);
            Assert.Equal(expected.Actions, actual.Actions); // includes absence and execution order
            foreach (var pair in expected.Expect)
            {
                Assert.True(snapshot.ContainsKey(pair.Key), $"Unknown expected field: {pair.Key}");
                Assert.Equal(JsonSerializer.Serialize(pair.Value), JsonSerializer.Serialize(snapshot[pair.Key]));
            }
        }
        // Replaying uses a new machine and does not share mutable session state.
        var replay = new TextConversationSimulator().Run(scenario);
        Assert.Equal(JsonSerializer.Serialize(frames), JsonSerializer.Serialize(replay));
    }

    [Theory]
    [InlineData("availability")]
    [InlineData("committed")]
    [InlineData("rejected")]
    public void Unsolicited_system_results_are_rejected_by_the_test_adapter(string kind)
    {
        var scenario = new ConversationScenario
        {
            Name = "invalid-system-reply", NowLocal = new DateTime(2026, 9, 16, 10, 0, 0),
            Contact = "+8801712345678",
            Steps = new List<ScenarioStep> { new()
            {
                Kind = kind, Slots = new(), Reference = "APT-FAKE", Failure = BookingFailure.SystemError,
                Stage = BookingStage.Booked, Actions = Array.Empty<string>()
            } }
        };
        Assert.Throws<InvalidOperationException>(() => new TextConversationSimulator().Run(scenario));
    }
}

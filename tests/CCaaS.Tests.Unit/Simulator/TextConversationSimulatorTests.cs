using System.Text.Json;
using CCaaS.Domain.Scheduling.Booking;
using Xunit.Abstractions;

namespace CCaaS.Tests.Unit.Simulator;

public sealed class TextConversationSimulatorTests
{
    private readonly ITestOutputHelper _output;
    public TextConversationSimulatorTests(ITestOutputHelper output) => _output = output;

    private static string ScenarioDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Simulator", "scenarios");

    public static IEnumerable<object[]> Scenarios()
    {
        // A friendly message matters here: this runs at discovery time, and a missing copy step
        // otherwise surfaces as "no tests found", which sends people looking in the wrong place.
        if (!Directory.Exists(ScenarioDirectory))
            throw new InvalidDataException(
                $"Scenario directory missing: {ScenarioDirectory}. "
                + "Check the CopyToOutputDirectory entry in CCaaS.Tests.Unit.csproj.");

        var files = Directory.GetFiles(ScenarioDirectory, "*.json")
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
            throw new InvalidDataException("No simulator scenarios copied to test output.");

        return files.Select(path => new object[] { Path.GetFileName(path) });
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Transcript_matches_expected_states_words_and_database_actions(string file)
    {
        var scenario = TextConversationSimulator.Load(Path.Combine(ScenarioDirectory, file));
        var frames = new TextConversationSimulator().Run(scenario);

        var openingLines = string.IsNullOrWhiteSpace(scenario.Welcome) ? 1 : 2;
        Assert.Equal(BookingStage.ChoosingIntent, frames[0].State.Stage);
        Assert.Equal(Enumerable.Repeat("Speak", openingLines).ToArray(), frames[0].Actions);

        if (!string.IsNullOrWhiteSpace(scenario.Welcome))
            Assert.Equal(scenario.Welcome.Trim(), frames[0].SpokenLines[0]);
        Assert.Equal(((IBookingPhrases)EnglishBookingPhrases.Instance).AskIntent(), frames[0].SpokenLines[^1]);

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

            if (expected.Says is not null)
            {
                Assert.Equal(expected.Says.Length, actual.SpokenLines.Length);
                for (var line = 0; line < expected.Says.Length; line++)
                    Assert.Contains(expected.Says[line], actual.SpokenLines[line],
                        StringComparison.OrdinalIgnoreCase);
            }

            foreach (var pair in expected.Expect)
            {
                Assert.True(snapshot.ContainsKey(pair.Key), $"Unknown expected field: {pair.Key}");
                Assert.Equal(JsonSerializer.Serialize(pair.Value), JsonSerializer.Serialize(snapshot[pair.Key]));
            }
        }

        AssertInvariants(scenario, frames);

        // Replaying uses a new machine and does not share mutable session state.
        var replay = new TextConversationSimulator().Run(scenario);
        Assert.Equal(JsonSerializer.Serialize(frames), JsonSerializer.Serialize(replay));
    }

    /// <summary>
    /// Properties that must hold in EVERY scenario, checked here rather than written into each
    /// fixture. A fixture only catches what its author thought to assert; these catch what a
    /// future change would break without anyone noticing, and they cost nothing to carry.
    /// </summary>
    internal static void AssertInvariants(
        ConversationScenario scenario, IReadOnlyList<SimulationFrame> frames)
    {
        var phrases = EnglishBookingPhrases.Instance;

        // Every reference a scenario ever commits, in the form the agent would read it aloud.
        var spokenReferences = scenario.Steps
            .Where(s => (s.Kind == "committed" || s.Kind == "rescheduled") && !string.IsNullOrWhiteSpace(s.Reference))
            .Select(s => phrases.SpeakReference(s.Reference!))
            .ToArray();

        foreach (var frame in frames)
        {
            foreach (var line in frame.SpokenLines)
            {
                Assert.DoesNotContain(scenario.Contact, line, StringComparison.OrdinalIgnoreCase);
                if (line.Contains("appointment has been cancelled", StringComparison.OrdinalIgnoreCase))
                    Assert.True(frame.State.CancellationConfirmed);
                // 1. Never claim a booking that does not exist. This is the property the whole
                //    state machine is built around, and the one a caller would be hurt by: the
                //    only path to a reference is a commit actually coming back.
                if (frame.State.BookingReference is null)
                {
                    foreach (var claim in TextConversationSimulator.BookingClaims)
                        Assert.DoesNotContain(claim, line.Replace("’", "'"), StringComparison.OrdinalIgnoreCase);

                    foreach (var spoken in spokenReferences)
                        Assert.DoesNotContain(spoken, line, StringComparison.OrdinalIgnoreCase);
                }
            }

            // 2. A closing line must be the end of something. Otherwise the telephony layer
            //    stops listening and the caller is left talking to a line that has gone quiet.
            if (frame.Actions.Contains("Speak:closing"))
                Assert.Contains(frame.Actions,
                    a => a.StartsWith("Transfer:", StringComparison.Ordinal)
                         || a.StartsWith("End:", StringComparison.Ordinal));

            // 3. The readback must contain every value that would be wrong on the voucher.
            //    A confirmation the caller cannot check is not a confirmation.
            if (frame.State.Stage == BookingStage.ConfirmingBooking
                && frame.State.Selected is { } slot
                && frame.SpokenLines.Length > 0)
            {
                var readback = frame.SpokenLines[^1];
                var name = string.IsNullOrWhiteSpace(frame.State.CallerName) || frame.State.CallerName == frame.State.Contact
                    ? "you" : frame.State.CallerName;

                Assert.Contains(phrases.SpeakDate(DateOnly.FromDateTime(slot.StartsAtLocal),
                    DateOnly.FromDateTime(scenario.NowLocal)), readback, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(phrases.SpeakTime(TimeOnly.FromDateTime(slot.StartsAtLocal)),
                    readback, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(name, readback, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(EnglishBookingPhrases.SpeakContactSuffix(frame.State.Contact),
                    readback, StringComparison.OrdinalIgnoreCase);
            }

            if (frame.State.Stage is BookingStage.ConfirmingCancellation or BookingStage.ConfirmingReschedule
                && frame.State.Existing is { } existing && frame.SpokenLines.Length > 0)
            {
                var readback = frame.SpokenLines[^1];
                Assert.Contains(phrases.SpeakDate(DateOnly.FromDateTime(existing.Slot.StartsAtLocal),
                    DateOnly.FromDateTime(scenario.NowLocal)), readback, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(phrases.SpeakTime(TimeOnly.FromDateTime(existing.Slot.StartsAtLocal)),
                    readback, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(EnglishBookingPhrases.SpeakContactSuffix(frame.State.Contact), readback);
            }

            // 4. A committed booking must be for the slot the caller actually agreed to.
            if (frame.State.Stage == BookingStage.Booked)
                Assert.Contains(frames,
                    f => f.Actions.Any(a => a.StartsWith(
                        $"Commit:{frame.State.Selected!.SlotId}", StringComparison.Ordinal)
                        || a == $"Reschedule:{frame.State.Existing?.BookingId}:{frame.State.Selected!.SlotId}"));
        }
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

    [Fact]
    public void A_partial_policy_block_keeps_the_real_defaults_for_everything_else()
    {
        // Fixture 17 overrides one field. If the others silently became zero, MaxTurns would be
        // zero and every call would transfer on its first turn - a failure that looks like a
        // machine bug rather than a deserialisation one, which is why it is asserted directly.
        var overridden = new PolicyOverride { RequireName = false }.ToPolicy();

        Assert.False(overridden.RequireName);
        Assert.Equal(BookingPolicy.Default.MaxTurns, overridden.MaxTurns);
        Assert.Equal(BookingPolicy.Default.MaxNoProgressTurns, overridden.MaxNoProgressTurns);
        Assert.Equal(BookingPolicy.Default.MaxAlternativesOffered, overridden.MaxAlternativesOffered);
    }

    [Fact]
    public void Every_scenario_asserts_what_the_agent_says()
    {
        // Without this the "says" field is optional in practice as well as in the schema, and
        // the wording coverage quietly rots as scenarios are added.
        var missing = Scenarios()
            .Select(row => (string)row[0])
            .Select(file => TextConversationSimulator.Load(Path.Combine(ScenarioDirectory, file)))
            .Where(s => s.Steps.Any(step => step.Says is null))
            .Select(s => s.Name)
            .ToArray();

        Assert.Empty(missing);
    }
}

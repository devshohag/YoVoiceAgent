using System.Text.RegularExpressions;
using CCaaS.Domain.Scheduling.Booking;
using CCaaS.Tests.Unit.Simulator;

namespace CCaaS.Tests.Unit.Robustness;

public sealed class SttCorruptionTests
{
    private static string ScenarioDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Simulator", "scenarios");

    public static IEnumerable<object[]> Cases()
    {
        var files = Directory.GetFiles(ScenarioDirectory, "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        foreach (var file in files)
        {
            var scenario = TextConversationSimulator.Load(file);
            for (var step = 0; step < scenario.Steps.Count; step++)
            {
                if (scenario.Steps[step].Kind != "say" || scenario.Steps[step].Text is null)
                    continue;

                foreach (var corruption in Corruptions)
                {
                    var corrupted = corruption.Apply(scenario.Steps[step].Text!);
                    if (string.Equals(corrupted, scenario.Steps[step].Text, StringComparison.Ordinal))
                        continue;

                    yield return new object[]
                    {
                        Path.GetFileName(file), step, corruption.Name, corrupted
                    };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Realistic_stt_corruption_never_creates_a_wrong_booking(
        string file, int step, string corruptionName, string corruptedText)
    {
        var scenario = TextConversationSimulator.Load(Path.Combine(ScenarioDirectory, file));
        var baseline = new TextConversationSimulator().Run(scenario);
        var corruptedScenario = WithCorruptedStep(scenario, step, corruptedText);
        var actual = TryRunCorrupted(corruptedScenario);

        var classification = Classify(baseline, actual);
        Assert.True(classification != RobustnessClassification.WrongBooking,
            $"{file}, step {step + 1}, corruption {corruptionName} produced a wrong booking.");
    }

    [Fact]
    public void Corruption_catalogue_reports_correct_and_question_percentages()
    {
        var results = new List<RobustnessClassification>();
        foreach (var row in Cases())
        {
            var file = (string)row[0];
            var step = (int)row[1];
            var corruptedText = (string)row[3];
            var scenario = TextConversationSimulator.Load(Path.Combine(ScenarioDirectory, file));
            var baseline = new TextConversationSimulator().Run(scenario);
            var actual = TryRunCorrupted(WithCorruptedStep(scenario, step, corruptedText));
            results.Add(Classify(baseline, actual));
        }

        var correct = results.Count(x => x == RobustnessClassification.CorrectBooking);
        var questions = results.Count(x => x == RobustnessClassification.ClarifyingQuestion);
        var wrong = results.Count(x => x == RobustnessClassification.WrongBooking);
        var total = results.Count;

        Assert.NotEqual(0, total);
        Assert.Equal(0, wrong);
        Console.WriteLine($"STT injection: {total} cases; correct={Percentage(correct, total):P1}; "
            + $"question={Percentage(questions, total):P1}; wrong={Percentage(wrong, total):P1}");
    }

    private static ConversationScenario WithCorruptedStep(
        ConversationScenario scenario, int step, string corruptedText) => new()
        {
            Name = scenario.Name + "-stt-corruption",
            NowLocal = scenario.NowLocal,
            Contact = scenario.Contact,
            Welcome = scenario.Welcome,
            BusinessOpen = scenario.BusinessOpen,
            BusinessClose = scenario.BusinessClose,
            Policy = scenario.Policy,
            Steps = scenario.Steps.Select((current, index) => index == step
                ? new ScenarioStep
                {
                    Kind = current.Kind,
                    Text = corruptedText,
                    Slots = current.Slots,
                    Appointments = current.Appointments,
                    Reference = current.Reference,
                    Failure = current.Failure,
                    Stage = current.Stage,
                    Actions = current.Actions,
                    Says = current.Says,
                    Expect = current.Expect
                }
                : current).ToList()
        };

    private static RobustnessClassification Classify(
        IReadOnlyList<SimulationFrame> baseline, IReadOnlyList<SimulationFrame>? actual)
    {
        var baselineBooking = GetBookingIdentity(baseline);
        var actualBooking = actual is null ? null : GetBookingIdentity(actual);

        if (actualBooking is not null && baselineBooking is not null)
            return actualBooking.Equals(baselineBooking) ? RobustnessClassification.CorrectBooking
                : RobustnessClassification.WrongBooking;

        if (actualBooking is not null)
            return RobustnessClassification.WrongBooking;

        return RobustnessClassification.ClarifyingQuestion;
    }

    private static IReadOnlyList<SimulationFrame>? TryRunCorrupted(ConversationScenario scenario)
    {
        try
        {
            return new TextConversationSimulator().Run(scenario);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static BookingIdentity? GetBookingIdentity(IReadOnlyList<SimulationFrame> frames)
    {
        var final = frames[^1];
        if (final.State.Stage != BookingStage.Booked || final.State.Selected is null)
            return null;

        return new BookingIdentity(
            final.State.Selected.SlotId,
            final.State.CallerName,
            final.State.Contact,
            final.State.Date,
            final.State.Time);
    }

    private static double Percentage(int value, int total) => total == 0 ? 0 : (double)value / total;

    private sealed record BookingIdentity(
        Guid SlotId, string? CallerName, string Contact, DateOnly? Date, TimeOnly? Time);

    private sealed record Corruption(string Name, Func<string, string> Apply);

    private enum RobustnessClassification
    {
        CorrectBooking,
        ClarifyingQuestion,
        WrongBooking
    }

    private static readonly Corruption[] Corruptions =
    {
        new("homophone-four", text => ReplaceFirst(text, "four", "for")),
        new("homophone-two", text => ReplaceFirst(text, "two", "to")),
        new("homophone-ate", text => ReplaceFirst(text, "ate", "eight")),
        new("digit-word-split", text => ReplaceFirst(text, "ten thirty", "10 30")),
        new("digit-word-split-four-thirty", text => ReplaceFirst(text, "four thirty", "4 30")),
        new("meridiem-split", text => ReplaceFirst(text, "a.m.", "a m")),
        new("meridiem-split-pm", text => ReplaceFirst(text, "p.m.", "p m")),
        new("dropped-article", text => ReplaceFirst(text, "at the ", "at ")),
        new("merged-words", text => ReplaceFirst(text, "half past", "halfpast")),
        new("inserted-filler", text => ReplaceFirst(text, "tomorrow", "tomorrow um")),
        new("weekday-confusion", text => ReplaceFirst(text, "Tuesday", "Thursday")),
        new("truncated-start", text => ReplaceFirst(text, "tomorrow", "morrow")),
        new("number-run-on", text => ReplaceFirst(text, "four thirty", "fourty three"))
    };

    private static string ReplaceFirst(string input, string oldValue, string newValue) =>
        Regex.Replace(input, Regex.Escape(oldValue), newValue, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
}
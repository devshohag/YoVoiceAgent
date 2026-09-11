using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CCaaS.Domain.Scheduling.Booking;
using CCaaS.Domain.Scheduling.DateTimeParsing;

namespace CCaaS.Tests.Unit.Simulator;

// Test-only adapter. System replies are scripted, never sent to a real database.
// JSON flow syntax is the deliberately supported YAML 1.2 subset; no YAML dependency
// or production registration is needed. See docs/task-2.4-text-conversation-simulator.md.
public sealed class ConversationScenario
{
    public required string Name { get; init; }
    public required DateTime NowLocal { get; init; }
    public required string Contact { get; init; }
    public TimeOnly BusinessOpen { get; init; } = new(9, 0);
    public TimeOnly BusinessClose { get; init; } = new(18, 0);
    public BookingPolicy Policy { get; init; } = BookingPolicy.Default;
    public required List<ScenarioStep> Steps { get; init; }
}

public sealed class ScenarioStep
{
    public required string Kind { get; init; }
    public string? Text { get; init; }
    public List<OfferedSlot>? Slots { get; init; }
    public string? Reference { get; init; }
    public BookingFailure? Failure { get; init; }
    public required BookingStage Stage { get; init; }
    public required string[] Actions { get; init; }
    // Presence matters: an explicit null asserts that a previously collected slot was cleared.
    public Dictionary<string, JsonElement> Expect { get; init; } = new();
}

public sealed record SimulationFrame(int Step, string Input, BookingState State,
    string[] Actions, string[] SpokenLines);

public sealed class TextConversationSimulator
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static ConversationScenario Load(string path) =>
        JsonSerializer.Deserialize<ConversationScenario>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Empty scenario: {path}");

    public IReadOnlyList<SimulationFrame> Run(ConversationScenario scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario.Name) || scenario.Steps.Count == 0)
            throw new InvalidDataException("A scenario needs a name and at least one step.");
        if (scenario.NowLocal.Kind != DateTimeKind.Unspecified)
            throw new InvalidDataException("NowLocal must be a fixed local wall clock without Z or an offset.");

        var context = new DateTimeParseContext(scenario.NowLocal,
            scenario.BusinessOpen, scenario.BusinessClose);
        var machine = new BookingConversationMachine(new EnglishDateTimeParser(), policy: scenario.Policy);
        var current = machine.Begin(scenario.Contact);
        var frames = new List<SimulationFrame> { Frame(0, "begin", current) };

        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];
            // Reject invented database replies: fixtures must follow emitted actions.
            // The production machine is exercised unchanged, including its waiting states.
            BookingInput input = step.Kind switch
            {
                "say" when step.Text is not null => new BookingInput.CallerSpoke(step.Text),
                "silence" => new BookingInput.CallerSilent(),
                "availability" when current.State.Stage == BookingStage.CheckingAvailability
                    && step.Slots is not null => new BookingInput.AvailabilityChecked(step.Slots),
                "committed" when current.State.Stage == BookingStage.Booking
                    && !string.IsNullOrWhiteSpace(step.Reference) => new BookingInput.BookingCommitted(step.Reference),
                "rejected" when current.State.Stage == BookingStage.Booking
                    && step.Failure.HasValue => new BookingInput.BookingRejected(step.Failure.Value),
                _ => throw new InvalidOperationException(
                    $"{scenario.Name}, step {i + 1}: invalid {step.Kind} input in {current.State.Stage}.")
            };
            current = machine.Advance(current.State, input, context);
            frames.Add(Frame(i + 1, step.Text ?? step.Kind, current));
        }
        return frames;
    }

    private static SimulationFrame Frame(int step, string input, BookingTurn turn) =>
        new(step, input, turn.State, turn.Actions.Select(Describe).ToArray(), turn.SpokenLines.ToArray());

    private static string Describe(BookingAction action) => action switch
    {
        BookingAction.Speak s => s.ExpectsReply ? "Speak" : "Speak:closing",
        BookingAction.LookUpAvailability l => $"Lookup:{l.Date:yyyy-MM-dd}:"
            + $"{l.PreferredTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "none"}:{l.DayPart}",
        BookingAction.CommitBooking c => $"Commit:{c.SlotId}:{c.CallerName}:{c.Contact}",
        BookingAction.TransferToHuman t => $"Transfer:{t.Reason}",
        BookingAction.EndCall e => $"End:{e.Reason}",
        _ => throw new InvalidOperationException($"Unhandled action: {action.GetType().Name}")
    };

    // Stable projection for scenario assertions and readable failure reports.
    public static Dictionary<string, object?> Snapshot(BookingState state) => new()
    {
        ["date"] = state.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["time"] = state.Time?.ToString("HH:mm", CultureInfo.InvariantCulture),
        ["dateCertainty"] = state.DateCertainty.ToString(),
        ["timeCertainty"] = state.TimeCertainty.ToString(),
        ["selected"] = state.Selected?.SlotId.ToString(),
        ["selectedUtc"] = state.Selected?.StartsAtUtc.ToString("O", CultureInfo.InvariantCulture),
        ["offered"] = state.Offered.Select(s => s.SlotId.ToString()).ToArray(),
        ["name"] = state.CallerName,
        ["contact"] = state.Contact,
        ["reference"] = state.BookingReference,
        ["handoff"] = state.HandoffReason.ToString(),
        ["noProgress"] = state.NoProgressTurns,
        ["callerTurns"] = state.CallerTurns,
        ["emptyLookups"] = state.EmptyLookups,
        ["terminal"] = state.IsTerminal,
        ["waiting"] = state.IsWaitingOnSystem
    };
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CCaaS.Domain.Scheduling.Booking;
using CCaaS.Domain.Scheduling.DateTimeParsing;

namespace CCaaS.Tests.Unit.Simulator;

// Task 2.4 - text conversation simulator.
//
// Drives the real Task 2.3 machine, parser and English phrase bank through scripted
// transcripts. System replies are scripted; nothing touches a database. Fixtures are JSON,
// parsed with System.Text.Json - no YAML dependency, and the extension says so, because the
// point of a fixture format is that somebody who is not a C# developer can add a transcript
// and have it either work or fail with a message they understand.
//
// WHAT THIS PROVES AND WHAT IT DOES NOT
// -------------------------------------
// It proves the conversation: which stage the call is in, what the agent says, and the exact
// database actions it would ask for, in order. It proves nothing about SQL - no transaction,
// no concurrency, no duplicate-booking behaviour under load. Every `Commit` below is an
// assertion about a payload, never an execution, so a green run here does not mean a booking
// would succeed against the real AppointmentService.

/// <summary>
/// Policy fields as a scenario may supply them - a plain class with property initialisers, so
/// a fixture can override one field and the rest keep their real defaults. Deserialising
/// <see cref="BookingPolicy"/> directly would depend on the serialiser honouring positional
/// record defaults; getting that wrong silently sets MaxTurns to zero and every call transfers
/// on its first turn, which is a confusing way to find out.
/// </summary>
public sealed class PolicyOverride
{
    public int MaxNoProgressTurns { get; init; } = BookingPolicy.Default.MaxNoProgressTurns;
    public int MaxTurns { get; init; } = BookingPolicy.Default.MaxTurns;
    public int MaxAlternativesOffered { get; init; } = BookingPolicy.Default.MaxAlternativesOffered;
    public bool RequireName { get; init; } = BookingPolicy.Default.RequireName;

    public BookingPolicy ToPolicy() =>
        new(MaxNoProgressTurns, MaxTurns, MaxAlternativesOffered, RequireName);
}

public sealed class ConversationScenario
{
    public required string Name { get; init; }
    public required DateTime NowLocal { get; init; }
    public required string Contact { get; init; }

    /// <summary>Opening line from the agent configuration. Absent means the call opens with the question alone.</summary>
    public string? Welcome { get; init; }

    public TimeOnly BusinessOpen { get; init; } = new(9, 0);
    public TimeOnly BusinessClose { get; init; } = new(18, 0);
    public PolicyOverride Policy { get; init; } = new();
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

    /// <summary>
    /// What the agent must say this turn, one entry per spoken line, each matched as a
    /// case-insensitive substring. Optional, but where it is present it is the only thing
    /// standing between a working agent and one that says the wrong time confidently.
    /// </summary>
    public string[]? Says { get; init; }

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
        var machine = new BookingConversationMachine(
            new EnglishDateTimeParser(), policy: scenario.Policy.ToPolicy());

        var current = machine.Begin(scenario.Contact, scenario.Welcome);
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

    /// <summary>
    /// Phrases that assert to the caller that an appointment exists. None of them may be
    /// spoken while <see cref="BookingState.BookingReference"/> is null - see
    /// <c>BookingInvariants</c> in the test runner for why this is checked on every frame of
    /// every scenario rather than left to individual fixtures.
    /// </summary>
    public static readonly string[] BookingClaims =
    {
        "you're booked", "youre booked", "you are booked",
        "that's booked", "thats booked", "is booked", "i've booked", "ive booked"
    };
}

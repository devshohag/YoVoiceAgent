using CCaaS.Domain.Scheduling.DateTimeParsing;

namespace CCaaS.Domain.Scheduling.Booking;

/// <summary>
/// Where the booking conversation currently stands.
///
/// WHY A STATE MACHINE AND NOT A PROMPT
/// ------------------------------------
/// An LLM is excellent at understanding what a caller meant and terrible at guaranteeing what
/// it will do next. A booking has irreversible consequences - a slot is taken, a customer is
/// told a time, a van is dispatched - and "the model usually confirms before booking" is not a
/// property anyone can ship. So understanding stays with the LLM and the parser; the decision
/// of what happens next lives here, where it is a pure function that can be exhaustively
/// tested and will behave identically on call one and call one hundred thousand.
///
/// Three stages exist only to wait for the outside world - CheckingAvailability, Booking -
/// because the machine performs no I/O. It emits an action, the orchestrator carries it out,
/// and the answer comes back as the next input.
/// </summary>
public enum BookingStage
{
    /// <summary>Nothing collected yet; the agent has greeted and asked the open question.</summary>
    Opening,

    /// <summary>A date, a time, or both are still missing.</summary>
    CollectingWhen,

    /// <summary>A closed question is outstanding because a parsed value had two readings.</summary>
    ResolvingAmbiguity,

    /// <summary>Waiting for the orchestrator to return slots. No caller input is expected.</summary>
    CheckingAvailability,

    /// <summary>The requested time was not free; nearby slots have been offered.</summary>
    OfferingAlternatives,

    /// <summary>A slot is chosen but the caller's name is still unknown.</summary>
    CollectingName,

    /// <summary>The details have been read back and a yes or no is outstanding.</summary>
    ConfirmingBooking,

    /// <summary>Waiting for the orchestrator's booking result. No caller input is expected.</summary>
    Booking,

    /// <summary>Terminal. The booking is committed and the caller has been told the reference.</summary>
    Booked,

    /// <summary>Terminal. The call is going to a human being.</summary>
    Transferring,

    /// <summary>Terminal. The caller did not want to book, or the call ran out of road.</summary>
    Ended
}

/// <summary>Why the conversation was handed to a human. Written to the call record.</summary>
public enum HandoffReason
{
    None,
    CallerAsked,
    NoProgress,
    CallLengthExceeded,
    NoAvailability,
    BookingFailed
}

/// <summary>
/// One bookable slot as the conversation sees it.
/// </summary>
/// <param name="SlotId">Opaque to the machine; the orchestrator books by this.</param>
/// <param name="StartsAtLocal">
/// Start time in the CONTACT's zone. The machine compares and speaks local times only - it has
/// no timezone knowledge at all, by design, so conversion happens once at the edge and the
/// rest of the flow cannot get it wrong.
/// </param>
/// <param name="StartsAtUtc">Carried through untouched so the orchestrator never re-derives it.</param>
public sealed record OfferedSlot(
    Guid SlotId,
    DateTime StartsAtLocal,
    DateTime StartsAtUtc,
    string ProviderName);

/// <summary>Why a commit attempt failed. Each one needs a different thing said to the caller.</summary>
public enum BookingFailure
{
    /// <summary>Someone else took the slot between the offer and the commit. Recoverable - offer another.</summary>
    SlotTaken,

    /// <summary>The details were rejected, e.g. an unusable contact number. Not recoverable in speech.</summary>
    InvalidDetails,

    /// <summary>Database or network trouble. Never the caller's problem to solve.</summary>
    SystemError
}

/// <summary>Tunables for how patient and how chatty the agent is. Tenant-configurable later.</summary>
/// <param name="MaxNoProgressTurns">
/// How many consecutive caller turns may add nothing before the call goes to a human. This is
/// the most important number in this file: without it a voice bot re-asks the same question
/// forever, burning call minutes and goodwill. Two is deliberate - one repeat is a mishearing,
/// two is a conversation that is not working.
/// </param>
/// <param name="MaxTurns">Hard ceiling on caller turns, whatever the progress. Stops runaway calls.</param>
/// <param name="MaxAlternativesOffered">
/// How many alternative slots to read out at once. Three is the practical limit in speech -
/// nobody holds more than that in their head without a screen.
/// </param>
/// <param name="RequireName">Whether a name must be collected before committing.</param>
public sealed record BookingPolicy(
    int MaxNoProgressTurns = 2,
    int MaxTurns = 20,
    int MaxAlternativesOffered = 2,
    bool RequireName = true)
{
    public static readonly BookingPolicy Default = new();
}

/// <summary>
/// Everything the conversation has established so far.
///
/// Immutable and complete: the machine is a pure function of (state, input), so this record is
/// the ONLY thing that needs persisting to survive a worker restart mid-call, and replaying a
/// call from its transcript reproduces it exactly.
/// </summary>
public sealed record BookingState
{
    public BookingStage Stage { get; init; } = BookingStage.Opening;

    // ---- what the caller has asked for
    public DateOnly? Date { get; init; }
    public TimeOnly? Time { get; init; }
    public ValueCertainty DateCertainty { get; init; } = ValueCertainty.NotProvided;
    public ValueCertainty TimeCertainty { get; init; } = ValueCertainty.NotProvided;
    public DayPart DayPart { get; init; } = DayPart.None;

    /// <summary>The 1-12 hour awaiting a morning/evening decision, while in ResolvingAmbiguity.</summary>
    public int? AmbiguousHour { get; init; }
    public int AmbiguousMinute { get; init; }

    // ---- what has been offered and chosen
    public IReadOnlyList<OfferedSlot> Offered { get; init; } = Array.Empty<OfferedSlot>();
    public OfferedSlot? Selected { get; init; }

    // ---- who the caller is
    public string? CallerName { get; init; }

    /// <summary>Already E.164 from the campaign lead. Never collected by voice - it is known.</summary>
    public string Contact { get; init; } = string.Empty;

    // ---- outcome
    public string? BookingReference { get; init; }
    public HandoffReason HandoffReason { get; init; } = HandoffReason.None;

    /// <summary>A human was requested while a commit was already in flight; preserve its outcome first.</summary>
    public bool HandoffPending { get; init; }

    // ---- guard rails
    public int NoProgressTurns { get; init; }
    public int CallerTurns { get; init; }

    /// <summary>
    /// How many times a lookup has come back with nothing usable. Counted separately from
    /// <see cref="NoProgressTurns"/> and never reset, because a caller naming a second and a
    /// third day IS making progress - the calendar is what is failing. Without its own counter
    /// a thin calendar produces a polite, endless tour of the week.
    /// </summary>
    public int EmptyLookups { get; init; }

    public bool IsTerminal =>
        Stage is BookingStage.Booked or BookingStage.Transferring or BookingStage.Ended;

    /// <summary>True while the machine is waiting on the orchestrator rather than the caller.</summary>
    public bool IsWaitingOnSystem =>
        Stage is BookingStage.CheckingAvailability or BookingStage.Booking;

    public static BookingState Start(string contact) =>
        new() { Stage = BookingStage.Opening, Contact = contact };
}

// =========================================================================================
// inputs
// =========================================================================================

/// <summary>Something that happened, which the machine must react to.</summary>
public abstract record BookingInput
{
    /// <summary>The caller said something. Raw transcript text.</summary>
    public sealed record CallerSpoke(string Utterance) : BookingInput;

    /// <summary>The endpointer timed out with nothing said.</summary>
    public sealed record CallerSilent : BookingInput;

    /// <summary>Result of a <see cref="BookingAction.LookUpAvailability"/>.</summary>
    public sealed record AvailabilityChecked(IReadOnlyList<OfferedSlot> Slots) : BookingInput;

    /// <summary>Result of a successful <see cref="BookingAction.CommitBooking"/>.</summary>
    public sealed record BookingCommitted(string Reference) : BookingInput;

    /// <summary>Result of a failed <see cref="BookingAction.CommitBooking"/>.</summary>
    public sealed record BookingRejected(BookingFailure Failure) : BookingInput;
}

// =========================================================================================
// actions
// =========================================================================================

/// <summary>
/// What the orchestrator must now do. The machine never performs any of these itself - that is
/// what keeps it testable without a database, a telephone or a clock.
/// </summary>
public abstract record BookingAction
{
    /// <param name="ExpectsReply">
    /// False for closing lines. The telephony layer uses this to decide whether to re-open the
    /// microphone or to hang up, so a farewell is not followed by an awkward listening pause.
    /// </param>
    public sealed record Speak(string Text, bool ExpectsReply = true) : BookingAction;

    public sealed record LookUpAvailability(DateOnly Date, TimeOnly? PreferredTime, DayPart DayPart)
        : BookingAction;

    public sealed record CommitBooking(Guid SlotId, string CallerName, string Contact) : BookingAction;

    public sealed record TransferToHuman(HandoffReason Reason) : BookingAction;

    public sealed record EndCall(string Reason) : BookingAction;
}

/// <summary>The outcome of one turn: the new state, and what to do about it.</summary>
/// <remarks>
/// Actions are ordered and must be executed in order - a Speak that precedes a
/// CommitBooking is a filler line that covers the database round trip, and playing it
/// afterwards would leave the caller listening to silence at the one moment they are most
/// anxious about whether the booking worked.
/// </remarks>
public sealed record BookingTurn(BookingState State, IReadOnlyList<BookingAction> Actions)
{
    public IEnumerable<string> SpokenLines =>
        Actions.OfType<BookingAction.Speak>().Select(x => x.Text);
}

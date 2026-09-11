# YoVoiceAgent — Phase 2 handoff specification

**Tasks 2.5 – 2.9 · written 11 September 2026 · for a developer picking this up cold**

---

## 0. Authority and scope

The **build plan artifact** is the single source of truth for what each task is. Where any
task document in `docs/` contradicts it, the plan wins and the document is wrong.

Two numbering mistakes exist in the already-merged task docs. Do not be misled by them:

- `docs/task-2.3-booking-state-machine.md` §10 describes "Task 2.4" as a production
  orchestrator. **That is wrong.** Task 2.4 is the text conversation simulator, and it is done.
  No orchestrator task exists in Phase 2 at all.
- Several docs refer to "Task 2.5" as "retire `AppointmentService.NormalizeContact`". **That is
  wrong.** Task 2.5 is the availability and booking service; the phone-normalisation cleanup is
  one part of it, not the whole task.

This document specifies **Tasks 2.5 through 2.9**, which is everything left in Phase 2.

---

## 1. Where the code stands today

### 1.1 Delivered and merged

| Task | What landed | Where |
|---|---|---|
| 2.1 | E.164 normaliser, compliance entities, call telemetry fields, dialer indexes | `Domain/Common/PhoneNumber.cs`, `Domain/Compliance/`, `Infrastructure/Crm/PhoneNormalizationBackfill.cs` |
| 2.2 | English date/time parser with a certainty model | `Domain/Scheduling/DateTimeParsing/` |
| 2.3 | Deterministic booking conversation state machine | `Domain/Scheduling/Booking/` |
| 2.4 | Text conversation simulator, 23 scenarios | `tests/CCaaS.Tests.Unit/Simulator/` |

Test count after 2.4: roughly **279 xUnit cases**, all green.

### 1.2 Task 2.3 is PARTIAL against the plan — read this before starting 2.6

The plan specifies for 2.3:

> Greeting → Intent → Date → Time → Name → Phone → Availability → Readback → Confirm → Book →
> Close; সাথে Correction, Cancel, Reschedule, Repeat, Handoff, Unknown

What `BookingConversationMachine` actually implements:

| Plan element | Status |
|---|---|
| Date, Time, Name, Availability, Readback, Confirm, Book, Close | done |
| Correction | done (a new value beats a bare "no") |
| Handoff | done (`WantsHuman` honoured from any stage) |
| Unknown | done (no-progress counter → handoff) |
| **Intent stage** | **missing** — the machine assumes the caller wants to book |
| **Phone stage** | **missing by design** — contact comes from the campaign lead at `Begin()`. For inbound calls this will need adding |
| **Cancel** | **missing** |
| **Reschedule** | **missing** |
| **Repeat** ("say that again") | **missing** |

Closing those four gaps is Task 2.6 work. Budget for it.

### 1.3 Entities the plan calls for that DO NOT EXIST yet

| Entity | Status | Needed by |
|---|---|---|
| `ConversationSessions` | **missing** | persisting `BookingState` across a worker restart |
| `ContactLists`, `ContactListItems` | **missing** | Phase 5 list upload |
| `TransferRequests`, `Queues` | **missing** | Phase 3 agent view |
| `ProviderUsage`, `DailyCallMetrics` | **missing** | Phase 3 analytics, Phase 5 cost-per-booking |
| `Script`, `Campaign` | exist | `src/CCaaS.Domain/Campaign/CampaignEntities.cs` |

Note: `src/CCaaS.Domain/Conversation/ConversationEntities.cs` contains `Conversation`,
`Participant`, `Interaction`, `Message` — that is the **omnichannel ticketing model**, not the
voice booking session. Do not reuse it for `BookingState`.

### 1.4 The appointment module as it stands

`src/CCaaS.Domain/Appointment/AppointmentEntities.cs`:

```csharp
public sealed class AppointmentAvailabilitySlot : BaseEntity
{
    public Guid AppointmentProviderId { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public AppointmentSlotStatus Status { get; set; } = AppointmentSlotStatus.Available;
    // NO RowVersion. NO Capacity. NO BookedCount.
}

public sealed class AppointmentBooking : BaseEntity
{
    public Guid AppointmentProviderId { get; set; }
    public Guid AvailabilitySlotId { get; set; }
    public string BookingReference { get; set; } = default!;
    public string CustomerName { get; set; } = default!;
    public string CustomerContact { get; set; } = default!;   // stored as "01712345678", not E.164
    public string? Purpose { get; set; }
    public AppointmentBookingStatus Status { get; set; } = AppointmentBookingStatus.Confirmed;
    public DateTime ConfirmedAtUtc { get; set; } = DateTime.UtcNow;
    // NO IdempotencyKey.
}
```

Indexes currently configured in `CcaasDbContext.OnModelCreating` (lines ~193–200):

```csharp
AppointmentProvider          UNIQUE (TenantId, Name)
AppointmentAvailabilitySlot  UNIQUE (TenantId, AppointmentProviderId, StartsAtUtc)
AppointmentBooking           UNIQUE (TenantId, AvailabilitySlotId)
AppointmentBooking           UNIQUE (TenantId, BookingReference)
```

`src/CCaaS.Infrastructure/Appointment/AppointmentService.cs` currently:

- opens a **`IsolationLevel.Serializable`** transaction for every booking;
- prevents double-booking with the `(TenantId, AvailabilitySlotId)` unique index;
- throws `InvalidOperationException` for *both* "slot already booked by someone else" *and*
  "slot no longer available" and "provider not available" — the caller cannot tell them apart;
- validates the contact with a private `NormalizeContact` that accepts **only Bangladesh**
  numbers and stores them in national form `01XXXXXXXXX`.

For the record, since it has been mis-stated before: `NormalizeContact` **does accept**
`+8801712345678` — it strips non-digits, converts a 13-digit `880…` to `0…`, then requires 11
digits starting `01` with a third digit 3–9. What it rejects is every **non-Bangladesh** number,
and what it stores diverges in format from the `PhoneE164` columns Task 2.1 added.

### 1.5 Who calls the booking service

| Caller | File | Behaviour on failure |
|---|---|---|
| Voice worker | `src/CCaaS.Workers.Telephony/LocalAiVoiceProcessor.cs` ≈ line 430 | catches `InvalidOperationException`, clears the slot, says "that slot was just booked", returns to the date-time stage |
| AI tool handler | `src/CCaaS.Infrastructure/Ai/DevelopmentAiProviders.cs` → `BookAppointmentTool` | lets the exception escape to `AiAgentService.ExecuteToolAsync`, which records `AiToolExecutionStatus.Failed` |
| REST API | `src/CCaaS.Api/Controllers/AppointmentsController.cs` | standard controller error handling |

**All three are live paths today.** Task 2.5 must not break them. The new
`BookingConversationMachine` is a *fourth* consumer that does not yet exist anywhere in the
call path — wiring it up is not part of Phase 2.

---

## 2. Working rules

```powershell
cd D:\yovoiceagent-clean
git checkout main
git pull --ff-only origin main
git checkout -b feat/task-2.X-short-name

# ... make changes ...

dotnet build CCaaS.sln -c Release --warnaserror    # CI uses --warnaserror; Debug hides the errors it catches
dotnet test  CCaaS.sln -c Release

git add .
git commit -m "..."
git push -u origin feat/task-2.X-short-name
# open a PR against main, wait for backend + frontend + compose jobs, then merge
```

- **`--warnaserror` is not optional.** Task 2.1 passed locally and failed CI on a nullability
  warning that Debug treats as a warning.
- **EF migrations** are created from `src/CCaaS.Api`:
  ```powershell
  cd src\CCaaS.Api
  dotnet ef migrations add <Name> --project ..\CCaaS.Infrastructure --startup-project .
  ```
  Read the generated `Up`/`Down` before committing. Existing migrations live in
  `src/CCaaS.Infrastructure/Migrations/`.
- **Verify the Task 2.1 migration is applied** to your database before starting 2.5. The
  compliance tables and `PhoneE164` columns come from it.

---

## 3. Invariants — do not break these

1. **Every table carries `TenantId`; every query filters on it.** `CcaasDbContext` builds global
   query filters by reflection over `ITenantOwned`. If you use `IgnoreQueryFilters()`, you must
   add the tenant predicate by hand — `AppointmentService` does exactly this and it is
   deliberate, because AI and background scopes have no ambient tenant.
2. **All timestamps UTC.** Local time exists only for display and for the caller's spoken
   values. The booking state machine deals exclusively in the contact's local time and knows
   nothing about zones — conversion happens at the edge.
3. **Phone numbers normalise to E.164 on write.** DNC matching depends on it.
4. **Consent, DNC and suppression rows are never updated or deleted.** They are legal records.
5. **Anything that dials or books carries an idempotency key.**
6. **The "confirmed" line is spoken only after the database write succeeds**, never before.
   `BookingConversationMachine` already enforces this: the only path to `Booked` is a
   `BookingCommitted` input.
7. **The Domain project carries no EF attributes.** All mapping lives in `CcaasDbContext`.
   Keep it that way.
8. **`BookingConversationMachine.Advance` stays a pure function** — no clock, no database, no
   model, no mutable field. Its entire test strategy depends on this.

---

## 4. Task 2.5 ⭐ — Availability and booking service

> **Plan:** slot lookup, RowVersion optimistic concurrency, idempotency key, slot-race scripted
> path · depends on 2.1

This is the critical-path task for the Phase 2 gate ("০ duplicate booking · slot race কভার্ড").

### 4.1 Entity changes

`src/CCaaS.Domain/Appointment/AppointmentEntities.cs`:

```csharp
public sealed class AppointmentAvailabilitySlot : BaseEntity
{
    public Guid AppointmentProviderId { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public AppointmentSlotStatus Status { get; set; } = AppointmentSlotStatus.Available;

    /// <summary>Seats on this slot. 1 for a one-to-one appointment; >1 for a group session.</summary>
    public int Capacity { get; set; } = 1;

    public int BookedCount { get; set; }

    /// <summary>
    /// SQL Server rowversion. The database changes it on every UPDATE, so two callers reaching
    /// for the same slot cannot both win: the second SaveChanges throws
    /// DbUpdateConcurrencyException, which is how the "that slot has just gone" line gets said.
    /// </summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public sealed class AppointmentBooking : BaseEntity
{
    // ... existing members unchanged ...

    /// <summary>
    /// Deterministic hash of (TenantId, AvailabilitySlotId, normalised contact). A retried
    /// request produces the same key and therefore the same booking, rather than a second one.
    /// </summary>
    public string IdempotencyKey { get; set; } = default!;
}
```

`src/CCaaS.Infrastructure/Persistence/CcaasDbContext.cs`, in the appointment index block
(around line 193):

```csharp
modelBuilder.Entity<AppointmentAvailabilitySlot>()
    .Property(x => x.RowVersion).IsRowVersion();

modelBuilder.Entity<AppointmentBooking>()
    .Property(x => x.IdempotencyKey).HasMaxLength(64).IsRequired();

modelBuilder.Entity<AppointmentBooking>()
    .HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
```

Keep the existing `UNIQUE (TenantId, AvailabilitySlotId)` index. The two do different jobs:
that one stops a slot being sold twice; the idempotency index makes a **retry** return the
caller's own booking instead of an error.

### 4.2 Migration

```powershell
cd src\CCaaS.Api
dotnet ef migrations add AddAppointmentConcurrencyAndIdempotency --project ..\CCaaS.Infrastructure --startup-project .
```

Review before committing:

- `RowVersion` must be `rowversion`, not `varbinary(8)`. If EF generates the wrong type, fix it
  in `Up()` by hand.
- `IdempotencyKey` is `nvarchar(64) NOT NULL`. **Existing rows have no value**, so the generated
  migration will fail on a non-empty table. Add a backfill statement inside `Up()` before the
  `NOT NULL` alter:

  ```sql
  UPDATE appointment.AppointmentBookings
  SET IdempotencyKey = CONVERT(nvarchar(64), HASHBYTES('SHA2_256',
        CONVERT(nvarchar(400), TenantId) + ':' +
        CONVERT(nvarchar(400), AvailabilitySlotId) + ':' + CustomerContact), 2)
  WHERE IdempotencyKey IS NULL OR IdempotencyKey = '';
  ```

  This must produce the same value as the C# derivation in §4.3 — test one row both ways.
- `Down()` must drop the index before the column.

### 4.3 Idempotency key derivation

New file `src/CCaaS.Domain/Appointment/BookingIdempotency.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace CCaaS.Domain.Appointment;

public static class BookingIdempotency
{
    /// <summary>
    /// Derived rather than supplied, because the callers that need protecting are the ones that
    /// cannot remember a key: a retried queue message, a re-sent tool call, a caller who says
    /// "yes" twice. The tuple is what makes two requests the same booking.
    /// </summary>
    public static string Derive(Guid tenantId, Guid slotId, string normalisedContact)
    {
        var material = $"{tenantId:N}:{slotId:N}:{normalisedContact.Trim().ToLowerInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
```

64 hex characters, deterministic, and the contact is not readable in an index.

### 4.4 A result type instead of an exception

The state machine needs to distinguish `SlotTaken` (recoverable — offer another) from
`InvalidDetails` and `SystemError` (hand to a person). Three different `InvalidOperationException`
messages cannot do that.

`src/CCaaS.Application/Appointment/AppointmentModule.cs`:

```csharp
public enum BookingOutcome
{
    Booked,
    /// <summary>This exact request already succeeded. Same booking returned; not an error.</summary>
    AlreadyYours,
    /// <summary>Someone else took it between the offer and the commit. Recoverable.</summary>
    SlotTaken,
    /// <summary>Name or contact unusable. Not recoverable by speech.</summary>
    InvalidDetails,
    ProviderUnavailable,
    SystemError
}

public sealed record BookingAttempt(
    BookingOutcome Outcome,
    Guid? BookingId,
    string? BookingReference,
    DateTime? StartsAtUtc,
    string? Status,
    string? Error);

public interface IAppointmentService
{
    Task<IReadOnlyList<AvailableAppointmentSlot>> GetAvailabilityAsync(Guid tenantId, DateOnly date, CancellationToken ct = default);

    /// <summary>Typed outcome. Preferred for anything that has to decide what to say next.</summary>
    Task<BookingAttempt> TryBookAsync(Guid tenantId, BookAppointmentCommand command, CancellationToken ct = default);

    /// <summary>Existing throwing shape, kept so the three live callers do not change in this task.</summary>
    Task<AppointmentBookingResult> BookAsync(Guid tenantId, BookAppointmentCommand command, CancellationToken ct = default);

    Task<IReadOnlyList<AppointmentBooking>> GetBookingsAsync(Guid tenantId, CancellationToken ct = default);
    Task<AppointmentVoucherData?> GetVoucherAsync(Guid tenantId, Guid bookingId, CancellationToken ct = default);
}
```

`BookAsync` becomes a thin wrapper: call `TryBookAsync`, return the result on
`Booked`/`AlreadyYours`, throw the same exception types as today on everything else. **The three
existing callers must keep working unchanged** — that is the whole reason for the wrapper.

Mapping for whoever wires the state machine later:

| `BookingOutcome` | `BookingFailure` (Domain/Scheduling/Booking) |
|---|---|
| `Booked`, `AlreadyYours` | → `BookingInput.BookingCommitted(reference)` |
| `SlotTaken` | `BookingFailure.SlotTaken` |
| `InvalidDetails` | `BookingFailure.InvalidDetails` |
| `ProviderUnavailable`, `SystemError` | `BookingFailure.SystemError` |

### 4.5 Concurrency: replace Serializable with RowVersion

Rewrite `AppointmentService.BookAsync` as `TryBookAsync`:

```
1. Validate name; normalise contact (§4.6). Failure → InvalidDetails.
2. key = BookingIdempotency.Derive(tenantId, slotId, contact)
3. Look for an existing booking with that key (tenant-filtered).
   Found → AlreadyYours with its details. No write.
4. Load the slot TRACKED (no IgnoreQueryFilters on the tracked read, or add the tenant predicate).
   Missing → SlotTaken.  Status != Available or BookedCount >= Capacity → SlotTaken.
5. Verify the provider is active. Not → ProviderUnavailable.
6. slot.BookedCount++;  if (slot.BookedCount >= slot.Capacity) slot.Status = Booked;
   Add the AppointmentBooking with the idempotency key and the E.164 contact.
7. SaveChangesAsync — NO explicit transaction, NO Serializable. One SaveChanges is one
   implicit transaction, and RowVersion is what makes it safe.
8. catch DbUpdateConcurrencyException            → SlotTaken
   catch DbUpdateException with SQL error 2601/2627 (unique violation):
        re-read by idempotency key → found ? AlreadyYours : SlotTaken
   catch anything else                            → SystemError (log it; never surface the text to a caller)
```

Why drop `Serializable`: it takes range locks for the whole transaction and serialises the
dialer's hot path under load. `rowversion` gives the same guarantee at the row level with no
lock held across the round trip. The plan specifies it; this is not a matter of taste.

Detecting the unique violation on SQL Server:

```csharp
catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
```

### 4.6 Phone normalisation — one normaliser, not two

Delete the private `NormalizeContact` and use Task 2.1's:

```csharp
private static (bool Ok, string? Value, string? Error) NormaliseContact(string? raw, string defaultRegion)
{
    if (string.IsNullOrWhiteSpace(raw))
        return (false, null, "Customer contact is required.");

    var contact = raw.Trim();

    // Email contacts are a real existing capability for non-voice clients. Keep them.
    if (contact.Contains('@') && MailAddress.TryCreate(contact, out var email)
        && string.Equals(email.Address, contact, StringComparison.OrdinalIgnoreCase))
        return (true, email.Address.ToLowerInvariant(), null);

    var phone = PhoneNumber.TryNormalize(contact, defaultRegion);
    return phone.Succeeded ? (true, phone.E164, null) : (false, null, phone.Error);
}
```

`PhoneNumber`'s API (from Task 2.1):

```csharp
PhoneNormalizationResult TryNormalize(string? raw, string defaultRegion = "BD");  // (Succeeded, E164, Error)
string Normalize(string? raw, string defaultRegion = "BD");                       // throws on failure
bool   IsE164(string? value);
```

Four consequences to handle, not just the code change:

1. **Stored format changes** from `01712345678` to `+8801712345678`. Search for every caller
   that compares or filters on `AppointmentBooking.CustomerContact` and fix it — start with
   `AppointmentsController` and `AppointmentVoucherPdf`.
2. **Existing rows must be backfilled** in the same migration, or lookups by contact break:
   ```sql
   UPDATE appointment.AppointmentBookings
   SET CustomerContact = '+880' + SUBSTRING(CustomerContact, 2, 10)
   WHERE CustomerContact LIKE '01%' AND LEN(CustomerContact) = 11;
   ```
   Run the idempotency backfill **after** this one, so the key is derived from the final value.
3. **The default region must be configurable**, not hard-coded `"BD"`. Take it from tenant
   settings with `"BD"` as the fallback, and pass it into `TryBookAsync`. Non-Bangladesh numbers
   are the whole point of the change.
4. The error message that reaches a caller must stay speakable. `PhoneNumber`'s errors are
   developer-facing; map them to one spoken sentence.

### 4.7 Availability lookup

`GetAvailabilityAsync` needs two changes:

- exclude slots that are full: `slot.BookedCount < slot.Capacity` as well as
  `Status == Available`;
- add an overload that takes a UTC range rather than a single date, because the conversation
  needs "the rest of today" and "tomorrow morning" without fetching a whole day and filtering
  in memory:

  ```csharp
  Task<IReadOnlyList<AvailableAppointmentSlot>> GetAvailabilityAsync(
      Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
  ```

Keep the existing single-date method — `CheckAppointmentAvailabilityTool` uses it.

### 4.8 Tests

**Unit** (`tests/CCaaS.Tests.Unit/Appointment/`):

- `BookingIdempotency.Derive` is stable across calls, case-insensitive on the contact, and
  different for different tenants / slots / contacts.
- Contact normalisation: BD local, BD E.164, international (`+14155552671`), email, rubbish.
- `BookingOutcome` → `BookingFailure` mapping table.

**Concurrency — this needs a real database.** EF Core's InMemory provider does not implement
`rowversion`, so a slot-race test against it proves nothing. Two honest options:

- **Preferred:** add a `tests/CCaaS.Tests.Integration` project using Testcontainers for SQL
  Server, and write: *N parallel `TryBookAsync` calls for one slot with capacity 1 → exactly one
  `Booked`, N−1 `SlotTaken`, exactly one row in `AppointmentBookings`.* Run it in CI as a
  separate job so the unit suite stays fast.
- **Minimum:** a documented manual test against a local SQL Server, with the result recorded in
  `docs/phase2-gate.md`. Say plainly in the gate report that it is manual.

Do not claim the slot race is covered on the strength of a mocked test. The gate asks for zero
duplicate bookings; only a real database can answer that.

### 4.9 Acceptance

- [ ] Migration applies and reverts cleanly on a database with existing bookings
- [ ] `RowVersion` is SQL `rowversion`; `IdempotencyKey` is unique per tenant
- [ ] The three existing callers compile and behave unchanged
- [ ] `TryBookAsync` returns `AlreadyYours` — not an error, not a second row — when called twice
      with identical arguments
- [ ] Parallel booking of one slot yields exactly one row
- [ ] `+14155552671` books successfully; contacts are stored in E.164
- [ ] `dotnet build -c Release --warnaserror` clean; full suite green

---

## 5. Task 2.6 — Readback, correction, and the missing stages

> **Plan:** confirmation readback generator + correction handling — natural English readback of
> date / time / name / number, catching "no, not Thursday" · depends on 2.3

### 5.1 Already delivered by 2.3

- `EnglishBookingPhrases.ReadBackForConfirmation` — "So that's tomorrow at half past four in the
  afternoon for Rahim Uddin. Shall I book it?"
- `SpeakTime`, `SpeakDate`, `SpeakDateWithPreposition`, `SpeakOrdinal`, `SpeakReference`
- Correction at confirmation: a new date or time in the reply beats the word "no"
- A cross-scenario test invariant already asserts the readback contains the time and the name

### 5.2 What 2.6 must add

**a. Contact readback.** The number is never spoken back today. For outbound it is known from
the lead and should be confirmed once: *"and I'll text the details to the number ending seven
eight, is that right?"* — last two digits only, never the whole number, because a full number
read aloud is a privacy problem on a recorded line.

**b. Negative-only correction.** "No, not Thursday" currently clears everything and re-asks the
open question. It should clear **only the contested component** and re-ask that one:

```
"no, not Thursday"     → clear Date, keep Time, ask "Which day, then?"
"not four, earlier"    → clear Time, keep Date, ask "What time suits you better?"
```

Implement by detecting a negation whose object parses as a date or as a time, in
`OnConfirmation` and `OnAlternativeChosen`.

**c. Repeat.** "Sorry, what?", "say that again", "can you repeat that" → re-speak the current
question **without** incrementing `NoProgressTurns`. Today it counts as a failure and two
repeats hand the call to a person, which is wrong: a caller asking you to repeat yourself is
engaged, not stuck. Add `CallerMove.Repeat` to `CallerIntent` and a `CurrentQuestion` re-speak
path in the machine.

**d. Cancel.** New stage `CancellingAppointment`: find the caller's upcoming booking by contact,
read it back, confirm, then emit a new `BookingAction.CancelBooking(bookingId)`. Needs a
service method `CancelAsync(tenantId, bookingId, reason, ct)`.

**e. Reschedule.** Cancel-then-book as one flow, and **never cancel before the new slot is
committed** — a caller who ends up with nothing because the new time was taken is worse off than
one who was told "that's gone, keep your old time?".

### 5.3 Tests

Extend the 2.4 simulator rather than writing new C# fixtures: add scenarios `24-cancel`,
`25-reschedule`, `26-repeat-does-not-spend-patience`, `27-partial-correction`. The
`says` assertions and the four invariants come for free.

### 5.4 Acceptance

- [ ] "No, not Thursday" keeps the time and asks only for the day
- [ ] "Say that again" does not increment `NoProgressTurns`
- [ ] A cancellation reads the appointment back before cancelling
- [ ] A reschedule never leaves the caller with no appointment
- [ ] The contact readback never speaks more than the last two digits

---

## 6. Task 2.7 — Duplicate protection

> **Plan:** idempotency key derivation, unique index, retry-safe handler · depends on 2.5

Most of this lands in 2.5. What remains:

**a. `Calls.IdempotencyKey`.** Task 2.1 added the column. **Nothing writes it.** Derive it at
dial time from `(TenantId, CampaignId, ContactId, attemptNumber)` and add the unique index the
plan specifies. Without this, a redelivered queue message dials the same person twice — which
is a compliance problem, not just a bug.

**b. Retry-safe tool handler.** `BookAppointmentTool` currently lets exceptions escape.
Switch it to `TryBookAsync` and return a structured outcome, so an LLM retry of the same tool
call reports "already booked" instead of an error the model may narrate as a failure to the
caller while the booking actually exists.

**c. Worker retry.** `LocalAiVoiceProcessor` catches `InvalidOperationException` and returns to
the date-time stage. With `TryBookAsync` it should branch on outcome — `AlreadyYours` must read
out the existing reference, not restart the conversation.

**d. Duplicate-booking test.** Same request sent 5 times concurrently → one row, five identical
references returned.

---

## 7. Task 2.8 — STT error injection tests

> **Plan:** golden transcripts with realistic ASR corruptions applied; does the brain stay
> correct · depends on 2.4

The point is not to prove the parser is perfect. It is to find **which corruptions cause a wrong
booking rather than a clarifying question** — a wrong booking is the expensive failure; an extra
question costs seconds.

### 7.1 Corruption catalogue

Build these from real `faster-whisper` output on 8 kHz phone audio, not from imagination. As a
starting set:

| Class | Example | Expected behaviour |
|---|---|---|
| Homophone | "four" → "for", "two" → "to"/"too", "ate" → "eight" | parse correctly or ask |
| Digit/word split | "ten thirty" → "10 30", "1030" | parse correctly |
| Meridiem split | "a.m." → "a m", "8 PM" → "8 p m" | parse correctly (2.2 covers this) |
| Dropped article | "at the fifteenth" → "at fifteenth" | parse correctly |
| Merged words | "half past" → "halfpast" | ask, do not guess |
| Inserted filler | "tomorrow um at uh four" | parse correctly (2.2 covers) |
| Weekday confusion | "Tuesday" → "Thursday" | **cannot be detected** — this is why the readback exists |
| Truncated start | "…morrow at four" | ask |
| Number run-on | "four thirty" → "fourty three" | ask, do not book 04:03 |

### 7.2 Harness

`tests/CCaaS.Tests.Unit/Robustness/`:

1. Take each 2.4 scenario's caller utterances as the golden transcript.
2. Apply one corruption at a time.
3. Run the same scenario.
4. Classify the outcome as **correct booking**, **clarifying question**, or **wrong booking**.
5. Assert **zero wrong bookings**, and report the correct/question split as a percentage.

A wrong booking is a hard failure. A clarifying question is a pass, counted separately so the
number can be improved later without blocking the gate.

---

## 8. Task 2.9 — Phase 2 gate report

`docs/phase2-gate.md`, containing real measurements, not aspirations:

- total test count and pass rate, by project
- scenario matrix: which conversation paths are covered, which are not
- the slot-race result, and **whether it was measured against a real database or by hand**
- STT-injection results: corruption classes, correct / question / wrong counts
- an explicit list of what Phase 2 did **not** deliver (see §1.2 and §1.3)
- the gate verdict, with the numbers that support it

**Phase 2 gate, from the plan:**

> ২০০+ টেস্ট কেস সবুজ · ০ duplicate booking · slot race কভার্ড · কোনো অডিও ছাড়াই

The test-count criterion is already met (~279 after 2.4). The other two are what 2.5 and 2.8
exist to satisfy. Do not sign the gate on the test count alone.

---

## 9. Open questions somebody has to decide

| # | Question | Blocks |
|---|---|---|
| 1 | Where does the contact's timezone come from — contact, campaign, or tenant? The machine takes `NowLocal` and `StartsAtLocal` from the caller and has no fallback chain. | Phase 5.1; any real wiring before then needs an interim answer |
| 2 | Which regions must book? `DayFirst` and the default phone region both depend on it. | Task 2.5 §4.6 |
| 3 | Is `ConversationSessions` built in Phase 2 or deferred? Without it `BookingState` does not survive a worker restart mid-call. | any production use of the state machine |
| 4 | Does `AppointmentBooking` get a `ContactId` FK? Today it stores a loose contact string, so a booking cannot be joined to a `Contact`, its consent, or its DNC status. | Phase 5 compliance reporting |
| 5 | Is the Testcontainers integration project approved? It adds a Docker requirement to CI. | Task 2.5 §4.8 |

---

## 10. After Phase 2

Per the plan, **Phase 2.5 — English voice path** (2 weeks, $0 API spend), starting with:

- `2.5.1 ⭐` Docker CPU isolation — `cpuset` separating `ollama` from `local-ai`; contention is
  measured at 6–8×
- `2.5.2 ⭐` STT benchmark harness — `tiny.en` / `base.en` / `small.en` on the same 8 kHz WAV,
  RTF and transcript at concurrency 1 and 3
- `2.5.3` VAD trim before STT

Gate: p50 ≤ 3 s on a single call, task success ≥ 90 % over 20 golden calls, $0 API cost.

Wiring `BookingConversationMachine` into the live call path belongs here or in Phase 4, not in
Phase 2. When that happens it needs: an orchestrator executing `BookingAction`s in order (the
filler `Speak` before a lookup exists to cover the round trip), timezone conversion at the edge,
`BookingState` persistence, and `TransferToHuman` routed into the existing
`IAiAgentService.RequestHandoffAsync` / `IHandoffContextSummarizer` path.

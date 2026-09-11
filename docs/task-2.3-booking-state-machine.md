# Task 2.3 — Booking conversation state machine

**Phase 2 — Outbound foundation · Branch:** `feat/task-2.3-booking-state-machine`
**Depends on:** Task 2.2 (`IDateTimeParser`) — merge that first.

---

## 1. What this task delivers

Task 2.2 turned *what the caller said* into a `DateOnly` and a `TimeOnly`. This task turns that
into **a booked appointment** — the flow that asks, offers, confirms, books, recovers and hands
over.

| File | Purpose |
|---|---|
| `src/CCaaS.Domain/Scheduling/Booking/BookingConversation.cs` | Stages, state, inputs, actions, policy |
| `src/CCaaS.Domain/Scheduling/Booking/CallerIntent.cs` | Yes / no / human / end / unsure, and name extraction |
| `src/CCaaS.Domain/Scheduling/Booking/BookingPhrases.cs` | Every line the agent can say, behind an interface |
| `src/CCaaS.Domain/Scheduling/Booking/BookingConversationMachine.cs` | The transition function |
| `tests/CCaaS.Tests.Unit/Scheduling/BookingConversationMachineTests.cs` | 24 whole-call scenarios |
| `tests/CCaaS.Tests.Unit/Scheduling/BookingPhrasesTests.cs` | How dates and times are spoken |
| `tests/CCaaS.Tests.Unit/Scheduling/CallerIntentTests.cs` | The six moves, and name extraction |

Still no NuGet packages, no configuration, no network, no database. Pure domain code.

---

## 2. The decision behind all of it

**An LLM is excellent at understanding what a caller meant, and unreliable at guaranteeing what
it will do next.** A booking is irreversible — a slot is taken, a customer is told a time, a van
is dispatched — and "the model usually confirms before booking" is not a property anyone can
ship to production.

So the two jobs are split:

| Job | Who does it |
|---|---|
| Understanding open-ended speech — a caller explaining a problem, asking what the service covers, changing the subject | The LLM |
| Turning "half past four" into a value | `IDateTimeParser` (Task 2.2) |
| **Deciding whether an appointment is booked** | **This state machine, deterministically** |

`Advance(state, input)` is a pure function: no clock, no database, no model, no mutable field.
Same state + same input ⇒ same result, on call one and on call one hundred thousand.

Four things fall out of that, and each is worth the constraint on its own:

1. **Whole calls are testable at full speed** with no telephone and no database — which is why
   the test scenarios are complete conversations, not fragments.
2. **`BookingState` is the only thing worth persisting.** A worker can die mid-call and another
   picks it up exactly where it was.
3. **A caller can never be told they are booked unless a commit actually succeeded.** The only
   path to `Booked` is a `BookingCommitted` input coming back from the database.
4. **The call cannot loop forever.** Every turn that adds nothing spends patience, and running
   out ends the call with a human on the line.

---

## 3. How the orchestrator uses it

The machine performs no I/O. It emits an action; the caller of the machine carries it out and
feeds the answer back in.

```csharp
var machine = new BookingConversationMachine(new EnglishDateTimeParser());
var ctx     = DateTimeParseContext.ForBusinessHours(nowInContactZone);

var turn  = machine.Begin(lead.PhoneE164, agent.WelcomeMessage);
var state = turn.State;

// then, for every event:
foreach (var action in turn.Actions)     // IN ORDER - see below
{
    switch (action)
    {
        case BookingAction.Speak s:
            await tts.SayAsync(s.Text);
            if (!s.ExpectsReply) micStaysClosed = true;
            break;

        case BookingAction.LookUpAvailability l:
            var slots = await appointments.GetAvailabilityAsync(tenantId, l.Date, ct);
            next = machine.Advance(state, new BookingInput.AvailabilityChecked(ToLocal(slots)), ctx);
            break;

        case BookingAction.CommitBooking c:
            try   { var r = await appointments.BookAsync(tenantId, new BookAppointmentCommand(
                        c.SlotId, c.CallerName, c.Contact, "Outbound booking"), ct);
                    next = machine.Advance(state, new BookingInput.BookingCommitted(r.BookingReference), ctx); }
            catch (InvalidOperationException)   // slot gone
                  { next = machine.Advance(state, new BookingInput.BookingRejected(BookingFailure.SlotTaken), ctx); }
            break;

        case BookingAction.TransferToHuman t: await telephony.TransferAsync(t.Reason); break;
        case BookingAction.EndCall:           await telephony.HangUpAsync();           break;
    }
}
```

**Actions must be executed in order.** A `Speak` that precedes a `LookUpAvailability` or a
`CommitBooking` is the filler line that covers the database round trip — "Let me check that for
you", "Booking that now". Playing it afterwards leaves the caller listening to silence at the
one moment they are most anxious about whether it worked.

**`StartsAtLocal` is the orchestrator's job.** The machine compares and speaks local times only
and has no timezone knowledge at all, by design. Convert once at the edge — until Phase 5.1
lands the fallback chain (contact → campaign → tenant), pass the tenant's zone.

---

## 4. The stages

```
                    Begin()
                       │
                       ▼
              ┌── CollectingWhen ◄────────────┐  "no", empty calendar, correction
              │        │                      │
              │        │ ambiguous hour       │
              │        ▼                      │
              │  ResolvingAmbiguity ──────────┤  "morning" / "evening"
              │        │                      │
              │        ▼                      │
              │  CheckingAvailability ────────┤  nothing free
              │        │                      │
              │   ┌────┴──── not exact ──► OfferingAlternatives ──┘
              │   │ exact
              │   ▼
              │  CollectingName ──► ConfirmingBooking ──► Booking ──► Booked
              │                            │                 │
              └────────────────────────────┘                 │ SlotTaken
                                                             └──► back to CheckingAvailability
  any stage, any time:
     "let me speak to a person"  ──► Transferring
     "not interested"            ──► Ended
     patience exhausted          ──► Transferring
```

---

## 5. Rules that exist because of how real calls fail

**A request for a human is honoured from any stage, immediately.** It is checked before the
stage handler runs, before the yes/no reading, before anything. "No, I want to speak to someone
else" is a transfer, not a refusal — reading it as a refusal leaves the caller arguing with a
robot, which is how complaints start.

**Two useless turns end the call with a person on it.** One repeat is a mishearing; two is a
conversation that is not working. Without this rail a voice bot re-asks the same question
forever, burning call minutes and goodwill. The counter resets the moment anything real lands.

**An empty calendar is counted separately.** A caller naming a second and a third day *is*
making progress, so the patience counter keeps resetting — and a thin calendar would produce a
polite, endless tour of the week. `EmptyLookups` never resets, and two empty lookups hand over
to someone who can look at more dates than the agent can.

**A correction beats the word "no".** "No, make it five instead" is a refusal and a new request
in one breath; only the second half is useful. The name already collected is kept — re-asking
for it is how a caller decides the agent is not listening.

**Losing a slot race is recoverable.** Two callers reaching for the same slot is ordinary under
concurrency: apologise, look again, carry on. Only `InvalidDetails` and `SystemError` end the
call with a human.

**A caller talking during a lookup costs nothing.** People fill silence. Those words answer no
question and must not spend patience.

**Nothing is booked without a commit.** "Yes" moves the stage to `Booking` and emits
`CommitBooking` — and that is all. Only `BookingCommitted` reaches `Booked`.

---

## 6. Verification

No .NET SDK in the environment this was written in, so the machine was ported line-for-line to
Python and driven through complete call transcripts. **116 checks, 0 failures** on the final
run. Three defects were found and fixed that way:

**1 — a thin calendar never handed over.** After an empty day, the caller naming another day
reset the no-progress counter, so the handover condition could never be reached. Fixed with the
separate `EmptyLookups` counter described above.

**2 — "quarter to five" was announced as evening.** `SpeakTime` took the part of day from the
hour it counted back *to* (17:00 → evening) rather than from the appointment itself. 16:45 is an
afternoon appointment; 11:45 is a morning one. Fixed to use the actual time.
→ `SpeakTime_QuarterTo_DescribesWhenTheCallerActuallyComesIn`

**3 — the call-length ceiling could not be reached** while a lookup was in flight, because turns
spoken during `CheckingAvailability` are deliberately not counted. Not a code defect in the end
— the guard is correct, and the scenario that "proved" otherwise was invalid. Recorded here so
nobody re-discovers it as a bug.
→ `CallerTalkingWhileALookupIsInFlight_IsNotCountedAgainstThem`

**4 — every earlier slot was ranked as the furthest away.** Found by the real xunit suite, not
by the port. `Rank` sorted alternatives by `TimeOnly - TimeOnly`, and **TimeOnly subtraction is
circular**: it returns the elapsed time going forward around the clock, so 15:00 minus 16:00 is
twenty-three hours rather than one. Asked for four o'clock with 15:00, 17:00 and 11:00 free, the
agent offered "five o'clock or eleven o'clock" and silently skipped the three o'clock next to
it. Fixed by ranking on minutes-since-midnight.

This is the one thing the Python port could not have caught — it used ordinary minute
arithmetic, which is what a reader assumes `TimeOnly` subtraction does. Any future port carries
the same blind spot: the port checks *logic*, the xunit suite checks *the platform*, and both
runs are needed.
→ `AnEarlierSlotCanBeTheNearestOne`, `EquallyNearSlots_AreOfferedEarliestFirst`

---

## 7. Apply — Windows / PowerShell

```powershell
cd D:\yovoiceagent-clean

git checkout main
git pull origin main                       # Task 2.2 must already be merged
git checkout -b feat/task-2.3-booking-state-machine

Expand-Archive -Path "$env:USERPROFILE\Downloads\task-2.3-booking-state-machine.zip" `
               -DestinationPath . -Force

git status --short
```

Expect additions only — `docs/`, `src/CCaaS.Domain/Scheduling/Booking/`, and three files under
`tests/CCaaS.Tests.Unit/Scheduling/`. Nothing modified, nothing deleted, **no EF migration** —
there is no entity and no schema change in this task.

---

## 8. Build and test

```powershell
dotnet restore CCaaS.sln
dotnet build CCaaS.sln -c Release --warnaserror
dotnet test  CCaaS.sln -c Release --no-build
```

Do not drop `--warnaserror`. Debug treats several nullability issues as warnings and CI does
not — that is exactly how Task 2.1 passed locally and failed in CI.

Just this suite while iterating:

```powershell
dotnet test CCaaS.sln -c Release --filter "FullyQualifiedName~Scheduling"
```

---

## 9. Commit and push

```powershell
git add .
git commit -m "feat(scheduling): deterministic booking conversation state machine

Adds the flow that turns parsed dates and times into a booked appointment:
stages, a pure (state, input) -> (state, actions) transition function, caller
intent detection for the six moves that gate irreversible actions, and a
phrase bank behind an interface so wording and language are swappable.

The machine performs no I/O - it emits actions the orchestrator executes and
feeds back - so whole calls are testable without a telephone or a database,
BookingState is the only thing that needs persisting across a worker restart,
and the only path to Booked is a commit actually coming back.

Guard rails: a request for a human is honoured from any stage; two useless
turns hand the call to a person; repeatedly empty availability is counted
separately so a thin calendar cannot produce an endless tour of the week; a
lost slot race recovers instead of losing the call.

24 whole-call scenarios plus wording and intent coverage."

git push -u origin feat/task-2.3-booking-state-machine
```

Then open the PR against `main` and merge once the three CI jobs are green.

---

## 10. Task numbering correction

The master plan and `phase2-handoff-spec.md` are authoritative. Task 2.4 is the
text conversation simulator and is merged. Production action execution, timezone
conversion, state persistence and live transfer wiring are not Phase 2 tasks.
Task 2.5 supplies typed booking outcomes and optimistic concurrency for that future
adapter. During an in-flight commit, a human request is retained in HandoffPending;
the machine records the result before transferring. A lookup can transfer immediately.

---

## 11. Open items carried forward

- **Task 2.5** — retire `AppointmentService.NormalizeContact` in favour of `PhoneNumber`
  (carried from Task 2.1). Note that `BookAsync` currently re-validates the contact with the
  Bangladesh-only rules, which will reject the E.164 numbers this flow passes in.
- **Phase 5.1** — timezone fallback chain (contact → campaign → tenant) supplying `NowLocal`
  and `StartsAtLocal`.
- **Phase 5.5** — confirm target-market regions before locking `DayFirst` per campaign.
- Make `BookingPolicy` and the phrase bank tenant-configurable once there is a second tenant
  with different hours.

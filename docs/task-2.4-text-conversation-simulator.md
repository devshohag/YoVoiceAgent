# Task 2.4 — Text conversation simulator

**Phase 2 · Branch:** `feat/task-2.4-text-conversation-simulator`
**Depends on:** Task 2.3 merged into `main`.

---

## 1. What this is, and what it is not

A test-only harness that drives the real Task 2.3 `BookingConversationMachine`,
`EnglishDateTimeParser` and English phrase bank through scripted transcripts, then asserts the
stage, the words, the collected values and the exact database actions — in order — at every
turn.

**It is not the orchestrator.** Section 10 of the Task 2.3 document describes wiring the
machine to `IAppointmentService`, persisting `BookingState` against the call session, converting
timezones at the edge and routing `TransferToHuman` into `RequestHandoffAsync`. None of that is
here. After merging this, no call can book anything.

Two things follow from that, and both matter more than the green tick:

- Every `Commit:` in these fixtures is an assertion about a **payload**, never an execution.
  Nothing touches SQL, so this proves nothing about transactions, concurrency, or duplicate
  bookings under load.
- Every fixture commits with the contact `+8801712345678`. The real
  `AppointmentService.NormalizeContact` **rejects** E.164 and accepts only local 11-digit
  Bangladesh numbers. A fully green simulator therefore sits directly on top of a booking path
  that would fail on the first real call. That is Task 2.5, and it is still open.

| File | Change | Purpose |
|---|---|---|
| `tests/CCaaS.Tests.Unit/Simulator/TextConversationSimulator.cs` | new | Scenario model, loader, simulator, stable projection |
| `tests/CCaaS.Tests.Unit/Simulator/TextConversationSimulatorTests.cs` | new | Data-driven runner, cross-scenario invariants |
| `tests/CCaaS.Tests.Unit/Simulator/scenarios/*.json` | new | 23 scenarios, 118 scripted steps |
| `tests/CCaaS.Tests.Unit/CCaaS.Tests.Unit.csproj` | modified | Copy fixtures to test output |
| `src/CCaaS.Domain/Scheduling/Booking/BookingPhrases.cs` | modified | Two phrasing bugs found by this work |
| `src/CCaaS.Domain/Scheduling/Booking/BookingConversationMachine.cs` | modified | Availability is filtered by the requested day |
| `tests/CCaaS.Tests.Unit/Scheduling/BookingPhrasesTests.cs` | modified | Cover the corrected phrasing |

No NuGet dependency. No schema, provider or CI change.

---

## 2. What building this found

Writing the simulator meant generating the agent's **actual words** for the first time, rather
than the tags the Task 2.3 unit tests assert. Three defects surfaced immediately.

**1 — "What time on tomorrow?"** `AskWhatTime`, `NothingFreeThatDay` and `OfferTimes` all glued
a preposition to a date that already carried one: *"What time on tomorrow?"*, *"there's nothing
free on today"*, *"On tomorrow I have…"*. Every unit test passed, because they assert tags.
Fixed with `SpeakDateWithPreposition` — a relative day takes no preposition, a named day takes
"on". This is the exact class of wrongness that makes a caller notice they are talking to a
machine.

**2 — availability was trusted blindly.** `OnAvailability` accepted any slot the orchestrator
returned, including one for a day nobody asked about. `Select` then adopted that slot's date, so
the appointment quietly moved. The readback would say the new date, but a half-listening caller
would not catch it. Slots are now filtered by the requested day, which turns an orchestrator bug
into "nothing free" — and that ends with a person on the line.

**3 — the original package never asserted a single spoken word.** `Describe` collapsed every
line to `"Speak"`, and `SpokenLines` was printed but never compared. Every phrase could have
been replaced with gibberish, or with "you're booked" before the commit, and all twenty
scenarios would still have passed. That is addressed below.

The twenty original fixtures were independently replayed against a separate implementation of
the machine and **matched on every stage and every action** — the flow assertions in that
package were correct. It was the wording that was unguarded.

---

## 3. Scenario format

Fixtures are **JSON**, with a `.json` extension, parsed by `System.Text.Json`. The previous
version named them `.yaml` on the grounds that JSON is a YAML 1.2 subset — true, but the first
person to open one and write a comment or an unquoted key gets a `JsonException` and no idea
why. The extension now matches the parser.

```json
{
  "kind": "say",
  "text": "tomorrow at four",
  "stage": "CheckingAvailability",
  "actions": ["Speak", "Lookup:2026-09-17:16:00:None"],
  "says": ["Let me check that for you."],
  "expect": { "date": "2026-09-17", "time": "16:00", "waiting": true }
}
```

| Field | Meaning |
|---|---|
| `kind` | `say` (needs `text`), `silence`, `availability` (needs `slots`), `committed` (needs `reference`), `rejected` (needs `failure`) |
| `stage` | Stage the machine must be in after this input |
| `actions` | The **entire ordered** action list, including an empty list when nothing should happen |
| `says` | One entry per spoken line, matched as a case-insensitive substring |
| `expect` | Optional value checks; an explicit `null` asserts a value was cleared. Unknown fields fail the test |

Scenario-level: `name`, `nowLocal` (a fixed wall clock, no `Z` and no offset), `contact`,
optional `welcome`, optional `businessOpen`/`businessClose`, optional `policy`.

`policy` may set any subset of its four fields; the rest keep their real defaults. That is why
it deserialises through `PolicyOverride` — a plain class with property initialisers — rather
than straight into the `BookingPolicy` record. Relying on the serialiser to honour positional
record defaults would silently set `MaxTurns` to zero, and every call would transfer on its
first turn. `A_partial_policy_block_keeps_the_real_defaults_for_everything_else` asserts it.

System replies are only accepted when the machine actually asked for them: an `availability`
step outside `CheckingAvailability`, or a `committed` outside `Booking`, throws. A fixture
cannot invent a database answer.

---

## 4. Invariants checked on every scenario

A fixture only catches what its author thought to assert. These four are checked on every frame
of every scenario, so a future change cannot quietly break them:

1. **No booking is claimed before one exists.** While `BookingReference` is null, no spoken line
   may contain a booking claim ("you're booked", "I've booked"…) or the spoken form of any
   reference the scenario later commits. This is the property the whole state machine exists to
   guarantee, and the one a customer would actually be harmed by.
2. **A closing line ends something.** Any `Speak` with `ExpectsReply = false` must share its turn
   with a `Transfer` or an `End` — otherwise telephony stops listening and the caller is left
   talking to a silent line.
3. **The readback carries the details.** In `ConfirmingBooking`, the last spoken line must
   contain the spoken time of the selected slot and the name the booking will be made under.
   A confirmation the caller cannot check is not a confirmation.
4. **A booked call committed the slot it agreed to.** Reaching `Booked` requires a matching
   `Commit` action earlier in the same call.

`Every_scenario_asserts_what_the_agent_says` additionally fails if any scenario is added without
`says`, so wording coverage cannot quietly rot.

---

## 5. Coverage

Twenty scenarios carried over (happy path, split collection, corrections, alternatives, ambiguous
hour, slot race, `SystemError`/`InvalidDetails`, empty calendar, silence, handoff, declining,
late events, past times, `RequireName=false`, turn ceiling), plus three new ones that the Task
2.3 unit tests do not reach:

| Scenario | Why it earns its place |
|---|---|
| `21-long-call-two-corrections` | Ten turns, the caller changes their mind twice; the date set on turn 1 survives both corrections and the name survives all of them |
| `22-unsure-then-decides` | "I don't really mind, whatever suits you" is `Unsure`, not a refusal — then the caller names a different day entirely |
| `23-welcome-line` | The opening line from the agent configuration, which nothing tested before |

**Expected: 27 xUnit cases** — 23 scenarios plus three invalid-system-reply cases, the partial
policy check, and the says-coverage check.

The overlap with `BookingConversationMachineTests` is deliberate and kept. Two independent
encodings of the same behaviour have already earned their cost once on this project: the Python
port and the C# suite disagreed about `TimeOnly` subtraction, and only having both caught it.
The suites run in milliseconds; the duplication is cheaper than the class of bug it finds.

---

## 6. Verification in the authoring environment

No .NET SDK here, so the machine was replayed through an independent Python port:

- 23 fixtures replayed: **0 mismatches** on stage, actions, `says`, and every `expect` field.
- All four invariants evaluated against all 23: **0 violations**.
- The Task 2.3 behavioural suite re-run after both production changes: **116 checks, 0 failures**.

The C# build and the xUnit suite have **not** been executed here. Wording assertions are the
likeliest place for a port-versus-platform divergence to show up — that is exactly how the
`TimeOnly` bug was found last time — so if a `says` assertion fails, send me the diff rather
than editing the fixture.

---

## 7. Apply — Windows / PowerShell

If the earlier `.yaml` version of this package was ever extracted, remove it first, or both
formats will be copied to the output.

```powershell
cd D:\yovoiceagent-clean

git checkout main
git pull --ff-only origin main
git checkout -b feat/task-2.4-text-conversation-simulator

if (Test-Path tests\CCaaS.Tests.Unit\Simulator) { Remove-Item -Recurse -Force tests\CCaaS.Tests.Unit\Simulator }

$zip = Get-ChildItem "$env:USERPROFILE\Downloads" -Filter "task-2.4-*.zip" |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $zip) { throw "No task-2.4 zip in Downloads" }
"Extracting $($zip.Name)  ($($zip.LastWriteTime))"
Expand-Archive -Path $zip.FullName -DestinationPath . -Force

git status --short
```

Expect four modified files and one new directory. **No EF migration** — no entity, no schema
change.

---

## 8. Build and test

```powershell
dotnet restore CCaaS.sln
dotnet build CCaaS.sln -c Release --warnaserror
dotnet test  CCaaS.sln -c Release
```

Readable transcripts for one scenario set:

```powershell
dotnet test tests/CCaaS.Tests.Unit/CCaaS.Tests.Unit.csproj -c Release `
    --filter "FullyQualifiedName~TextConversationSimulator" `
    --logger "console;verbosity=detailed"
```

---

## 9. Commit and push

```powershell
git add .
git commit -m "test(scheduling): text conversation simulator with spoken-line assertions

Drives the Task 2.3 machine, parser and phrase bank through 23 scripted
transcripts, asserting stage, spoken words, collected values and the ordered
database actions each turn. Four invariants are checked on every scenario,
the first being that no booking may be claimed before a commit comes back.

Fixtures are JSON with a .json extension and deserialise policy through a
plain override type, so a partial policy block keeps the real defaults
instead of silently zeroing the call-length ceiling.

Fixes two defects this work exposed: date phrasings glued a preposition to
a relative day ('What time on tomorrow?'), and availability was accepted for
days the caller never asked about, which quietly moved the appointment."

git push -u origin feat/task-2.4-text-conversation-simulator
```

---

## 10. Still open

- **Task 2.5** — retire `AppointmentService.NormalizeContact` in favour of `PhoneNumber`.
  Until this lands, every booking this flow would attempt is rejected for its phone number.
- **The orchestrator** — execute the actions, persist `BookingState`, convert timezones at the
  edge, wire `TransferToHuman` to the existing handoff path. Do not tick Task 2.4 as "the flow
  is wired" on the strength of this package.
- **Phase 5.1** — timezone fallback chain (contact → campaign → tenant) supplying `NowLocal`
  and `StartsAtLocal`.

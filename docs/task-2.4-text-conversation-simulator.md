# Task 2.4 — Text conversation simulator

**Phase 2 — Appointment brain · Branch:** `feat/task-2.4-text-conversation-simulator`
**Depends on:** Task 2.3 merged into `main`.
**Package base commit:** `1f84ff09e8d8cab49b6042e594cede24ed68d589`

---

## 1. What this task delivers

A test-only text conversation simulator using the actual Task 2.3
`BookingConversationMachine`, `EnglishDateTimeParser` and English phrase bank.
It accepts transcript strings and scripted system replies, retaining every turn's
state, collected slots, ordered actions and spoken lines. The xUnit runner compares
these with scenario expectations and writes a readable turn trace to test output.

This implements Task 2.4 from the supplied master plan. Section 10 of the Task 2.3
document describes a production orchestrator; that is not included here. The master
plan's simulator scope takes precedence. Existing domain contracts are preserved,
including contact supplied at Begin, rather than collected through a new phone stage.

| File | Purpose |
|---|---|
| `tests/CCaaS.Tests.Unit/Simulator/TextConversationSimulator.cs` | Scenario loader, simulator and stable state/action projection |
| `tests/CCaaS.Tests.Unit/Simulator/TextConversationSimulatorTests.cs` | Data-driven xUnit runner, deterministic replay and invalid system-input checks |
| `tests/CCaaS.Tests.Unit/Simulator/scenarios/*.yaml` | 20 scenarios, 99 scripted steps |
| `tests/CCaaS.Tests.Unit/CCaaS.Tests.Unit.csproj` | Copy YAML fixtures into test output; only existing-file modification |
| `docs/task-2.4-text-conversation-simulator.md` | Scope, format, verification and application commands |

No production code, domain contracts, database schema, provider settings or CI workflows
are modified. No NuGet dependency is added.

---

## 2. Scenario format

Files use **YAML 1.2's JSON-compatible flow syntax**. This is intentionally a restricted
format parsed with the existing System.Text.Json runtime, not a general YAML parser.
Keep the quoted keys, braces and arrays shown in the supplied files. Block-style YAML,
comments, anchors and tags are not supported. This avoids introducing a dependency or
maintaining a custom YAML parser for this task. Files were also checked with a YAML parser.

Each scenario supplies a fixed `nowLocal` wall clock, contact, optional business hours
and policy, followed by ordered steps. The machine starts with `Begin(contact)`.
Each step declares the expected stage and the **entire ordered action list**, including
an empty list when nothing should happen. Optional `expect` fields check collected
values; explicit null checks that a value was cleared. Unknown expectation fields fail.

Input kinds:

| Kind | Required input | Meaning |
|---|---|---|
| `say` | `text` | One raw transcript string |
| `silence` | none | Caller endpointer timeout |
| `availability` | `slots` (may be empty) | Scripted lookup response; only while checking availability |
| `committed` | `reference` | Scripted successful commit; only while booking |
| `rejected` | `failure` | SlotTaken, InvalidDetails or SystemError; only while booking |

For example, one caller step is:

```json
{
  "kind": "say",
  "text": "tomorrow at four",
  "stage": "CheckingAvailability",
  "actions": ["Speak", "Lookup:2026-09-17:16:00:None"],
  "expect": {"date": "2026-09-17", "time": "16:00", "waiting": true}
}
```

Slots provide explicit local and UTC timestamps and stable IDs. The simulator carries
these through rather than reading the wall clock or the machine's local timezone.
It does not validate a timezone conversion implementation: that belongs to the adapter
that will eventually supply these values in production.

`Speak` and `Speak:closing` check action order and whether another reply is expected.
The real spoken text appears in the trace; copy changes do not break flow assertions.
`Lookup` includes requested date/time/day part. `Commit` includes the exact slot ID,
caller name and contact. `Transfer` and `End` include their reason.

---

## 3. Coverage and limits

The 20 scenario tests cover:

- Complete booking and split date/time collection.
- Correction before committing; name retained; corrected slot is committed.
- Nearest alternatives, earlier/equidistant slots and accepting a single alternative.
- Ambiguous morning/evening resolution.
- Scripted slot-race recovery and a fresh confirmation before the second commit.
- SystemError and InvalidDetails handoff without a booking reference.
- Repeated empty availability, silence limit and caller-requested handoff.
- Declining, rejecting a readback and handing off at readback without a write.
- Late caller events after booking and caller events during pending database work.
- Past time rejection and recovery.
- RequireName=false and the caller-turn ceiling.

Three additional xUnit cases reject unsolicited availability/commit/failure replies
at the simulator boundary. Each scenario runs twice and compares complete traces to
check replay determinism and isolation. Expected addition: **23 xUnit cases**.

Database actions are asserted, not executed. This does not prove real SQL concurrency,
transaction rollback, idempotency or zero duplicate bookings under load. Those remain
Task 2.5/2.7 and the Phase 2 gate. It does not add live telephony, an application
orchestrator, persisted state, cancellation/rescheduling APIs or extra machine stages.

---

## 4. Verification in the authoring environment

- YAML and JSON decoding: 20 files validated; 99 steps.
- Fixture fields, input sequencing against declared expected states, enum names,
  timestamps, slot IDs and scenario names checked statically.
- Project XML and whitespace diff checked.
- Package contents checked against the changed-file list and source bytes.
- No .NET SDK is available in this environment; SDK download was blocked. The C# build
  and xUnit suite have **not been executed here**. No passing CI or merge clearance is
  claimed. Run the commands below and require all existing PR checks to pass.

---

## 5. Apply — Windows / PowerShell

Start from a clean worktree, with Task 2.3 merged. This ZIP contains complete files and
extracts directly into the repository root; there is no extra wrapping directory.
The base commit above identifies exactly which test-project file it was built against.
If that file has since changed on main, review its diff before committing so newer
project entries are not lost during extraction.

```powershell
cd D:\yovoiceagent-clean

git status --short
git checkout main
git pull --ff-only origin main
git checkout -b feat/task-2.4-text-conversation-simulator

Expand-Archive -Path "$env:USERPROFILE\Downloads\task-2.4-text-conversation-simulator.zip" `
               -DestinationPath . -Force

git status --short
git diff -- tests/CCaaS.Tests.Unit/CCaaS.Tests.Unit.csproj
```

Expected: one modified test-project file; new simulator directory and task document.
Git may collapse the 22 new simulator files into a single directory entry.

---

## 6. Build and test

```powershell
dotnet restore CCaaS.sln
dotnet build CCaaS.sln -c Release --no-restore --warnaserror
dotnet test CCaaS.sln -c Release --no-build
```

Focused run with readable transcript output:

```powershell
dotnet test tests/CCaaS.Tests.Unit/CCaaS.Tests.Unit.csproj -c Release --no-build `
    --filter "FullyQualifiedName~TextConversationSimulatorTests" `
    --logger "console;verbosity=detailed"
```

The existing CI also runs frontend and compose/script checks. Local backend success
alone is not full CI clearance. A feature-branch push requires a PR to trigger the
current pull-request workflow.

---

## 7. Commit and push

Only continue after build/test and diff review succeed.

```powershell
git add tests/CCaaS.Tests.Unit/Simulator tests/CCaaS.Tests.Unit/CCaaS.Tests.Unit.csproj docs/task-2.4-text-conversation-simulator.md
git diff --cached --stat
git commit -m "test(scheduling): add text conversation simulator and YAML scenarios"
git push -u origin feat/task-2.4-text-conversation-simulator
```

Open a PR from this branch to `main`. The master plan marks Task 2.4 with a star:
obtain the three reviews and wait for backend, frontend and compose checks to pass,
then merge through the user's review workflow. Nothing is pushed or merged by this package.

Suggested PR title: `Task 2.4: text conversation simulator and YAML scenarios`

Suggested PR body:

> Booking flow decisions need reproducible transcript tests before live voice wiring.
> This adds a test-only simulator around the existing Task 2.3 machine, with 20 YAML
> scenarios covering 99 scripted steps and three invalid system-reply cases. Tests
> assert states, collected slots and ordered database-action payloads, and replay
> scenarios for determinism. Production code and contracts are unchanged. The test
> project only gains fixture-copy metadata. Authoring checks validated fixture syntax
> and packaging; C# build/xUnit require local execution and CI because the authoring
> environment has no .NET SDK.

# Task 2.2 — English spoken date/time normalizer

**Phase 2 — Outbound foundation · Branch:** `feat/task-2.2-english-datetime-parser`

---

## 1. What this task delivers

The outbound agent has to turn what a caller *says* into a slot it can book. Everything
downstream — availability lookup, the appointment transaction from Task 2.1, the readback, the
SMS confirmation — needs a `DateOnly` and a `TimeOnly`. Until now there was nothing that
produced them from a transcript.

This task adds a self-contained, language-scoped parser:

| File | Purpose |
|---|---|
| `src/CCaaS.Domain/Scheduling/DateTimeParsing/IDateTimeParser.cs` | Contract, result type, certainty model |
| `src/CCaaS.Domain/Scheduling/DateTimeParsing/SpokenNumbers.cs` | Word→number tables (hours, minutes, days, months, weekdays, filler) |
| `src/CCaaS.Domain/Scheduling/DateTimeParsing/EnglishDateTimeParser.cs` | The English implementation |
| `tests/CCaaS.Tests.Unit/Scheduling/EnglishDateTimeParserTests.cs` | 88 verified transcript cases |

No new NuGet packages. No configuration. No network. Pure domain code, which is why it can be
unit-tested exhaustively and why it costs nothing at runtime.

---

## 2. The three design decisions worth knowing

### 2.1 The parser reports what it guessed

`ValueCertainty` is the point of this task. A parser that returns only a `DateTime` throws away
the most useful thing it learned — whether the caller *said* the value or the parser *worked it
out*.

| Certainty | What it means | What the agent should do |
|---|---|---|
| `Explicit` | Caller stated it — "half past four in the afternoon" | Proceed |
| `Inferred` | Resolved from context — "four" → 16:00 because 04:00 is outside business hours | Proceed, but say it back before booking |
| `Ambiguous` | Two readings are both plausible — "eight" when open 08:00–20:00 | Ask a closed question |
| `NotProvided` | The utterance carried nothing for this half | Ask for it |

`IsComplete`, `NeedsConfirmation` and `IsAmbiguous` on the result are the three flags the
appointment state machine actually branches on.

### 2.2 Business hours decide a bare hour

"Four" is not ambiguous for a business open 09:00–18:00 — 04:00 is not a time anyone can visit.
This single rule removes most of the clarifying questions a naive parser would ask, which is
directly worth call minutes. When business hours admit **both** readings the result is
`Ambiguous` rather than a silent guess.

### 2.3 Nothing is read from ambient state

`DateTimeParseContext` carries `NowLocal`, the business window, the date-order convention and
`PreferFuture`. The parser never touches `DateTime.Now` or `TimeZoneInfo.Local`.

Two consequences, both deliberate:

- "Tomorrow" is reproducible, so it is testable.
- `NowLocal` must be **the contact's local time**, not the server's. Converting is the caller's
  job. This is the hook Phase 5.1 plugs the timezone fallback chain into
  (contact → campaign → tenant).

---

## 3. Deliberate conventions (read before changing behaviour)

**Weekdays.** Both "next Monday" and bare "Monday" resolve to the next occurrence. The only
difference: a bare weekday may resolve to **today** if the time has not yet passed, whereas
"next Wednesday" said on a Wednesday is always seven days out. This matches every calendar
application and matches what callers booking appointments overwhelmingly mean.

**Past times are reported, never corrected.** "Today at nine" said at 10:00 returns
2026‑09‑16 09:00 with `IsInPast = true`. Quietly moving it to tomorrow books the wrong day. The
one exception is a *bare weekday* whose time has passed today — that rolls forward a week,
because "Wednesday at 9am" said on Wednesday at 10am can only mean next Wednesday. A named
calendar date is never moved.

**Numeric dates.** `05/06` is 5 June to most of the world and 6 May to the United States.
`DayFirst` (default `true`) picks the value; when both readings are valid calendar dates the
result is **also** marked `Ambiguous`, so the agent confirms. Guessing silently here is exactly
how the wrong appointment gets booked.

**Bangla.** Not in scope, by the English‑first decision. Adding it later is a new class
implementing `IDateTimeParser` plus a registration — no edit to the appointment state machine.
That is the whole reason the interface exists now rather than later.

---

## 4. Bugs found during verification (keep the regression tests)

The container used to write this code has no .NET SDK and no NuGet access, so the parser was
verified by porting it line-for-line to Python and running the 88 cases there. Two real bugs
surfaced that way, plus one caught while writing. All three have named tests.

**1 — "10.30 a.m." was read as the 10th of March.**
A dotted European date (`10.03`) and a dotted time (`10.30`) are the same string. The meridiem is
the only thing that separates them. Fixed by giving `NumericDate` a negative lookahead
`(?!\s*(?:am|pm)\b)` and letting `HourMinuteMeridiem` accept `[:.]`. `HourMinute24` stays
**colon-only** on purpose, so `15.10` with no meridiem still reads as a date.
→ `Parse_StatedTime_IsExplicitAndNeedsNoReadback("tomorrow at 10.30 a.m.")`

**2 — "half past four" returned no time at all.**
The old `PastOrTo` regex had a minute group that could span two words, so in "at half past four"
it greedily captured `"at half"`, matched nothing, and `Regex.Matches` produced no overlapping
alternative. Five forms were broken: *half past four, quarter past five, quarter to five, ten
past ten, five to one*. Fixed by deleting the regex — `MatchPastOrTo` now scans word by word,
anchors on the past/to word, and looks back two words then one.
→ `Parse_PastAndToConstructions_AreRead` (all five)

**3 — "I am free at four" would have booked 04:00.**
`MorningPart` was `\b(?:morning|am)\b`, so the verb "am" made it a morning appointment. Narrowed
to `\bmorning\b`; "am" is a meridiem marker handled by the explicit time patterns only.
→ `Parse_TheWordAm_IsNotTreatedAsAPartOfDay`

Final verification run: **88 cases, 0 failures**, against NOW = Wednesday 16 Sep 2026 10:00,
business 09:00–18:00.

---

## 5. Apply — Windows / PowerShell

Project folder: `D:\yovoiceagent-clean`. ZIP in your Downloads folder.

```powershell
cd D:\yovoiceagent-clean

git checkout main
git pull origin main
git checkout -b feat/task-2.2-english-datetime-parser

Expand-Archive -Path "$env:USERPROFILE\Downloads\task-2.2-english-datetime-parser.zip" `
               -DestinationPath . -Force

git status --short
```

Expect exactly five lines, all additions (`??`):

```
?? docs/task-2.2-datetime-parser.md
?? src/CCaaS.Domain/Scheduling/
?? tests/CCaaS.Tests.Unit/Scheduling/
```

(Git collapses new directories, so you may see three lines rather than five files.)

Nothing is modified and nothing is deleted. **No EF migration is needed for this task** — there
is no entity and no schema change.

---

## 6. Build and test — exactly what CI runs

Run the Release build with `--warnaserror` locally. Debug treats several nullability issues as
warnings; CI does not, which is how Task 2.1 passed on your machine and failed in CI.

```powershell
dotnet restore CCaaS.sln
dotnet build CCaaS.sln -c Release --warnaserror
dotnet test  CCaaS.sln -c Release --no-build
```

Expected: build succeeds with 0 warnings, and the test run gains **~90 new passing tests** from
`EnglishDateTimeParserTests` on top of the existing suite.

To run just this suite while iterating:

```powershell
dotnet test CCaaS.sln -c Release --filter "FullyQualifiedName~EnglishDateTimeParser"
```

---

## 7. Commit and push

```powershell
git add .
git commit -m "feat(scheduling): English spoken date/time normalizer

Adds IDateTimeParser with a certainty model (Explicit / Inferred / Ambiguous)
so the appointment state machine can tell what the caller said from what the
parser guessed, and an English implementation that reads transcript-shaped
input: spelled-out numbers, split meridiems, past/to clock forms and filler.

Bare hours are resolved against business hours rather than guessed; past times
are reported rather than silently corrected; numeric dates valid under both
day-first and month-first readings are flagged for confirmation.

88 transcript cases covered, including regressions for dotted times read as
dates, past/to constructions, and the verb 'am' read as a part of day."

git push -u origin feat/task-2.2-english-datetime-parser
```

Then open the PR against `main`, wait for the three CI jobs (backend / frontend / compose), and
merge.

---

## 8. How the appointment flow will use this (Task 2.3 preview)

```csharp
var ctx = DateTimeParseContext.ForBusinessHours(nowInContactZone);
var parsed = parser.Parse(transcript, ctx);

if (!parsed.HasAnything)        => ask the open question again
if (parsed.IsAmbiguous)         => ask a closed question: "Ten in the morning, or ten at night?"
if (!parsed.HasDate)            => "What day suits you?"
if (!parsed.HasTime)            => "What time on {date}?"
if (parsed.IsInPast)            => "That's already gone — did you mean {next occurrence}?"
if (parsed.NeedsConfirmation)   => read the value back, then book
else                            => book
```

`parsed.Notes` is a diagnostic trail of how each value was resolved. It is written to the call
log and is never spoken to the caller and never parsed by other code.

---

## 9. Open items carried forward

- **Task 2.3** — wire this into the appointment state machine and the readback phrasing.
- **Task 2.5** — retire `AppointmentService.NormalizeContact` in favour of `PhoneNumber`
  (carried from Task 2.1).
- **Phase 5.1** — define the timezone fallback chain (contact → campaign → tenant) that supplies
  `NowLocal`. Until then, callers must pass a correctly converted local time.
- **Phase 5.5** — confirm the target-market region list before locking `DayFirst` defaults per
  campaign.

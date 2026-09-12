# Phase 2 Gate Report

**Date:** 12 September 2026  
**Scope:** Tasks 2.5 through 2.9  
**Branch:** `feat/task-2.8-stt-error-injection`

## Verdict

**NOT SIGNED.** The appointment and conversation implementation is present, and the
STT-injection harness reports zero wrong bookings in its current catalogue. The Phase 2 gate
cannot yet be claimed because the SQL Server race/migration tests were skipped in the recorded
local run. A real database result is required for the zero-duplicate and slot-race criteria.

## Verification Record

| Check | Result | Evidence or limitation |
|---|---:|---|
| Release build with warnings as errors | Passed | `dotnet build CCaaS.sln -c Release --warnaserror` |
| Focused appointment/booking/call/tool tests | 116 passed, 19 skipped, 0 failed | The 19 SQL tests require `CCAAS_TASK25_SQL` |
| Task 2.8 STT corruption harness | 94 passed, 0 failed | 13 catalogue transformations over copied simulator scenarios |
| SQL migration and slot-race tests | Not measured | `CCAAS_TASK25_SQL` was not configured |
| Real 8 kHz faster-whisper transcript corpus | Not available | Current harness uses the handoff catalogue applied to existing transcripts |
| API spend | Not measured in this report | Phase 2.5 voice-path work is outside this Phase 2 report |

The focused Task 2.8 run classified each case as a correct booking, a clarifying/no-confident
booking path, or a wrong booking. It asserted zero wrong bookings. A corrupted transcript that
caused the scripted adapter to diverge was treated as a clarifying path; it was not treated as a
successful booking.

## Scenario Matrix

| Conversation path | Status | Coverage |
|---|---|---|
| Greeting and intent | Covered | Existing simulator scenarios |
| Date and time collection | Covered | Happy path, split input, ambiguity, past-time cases |
| Availability lookup | Covered | Availability, empty calendar, alternatives |
| Name and contact readback | Covered | Readback invariants and contact suffix safety tests |
| Confirmation and commit | Covered | Commit only after confirmation; no-write rejection cases |
| Slot-taken recovery | Covered at state-machine level | Scenario 07; SQL race still requires execution |
| Repeat | Covered | Scenario 26 and cancellation repeat scenario |
| Partial date/time correction | Covered | Scenarios 27, 28, and negative replacement |
| Cancel | Covered in pure machine/simulator | Scenario 24 and lifecycle tests; live worker wiring is not delivered |
| Reschedule | Covered in pure machine/service tests | Scenario 25 and atomic lifecycle tests; live worker wiring is not delivered |
| Human handoff | Covered | Handoff and pending-action scenarios |
| Unknown/no-progress | Covered | Existing simulator and machine tests |
| STT corruption safety | Covered by current catalogue | 94 generated cases, zero wrong bookings |

## Task Status

### Task 2.5: Availability and Booking Service

Implemented in the domain, application, infrastructure, and migration layers:

- SQL Server `rowversion`, capacity counters, and fullness filtering
- Deterministic tenant-scoped booking idempotency key
- Typed `BookingAttempt` outcomes and compatibility `BookAsync` wrapper
- E.164 phone persistence with configurable default region support
- UTC range availability lookup
- Legacy contact and idempotency backfill in the migration

The SQL-dependent acceptance criteria remain unverified in this report.

### Task 2.6: Readback, Correction, and Missing Stages

Implemented in the pure conversation model and simulator:

- Last-two-digit contact readback
- Negative-only date/time correction
- Repeat without spending the no-progress budget
- Cancellation readback before cancellation action
- Atomic reschedule action that secures the new appointment before replacing the old one

The machine emits lifecycle actions, but a live telephony orchestrator consuming those actions is
not part of the delivered Phase 2 path.

### Task 2.7: Duplicate Protection

Implemented:

- Campaign call idempotency derivation and persistence
- Retry-safe sequential call-session lookup
- Structured booking-tool outcomes
- Voice handling for `AlreadyYours` and `SlotTaken`
- Appointment duplicate tests, pending SQL execution

Concurrent campaign-origin retries still need a database-level race test and outcome handling for
a unique-constraint collision between lookup and insert.

### Task 2.8: STT Error Injection

Implemented at `tests/CCaaS.Tests.Unit/Robustness/SttCorruptionTests.cs`. The harness applies the
following transformations one at a time to caller utterances from the existing simulator:

- homophones: `four`/`for`, `two`/`to`, `ate`/`eight`
- digit/word split
- meridiem split
- dropped article
- merged `half past`
- inserted filler
- weekday substitution
- truncated start
- number run-on

The current 94-case run produced zero wrong bookings. This is a transcript-level harness, not a
measurement from real audio. The catalogue should be replaced or extended with labelled output
from real 8 kHz faster-whisper calls when that corpus exists.

## Explicitly Not Delivered by Phase 2

- Live wiring of `BookingConversationMachine` lifecycle actions into telephony
- Persistent `ConversationSessions` for worker-restart recovery
- Real-time English voice path, streaming gateway, VAD, Piper, or caller-heard TTFA gate
- Real 20-call golden audio set and replay metrics such as WER, entity accuracy, intent accuracy,
  and task success
- Contact-list upload, queues, transfer requests, provider-usage rollups, and daily metrics
- Outbound compliance engine and production dialer
- Bangla STT/date-time/phrase provider work
- A completed SQL Server slot-race measurement in this report

## Required Before Signing

1. Run the SQL migration, idempotency, tenant-isolation, rollback, and parallel slot-race tests
   with `CCAAS_TASK25_SQL` pointed at a disposable SQL Server.
2. Record the actual SQL test totals and the parallel result: one capacity-1 winner, zero
   duplicate rows, and all other attempts classified as `SlotTaken` or `AlreadyYours` as
   appropriate.
3. Add real faster-whisper 8 kHz transcripts to the Task 2.8 corpus and record the
   correct/question/wrong split by corruption class.
4. Re-run the full Release build and test suite, then update this report with the final numbers.
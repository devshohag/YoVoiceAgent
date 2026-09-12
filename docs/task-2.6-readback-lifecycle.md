# Task 2.6 — Readback, correction, cancellation and rescheduling

Base: `c860aa7` (main, merged Task 2.5 PR #6, including both SQL test fixes).
Authority: `docs/phase2-handoff-spec.md` section 5. The existing unfiltered
`UNIQUE (TenantId, AvailabilitySlotId)` remains unchanged. No package dependencies are added.

## Delivered

- An explicit intent question accepts booking, cancellation and rescheduling. Giving a
  date/time immediately still starts a booking without an extra intent turn.
- Confirmation includes only the last two telephone digits, spoken as words. Email contacts
  get a generic contact-on-file question. When the name is optional, the spoken name is
  `you`, never the contact number used internally by the existing booking command.
- Negative-only date/time corrections discard the negated component and preserve the other.
  A replacement after a comma, `but`, `instead`, or `make it` is parsed independently.
  Name introductions at confirmation also cause a new readback, never an immediate write.
- Repeat replays the actual previous question. It preserves `NoProgressTurns`; the overall
  caller-turn ceiling still applies. System waits do not count caller chatter as no progress.
- Cancellation looks up upcoming appointments for the known contact. Exactly one matching
  appointment is read back and explicitly confirmed before `CancelBooking` is emitted.
  Zero/multiple matches or a contested identity go to a human instead of choosing a booking.
- Rescheduling first confirms the old appointment, then collects and confirms the new slot.
  `RescheduleBooking` is one service operation, not a separate cancel followed by a book.
  No cancellation action is emitted before a replacement is secured.
- Late success events are accepted only in their corresponding waiting stage. A human
  request during a mutation is fulfilled after the result is recorded.

## Service contract and persistence

`FindUpcomingAsync(tenantId, contact, fromUtc)` normalizes contact with the tenant's configured
region and filters all joined tables by tenant. It returns confirmed future appointments.
The adapter converts their UTC times into `ExistingAppointment.Slot.StartsAtLocal` at the edge.

`CancelAsync(tenantId, bookingId, reason)` records status, UTC cancellation timestamp and reason.
A retry returns `AlreadyCancelled`. A historical booking row is retained, so its old slot is
marked **Blocked**, with `BookedCount = 0`. It cannot be advertised or sold again under the
original unfiltered slot uniqueness rule. This is an intentional consequence of retaining
that index, not group-capacity support.

`RescheduleAsync(tenantId, bookingId, newSlotId)` loads both slots tracked, inserts the
replacement, cancels the original and updates both slots in **one SaveChangesAsync**.
The implicit SQL transaction rolls back all writes on a constraint/concurrency failure.
The original slot's RowVersion prevents two concurrent changes to different target slots
from both succeeding. The new slot's RowVersion and existing indexes protect its capacity.
There is no explicit transaction and no Serializable isolation.

The replacement stores `RescheduledFromBookingId`. A unique filtered index on
`(TenantId, RescheduledFromBookingId)` permits only one replacement per original booking.
Identical retries return `AlreadyRescheduled` with the same reference while the replacement
is still confirmed. A retry targeting a different slot or a no-longer-active replacement
returns a conflict; it never claims an inactive appointment is confirmed.

Tenant authorization is mandatory. These service methods accept trusted booking IDs, not
caller-provided authority. A future voice adapter must resolve the known/authenticated
contact through the upcoming lookup and confirm its returned booking. No public cancel or
reschedule endpoint is introduced here.

## New migration

`20260912090000_AddAppointmentLifecycle` adds three nullable booking fields:
`CancelledAtUtc`, `CancellationReason` (500 characters), and `RescheduledFromBookingId`,
plus the filtered replacement index. Existing booking rows need no invented backfill values.
The Designer and model snapshot are included. Existing migrations are not edited.
Down drops the new index before its column, retains booking rows and statuses, and removes
only the new metadata. Cancellation history/link metadata is therefore lost on downgrade;
it does not reactivate appointments. Review before applying to a real database.

## Adapter events (future wiring, not live worker integration)

| Emitted action | Service operation | Input on success |
| --- | --- | --- |
| FindUpcomingAppointments | FindUpcomingAsync, convert UTC at edge | AppointmentsFound |
| CancelBooking | CancelAsync | AppointmentCancelled for Cancelled/AlreadyCancelled |
| RescheduleBooking | RescheduleAsync | AppointmentRescheduled(reference) for Rescheduled/AlreadyRescheduled |

For unsuccessful changes or lookup exceptions, the adapter must return
`AppointmentChangeRejected` (mutations) or hand off the lookup failure; never fabricate a
success event. On mutation failure the machine transfers to a person. A failed reschedule
leaves the old booking untouched by this operation; if a competing operation changed it,
reconcile with the service result before telling the caller its status.

The state machine remains pure. The three live callers remain unchanged. Voice orchestrator,
restart persistence, inbound phone collection and actual telephony handoff wiring are outside
this task, as specified in the roadmap. Multiple upcoming bookings currently transfer rather
than supporting a spoken booking-reference selection menu.

## Tests and verification

The simulator has the required 24-cancel, 25-reschedule,
26-repeat-does-not-spend-patience and 27-partial-correction transcripts, plus negative-time,
contact correction, declined cancellation, multiple-match, failed reschedule, pending handoff,
repeat-readback and negative-with-replacement coverage. Existing scenarios remain in place;
only the initial silence prompt and optional-name spoken readback expectations change.
Shared invariants verify readback date/time/name/contact suffix and reject premature success
claims or speaking the full stored contact. Safety unit tests cover suffixes, intent precedence
and unsolicited success callbacks.

Nine additional SQL tests run under the existing `appointment-sql` job's namespace filter:
retry-safe cancellation; atomic/retry-safe rescheduling; taken target preservation; tenant
isolation; normalized upcoming lookup; different-target races; same-target races; real SQL
constraint rejection rollback; and migration Up/Down over an existing booking row.
They use the existing disposable SQL Server fixture, not EF InMemory. No workflow bypasses
or skip rules have been added.

Authoring validation: all 131 C# files parsed without syntax errors, all JSON scenarios parsed,
`git diff --check` passed, and the new migration target model exactly matches the snapshot body.
The .NET 9 SDK download failed repeatedly with connection timeouts; no Release build, xUnit
execution or SQL test execution was possible here. These static checks do not prove compilation
or concurrency. Runtime results must come from your .NET/SQL execution and CI. Do not mark Phase 2 complete based on this package.

## Apply, review and push (PowerShell)

Start with Task 2.5 merged and a clean working tree:

```powershell
cd D:\yovoiceagent-clean
if (git status --porcelain) { throw "Commit or stash existing work first." }
git checkout main
if ($LASTEXITCODE -ne 0) { throw "Checkout failed" }
git pull --ff-only origin main
if ($LASTEXITCODE -ne 0) { throw "Pull failed" }
git checkout -b feat/task-2.6-readback-lifecycle
if ($LASTEXITCODE -ne 0) { throw "Branch creation failed" }
Expand-Archive "$env:USERPROFILE\Downloads\task-2.6-readback-lifecycle.zip" -DestinationPath . -Force
git status --short

dotnet build CCaaS.sln -c Release --warnaserror
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
dotnet test CCaaS.sln -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

git add .
git commit -m "feat(appointment): add readback corrections and safe lifecycle flows"
if ($LASTEXITCODE -ne 0) { throw "Commit failed" }
git push -u origin feat/task-2.6-readback-lifecycle
```

Open a PR against main. Require the existing backend/frontend/compose checks and
**appointment-sql** to pass before merge. Without `CCAAS_TASK25_SQL`, local SQL facts are
skipped by the existing fixture; a green local unit run is not SQL clearance.

To run against a disposable local SQL Server, set `CCAAS_TASK25_SQL` to that server connection
and run the same appointment namespace filter as CI. The fixture creates uniquely named
test databases, retained locally for inspection. Never use production credentials.

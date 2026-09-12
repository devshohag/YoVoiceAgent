# Task 2.7 — Duplicate protection

Base: `10264e0` (main after Task 2.6). Authority:
`docs/phase2-handoff-spec.md` section 6.

## Delivered

- Campaign outbound calls derive a 64-character SHA-256 idempotency key from tenant,
  campaign, campaign contact (`CampaignLead.LeadId`) and the explicit 1-based attempt number.
- A retry with the same campaign tuple returns the persisted `CallSession` and does not
  dispatch a second originate command. A later attempt gets a different key.
- `CampaignLeadId` and the derived key are written to `CallSession`. The filtered unique
  `(TenantId, IdempotencyKey)` index delivered by Task 2.1 remains the database race guard.
- Campaign call requests must carry `CampaignId`, `CampaignLeadId`, `LeadId` and
  `AttemptNumber`. Non-campaign/manual outbound requests keep their existing contract.
- `BookAppointmentTool` now calls `TryBookAsync` and always returns the typed booking outcome.
  `AlreadyYours` is a successful response with the original reference rather than an exception.
- `LocalAiVoiceProcessor` branches on `BookingOutcome`. Both `Booked` and `AlreadyYours`
  read the returned reference and close successfully; only `SlotTaken` clears the selected
  slot and asks for another time.

## Duplicate-booking database evidence

`AppointmentServiceSqlTests.Five_parallel_retries_return_one_reference`, introduced with
Task 2.5, is the required Task 2.7 scenario: five concurrent identical requests must return
one `Booked`, four `AlreadyYours`, one distinct reference and exactly one booking row. It runs
only when `CCAAS_TASK25_SQL` points at a disposable SQL Server and is included in CI's
`appointment-sql` job.

## Verification

Run:

```powershell
dotnet build CCaaS.sln -c Release --warnaserror
dotnet test CCaaS.sln -c Release --no-build
```

For the real duplicate/concurrency assertion, also run the existing `appointment-sql` job or
set `CCAAS_TASK25_SQL` to a disposable local SQL Server. A skipped SQL fact is not evidence of
zero duplicate rows.

# Task 2.1 — Outbound-ready schema foundation

Phase 2 · foundation task · no behaviour change, schema + one pure-logic class only.

## What this task adds and why

Task 2.1 does not make the product do anything new. It puts the columns in place that
every later outbound task depends on, *before* the tables grow — because retrofitting
consent tracking onto a populated database is both painful and legally risky.

| Added | Where | Why now |
|---|---|---|
| `PhoneNumber` E.164 normaliser | `Domain/Common` | Every DNC match, consent lookup and import de-dup compares numbers as strings. Two spellings of one subscriber = a call placed against instructions. |
| `PhoneE164` / `ValueNormalized` | `Lead`, `Customer`, `Contact` | The canonical column those comparisons read. Raw value is kept alongside it. |
| `Customer.TimeZoneId` | `Customer` | Calling windows are a per-person rule. 9 am in the campaign's zone can be 3 am where the contact is. |
| `ContactConsent`, `DoNotCallEntry`, `SuppressionCheck` | new `compliance` schema | Consent has a history — granted by whom, on what basis, revoked when. A boolean cannot answer "were we allowed to call this person on that date". |
| `CallStageTiming` | `calls` schema | Latency is the acceptance criterion, so it must be queryable per stage, per concurrency level. Field names mirror the existing Python `VOICE_TIMING` JSON so the old batch pipeline and the new gateway write the *same* schema — otherwise the before/after comparison that justifies cutover cannot be computed. |
| AMD, idempotency, recording-consent, disposition-source fields | `CallSession` | What an AI-placed campaign call needs that a human-dialled one does not. |
| `CampaignLead.State` + dialer index | `campaign` schema | The dialer's hot query is "next due contacts for this campaign". |

Deliberately **not** included: the compliance rule engine, the dialer, and list upload.
Those are Phase 5 (5.1, 5.7, 5.5). This task only lands the storage.

## Files

**New**

```
src/CCaaS.Domain/Common/PhoneNumber.cs
src/CCaaS.Domain/Compliance/ComplianceEntities.cs
src/CCaaS.Infrastructure/Crm/PhoneNormalizationBackfill.cs
tests/CCaaS.Tests.Unit/Common/PhoneNumberTests.cs
docs/task-2.1-migration.md
```

**Overwrite** (existing files, additions only — nothing removed)

```
src/CCaaS.Domain/Crm/CrmEntities.cs
src/CCaaS.Domain/Calls/CallEntities.cs
src/CCaaS.Domain/Campaign/CampaignEntities.cs
src/CCaaS.Infrastructure/Persistence/CcaasDbContext.cs
```

## Step 1 — Branch

```bash
git checkout main
git pull
git checkout -b feat/task-2.1-outbound-schema-foundation
```

Extract the zip over the repo root, then confirm only the eight files above changed:

```bash
git status --short
```

## Step 2 — Build and test

```bash
dotnet build
dotnet test tests/CCaaS.Tests.Unit/CCaaS.Tests.Unit.csproj
```

`PhoneNumberTests` carries 45 assertions. They must all pass before the migration is
generated — the normaliser is what the new columns mean, so if it is wrong the schema is
storing the wrong thing.

> The normaliser logic was verified against all 45 cases by simulation, but this sandbox
> had no NuGet access, so nothing here was compiler-checked. Treat the first `dotnet build`
> as the real verification.

## Step 3 — Generate the migration

Run from the **startup project**, as the repo README documents:

```bash
cd src/CCaaS.Api
dotnet ef migrations add AddOutboundComplianceAndCallTiming \
  --project ../CCaaS.Infrastructure \
  --startup-project .
```

The migration is generated, never hand-written: `CcaasDbContextModelSnapshot.cs` is ~130 KB
and must stay byte-consistent with the model, which only the tooling can guarantee.

### Review checklist — open the generated migration before applying it

The `Up` method must contain **only additive operations**:

- [ ] `EnsureSchema(name: "compliance")`
- [ ] `CreateTable` — `compliance.ContactConsent`, `compliance.DoNotCallEntry`, `compliance.SuppressionCheck`
- [ ] `CreateTable` — `calls.CallStageTiming`
- [ ] `AddColumn` — `crm.Lead.PhoneE164`, `crm.Customer.PhoneE164`, `crm.Customer.TimeZoneId`, `crm.Contact.ValueNormalized` (all nullable)
- [ ] `AddColumn` — `calls.CallSession`: `CampaignLeadId`, `AmdResult`, `IdempotencyKey`, `RecordingConsentAnnouncedAt`, `DispositionSetBy`, `TransferredToAgentId`
- [ ] `AddColumn` — `campaign.CampaignLead`: `State`, `FinalDisposition`, `LastCallSessionId`, `SuppressedAtUtc`
- [ ] `CreateIndex` — the filtered and composite indexes from `CcaasDbContext`

**Stop and ask if you see any of these:** `DropColumn`, `DropTable`, `AlterColumn` that
narrows a type or makes a column non-nullable, or `RenameColumn`. None of them belong in
this task, and any of them on a deployed database means data loss.

`AmdResult` and `DispositionSetBy` are new non-nullable `int` columns with enum defaults of
`0` (`NotChecked` / `NotSet`), which is the correct value for every historic row — those
calls genuinely were never AMD-checked. Confirm the generated default is `0` and not
something else.

## Step 4 — Apply to a **copy** first

```bash
# take a backup no matter what the environment is
# then restore it under a different name and point the connection string at the copy
dotnet ef database update --project ../CCaaS.Infrastructure --startup-project .
```

Only after the copy succeeds, apply to the real database. Every migration in this project
follows the same order: **backup → apply to copy → apply for real.**

## Step 5 — Backfill the normalised columns

New columns start `NULL` for existing rows. Nothing dials a `NULL`, so the system is safe
in the meantime — but DNC matching cannot work until the values exist.

Run the backfill **in dry-run first** and read the report:

```csharp
// wire PhoneNormalizationBackfill into DI, then call once:
var report = await backfill.RunAsync(defaultRegion: "BD", dryRun: true);
// examined=... normalized=... unparseable=... alreadyDone=...
```

- `unparseable` is the number that matters. A handful is normal (test rows, "N/A").
  A large count means the raw data needs fixing **before** it gets interpreted.
- Those rows keep `NULL` and are therefore never dialled. That is the safe failure
  direction, and it is intentional.

Then re-run with `dryRun: false`. The method is idempotent, so it is safe to run again
after correcting raw values by hand.

Find the rows that need attention:

```sql
SELECT Id, Name, Phone
FROM crm.Customer
WHERE IsDeleted = 0 AND Phone IS NOT NULL AND PhoneE164 IS NULL;
```

### Why the backfill is C# and not a SQL script

A SQL version would be shorter and would also create a *second* normaliser, in a second
language, free to disagree with the first. Two normalisers is exactly the defect the
column exists to prevent — a DNC entry written by one and missed by the other. The
backfill calls the same `PhoneNumber` class the application calls.

## Step 6 — Verify

```sql
-- normalised values are all canonical
SELECT COUNT(*) AS non_canonical
FROM crm.Customer
WHERE PhoneE164 IS NOT NULL AND PhoneE164 NOT LIKE '+%';
-- expect 0

-- compliance tables exist and are empty (Phase 5 fills them)
SELECT COUNT(*) FROM compliance.ContactConsent;
SELECT COUNT(*) FROM compliance.DoNotCallEntry;
SELECT COUNT(*) FROM compliance.SuppressionCheck;

-- the dialer index exists
SELECT name FROM sys.indexes
WHERE object_id = OBJECT_ID('campaign.CampaignLead');
```

Then a smoke test: place one internal call (1001 → 7000) and confirm the existing flow
still works. This task changes no behaviour, so anything that breaks is a mistake in the
merge, not a feature.

## Step 7 — Push and open the PR

```bash
git add -A
git commit -m "feat(schema): outbound compliance, phone normalisation and call stage timing

Adds the storage Phase 5 depends on, ahead of the tables growing:
- PhoneNumber E.164 normaliser (45 unit assertions) so DNC/consent
  comparisons cannot be defeated by formatting
- PhoneE164 / ValueNormalized on Lead, Customer, Contact
- Customer.TimeZoneId for per-contact calling windows
- compliance schema: ContactConsent, DoNotCallEntry, SuppressionCheck
  (append-only; SuppressionCheck logs allows as well as blocks)
- calls.CallStageTiming, field-compatible with the existing Python
  VOICE_TIMING records so old and new pipelines are comparable
- CallSession: AMD result, idempotency key, recording-consent marker,
  disposition source, transfer target
- CampaignLead.State plus the dialer's composite index

No behaviour change. Rule engine, dialer and list upload are Phase 5."

git push -u origin feat/task-2.1-outbound-schema-foundation
```

Open the PR against `main`. This is a ⭐ task, so all three reviewers:

| Reviewer | Looks at |
|---|---|
| Architecture | Is the compliance boundary right? Should `SuppressionCheck` be append-only at the DB level too? |
| Security | Do the new tables inherit the tenant query filter? (They derive from `BaseEntity`, so they should — verify, don't assume.) Is `EvidenceUri` an object-storage key only, never a payload? |
| Code quality | `PhoneNumber` edge cases; is the `Plans` table the right extension point? |

## Rollback

```bash
# before the migration is applied anywhere
git checkout main && git branch -D feat/task-2.1-outbound-schema-foundation

# after it is applied
dotnet ef database update <PreviousMigrationName> --project ../CCaaS.Infrastructure --startup-project .
```

The migration is purely additive, so `Down` drops the new tables and columns and returns
the database to its previous shape. Nothing pre-existing is modified, which is why this
task is safe to apply to the deployed server.

## Open questions for the next task

1. `AppointmentService.NormalizeContact` still holds its own Bangladesh-specific phone
   logic. It works and nothing breaks, but it is now the second normaliser in the
   codebase. **Task 2.5 should retire it in favour of `PhoneNumber`** — flagged rather
   than changed here, because touching booking behaviour does not belong in a schema task.
2. `Customer.TimeZoneId` is nullable with no fallback resolver yet. Phase 5.1's rule
   engine needs "contact zone, else campaign zone, else tenant zone" as an explicit chain.
3. `PhoneNumber.Plans` covers 12 regions. Confirm the target market list before Phase 5.5
   imports foreign lists.

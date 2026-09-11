# Task 2.5 — Availability and booking service (replacement package)

**Branch:** `feat/task-2.5-booking-service-v2`
**Base:** `e6821e12204ebf1d50a862f2fa3909db03436a85` (main with merged Task 2.4).
**Authority:** `docs/phase2-handoff-spec.md`, plus the user's explicit decision:
**one person per slot; retain UNIQUE (TenantId, AvailabilitySlotId).**

This replaces the earlier `task-2.5-appointment-lifecycle.zip`. Do not combine them.
The old lifecycle migration, filtered slot index, cancellation/rescheduling methods
and Serializable wrapper are not part of this replacement. Start from clean main.

---

## 1. Delivered

- Capacity defaults to 1 and is enforced as 1 by a SQL check constraint.
- BookedCount and SQL rowversion protect the slot; the original unfiltered unique
  tenant/slot index is retained exactly.
- BookingIdempotency derives a 64-character SHA-256 key from lower-case Guid:N tenant
  and slot IDs plus the normalized, trimmed, lower-case contact, encoded as UTF-8.
- A new tenant/idempotency-key unique index protects repeated requests.
- TryBookAsync returns Booked, AlreadyYours, SlotTaken, InvalidDetails,
  ProviderUnavailable or SystemError. BookAsync retains its original signature and
  result shape for existing callers; validation uses ArgumentException, missing slot
  uses KeyNotFoundException, other unsuccessful outcomes use InvalidOperationException.
- No explicit transaction or Serializable isolation is used in booking. One
  SaveChanges writes the slot and booking in SQL Server's implicit transaction.
- Both rowversion and unique-conflict paths re-read the request key. A winning retry
  returns its existing reference rather than being confused with another caller.
- The same-key check also runs if the slot becomes full between lookup and loading.
- Caller cancellation propagates; internal errors are logged and returned as a generic
  speakable SystemError. Failed tracked appointment entities are detached before reuse.
- Availability supports a UTC [from, to) range and excludes full slots. The original
  DateOnly overload retains its UTC-calendar-day meaning for existing callers.
- Phone normalization reuses PhoneNumber. Email capability remains. Tenant-specific
  national-number interpretation uses the existing SystemSettings table:
  Category=`Appointment`, Key=`DefaultPhoneRegion`, Value such as `US` or `BD`,
  IsActive=true. Queries include TenantId and IsDeleted. Missing/blank config uses BD.

No live worker/tool callers or frontend code are changed. CustomerContact uses in
AppointmentMasterController and AppointmentVoucherPdf were inspected: they display
it, rather than comparing it to an 11-digit national value. The old contact equality
check in AppointmentService is replaced by idempotency lookup.

Cancellation, rescheduling, Repeat and contact-tail readback remain Task 2.6, as the
new handoff spec specifies. This package does not claim the Phase 2 gate or introduce
an orchestrator. The extra fixes below are the prior review items the user requested.

---

## 2. Previous review fixes included

- The simulator asserts the actual welcome text, its trimmed form, and the opening
  question. Blank welcomes correctly produce just the question.
- Readback invariants now include the date as well as time and name.
- Premature-confirmation checks cover additional confirmation wording and curly
  apostrophes. Mutation tests demonstrate rejection of those lines before a commit.
  This is a lexical check, not a claim to understand every possible English sentence.
- Numeric ambiguous dates must be restated with a month name before lookup. The date
  and selection are cleared while the other collected values survive. This protects
  collection, confirmation corrections and alternative selection.
- A human request during availability lookup transfers immediately.
- A human request during an already-issued commit sets HandoffPending. The database
  result is recorded first, then the machine transfers; a successful reference is
  retained for the human. It does not cancel or issue another booking while uncertain.
- Four `review-*` JSON transcripts exercise these transitions. Existing scenario IDs
  01–23 and their contents are retained; IDs 24–27 remain available for Task 2.6.
- Task 2.3 section 10 is corrected: Task 2.4 was the simulator, not an orchestrator.

The handoff specification is included verbatim for future work. The user's one-seat
choice in this document overrides its group-capacity example. The machine remains
pure; no EF attributes were added to Domain and no database calls were added there.

---

## 3. Migration

`20260911180000_AddAppointmentConcurrencyAndIdempotency`:

1. Preflight legacy contacts before schema changes. Empty, unsupported phone formats
   or non-ASCII legacy contacts stop with an explicit error; no value is guessed or
   deleted. Existing Unicode email contacts require a reviewed backfill before this
   migration; the live normalizer still retains email support.
2. Add Capacity=1, BookedCount=0, SQL rowversion and nullable IdempotencyKey.
3. Normalize legacy BD national contacts to E.164; trim/lower-case legacy ASCII email.
4. Backfill keys **after** contacts, using lower-case hyphenless GUIDs and a UTF-8 SQL
   collation before HASHBYTES. This corrects the table-name, GUID and encoding mismatch
   in the sample SQL from the handoff spec. Actual table: appointment.AppointmentBooking.
5. Backfill occupied counts, including historical bookings still reserved by the
   original unfiltered unique index; an inconsistent Available status becomes Booked.
6. Make IdempotencyKey required, create its unique index and single-seat/count checks.

SQL Server generates rowversion for existing rows. It is not a user timestamp and
requires no fabricated default. Down removes the new index before the key column and
removes checks before their columns. It preserves all booking rows and the original
slot index. Contact normalization and repaired slot status are not reversed; use a
backup if exact original data formatting is needed.

Backup → restore a copy → migrate/test that copy → reviewed deployment. Adding these
columns/indexes can lock tables. Coordinate migration and application upgrade: the
old binary does not supply required IdempotencyKey, so it must not remain a writer
against the new schema. An image-only rollback is not sufficient while that required
column remains. No live database or server was touched while creating this ZIP.

The migration/target model/snapshot were prepared without running the EF CLI here.
A model-drift test and a real SQL up/down/backfill-parity test are included. They must
pass, and the restored-production-copy migration must succeed, before deployment.

---

## 4. Verification and limits

Added test cases:

- 17 appointment contract/model cases: normalization, deterministic/scoped keys,
  outcome mapping, original unique index and model/snapshot alignment.
- 10 actual SQL Server cases: typed retries and legacy wrapper, different-caller race,
  five identical concurrent requests, UTC availability, tenant isolation, configured
  region, invalid/provider outcomes, write rollback/context reuse, rowversion/capacity
  enforcement, and migration up/down with SQL/C# hash parity on populated data.
- 8 focused review regression cases, including bad-confirmation/date-omission mutations
  and welcome whitespace handling.
- 4 additional transcript scenarios alongside the 23 already merged.

The separate Appointment SQL tests workflow runs the appointment cases against a
fresh SQL Server 2022 container on PRs. Without CCAAS_TASK25_SQL, the 10 SQL cases skip
locally; the normal model/unit and simulator cases still run. Test databases always
use new CCaaS_Task25_<guid> names. Local test databases are retained for inspection;
no DROP DATABASE/EnsureDeleted is run. Use a disposable server/login permitted to
create databases. The existing main CI runs the complete unit/simulator suite.

Authoring environment: no .NET SDK, SQL Server or Docker executable. **No C# build or
xUnit/SQL test run is claimed.** Static checks validate fixture JSON, workflow YAML,
source scope, whitespace, snapshot/target-model alignment, migration operation order,
and archive/source bytes. Require actual CI results before merge. Do not interpret
these checks as evidence of measured zero duplicate bookings.

---

## 5. Apply from clean main — PowerShell

```powershell
cd D:\yovoiceagent-clean
if (git status --porcelain) { throw "Commit or stash existing work before applying this ZIP." }

git checkout main
git pull --ff-only origin main
git checkout -b feat/task-2.5-booking-service-v2

Expand-Archive "$env:USERPROFILE\Downloads\task-2.5-booking-service-v2.zip" -DestinationPath . -Force

git status --short
git diff --check
git diff --stat
```

This ZIP contains complete changed files at repository-relative paths, without a
wrapping directory. If main has changed the same files after the base SHA, merge those
changes rather than overwrite them. Do not apply the earlier Task 2.5 ZIP first. No
files need deleting when starting from the stated base. The unit-test csproj is not
changed, preserving the merged JSON fixture-copy configuration.

## 6. Build and test

```powershell
dotnet restore CCaaS.sln
dotnet build CCaaS.sln -c Release --no-restore --warnaserror
dotnet test CCaaS.sln -c Release --no-build
```

Optional actual SQL run on your disposable SQL Express server:

```powershell
$env:CCAAS_TASK25_SQL = "Server=.\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true"
dotnet test tests/CCaaS.Tests.Unit/CCaaS.Tests.Unit.csproj -c Release --no-build `
    --filter "FullyQualifiedName~CCaaS.Tests.Unit.Appointment" `
    --logger "console;verbosity=detailed"
Remove-Item Env:\CCAAS_TASK25_SQL
```

## 7. Migration against a restored copy

Set CCAAS_MIGRATION_COPY_SQL to the restored-copy connection string first.
Do not generate an additional migration for these same changes.

```powershell
if ([string]::IsNullOrWhiteSpace($env:CCAAS_MIGRATION_COPY_SQL)) {
    throw "Set CCAAS_MIGRATION_COPY_SQL to the restored-copy database first."
}
dotnet tool restore
dotnet ef database update 20260911180000_AddAppointmentConcurrencyAndIdempotency `
    --project src/CCaaS.Infrastructure --startup-project src/CCaaS.Api `
    --context CcaasDbContext --connection "$env:CCAAS_MIGRATION_COPY_SQL"
```

## 8. Commit, push and PR

After local build/test and diff review succeed:

```powershell
git add .
git diff --cached --stat
git commit -m "feat(appointment): add optimistic booking and idempotent outcomes"
git push -u origin feat/task-2.5-booking-service-v2
```

Open a PR to main. Require backend, frontend, compose and appointment-sql checks and
review clearance before merge. Task 2.5 is starred in the new spec; obtain its three
reviews. No push, PR, merge or deployment was performed by this package.

Suggested PR title: `Task 2.5: single-seat optimistic booking and review fixes`

Suggested PR body:

> Implements the revised Phase 2 handoff spec with the user's single-seat decision.
> Keeps the original tenant/slot unique index, adds rowversion/count and derived
> idempotency, introduces typed TryBookAsync while retaining BookAsync callers, and
> adds UTC-range availability plus tenant-configured phone normalization. Migration
> backfills contacts and matching UTF-8 keys before enforcing the required column.
>
> Also fixes the prior review items: welcome/date assertions, confirmation mutations,
> numeric-date ambiguity and safe handoff during pending work. Includes unit/scenario
> tests and a separate SQL Server PR test job. Authoring checks were static; .NET/SQL
> execution must pass locally/CI. Deployment requires backup, restored-copy migration
> verification and coordinated writer upgrade.

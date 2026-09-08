# Phase 2: deterministic appointment workflow (review build)

Base: `a715a0b` (Phase 1 fix). Branch: `feat/voice-phase2`.

## Scope and status

This change implements a .NET appointment conversation engine, a Bangla/English
parser, a text simulator, and an **opt-in** integration into the existing worker.
It does not replace STT, install a Python gateway, change CPU allocation, purchase
an API, or deploy to the server. SQL Server and the existing tenant model remain.
The simulator uses memory only: it cannot create real bookings.

The measured STT delay and incorrect transcript remain unresolved. A deterministic
workflow removes Ollama from the enabled appointment path; it cannot reconstruct
words STT did not recognize. No end-to-end latency or accuracy result is claimed.

## Behavior

- Explicit intent at entry; subsequent prompts follow state, with no LLM routing.
- Book, availability query, cancel and reschedule.
- Relative dates, weekdays, numeric/calendar dates, Bangla digits, fractional times,
  AM/PM and Bangladesh mobile number normalization.
- Timezone and clock are explicit; a local day can span two UTC availability dates.
- Ambiguous times ask for clarification. No nearest-slot selection or silent
  provider selection. Month/day without year uses the current year; past dates
  are rejected. Bare weekdays mean the next occurrence, including next week for
  the same weekday. Midnight uses the stated date and is read back.
- Exact yes is required **after** a readback. Corrections invalidate confirmation.
- A language switch preserves the appointment details without confirming them.
- Reference plus matching phone is required for cancel/reschedule. This is a
  lookup check, not proof of caller identity; stronger caller verification and
  abuse controls remain a release requirement for exposed cancellation flows.
- DB success precedes confirmation text. Slot conflicts and uncertain backend
  results have different messages. A failed booking transaction uses a separate
  worker scope so its tracked writes cannot leak into conversation persistence.
- Last-turn replay and same-slot/name/contact booking retries are protected.
  This is not a cross-service exactly-once protocol. A process crash after DB
  commit but before conversation persistence still needs reconciliation testing.
- State is checked against both tenant and session. New logs expose route/action,
  not raw phone/name. Existing conversation storage contains these details and
  retains the existing access/retention responsibilities.

Supported parser grammar is deliberately bounded. Unsupported input prompts again;
this phase is not a general Bengali language understanding model. DTMF collection,
caller-heard timing, actual handoff delivery and audio buffer cancellation remain
later-phase work. Asking for a representative in an error message does not itself
complete a handoff.

## PC: apply and test before server use

Download `YoVoiceAgent-Phase2.patch` to Downloads. Start from the repository containing
Phase 1 fix `a715a0b` (or its equivalent applied changes). Do not discard local work.
Run each command only if the previous command succeeded:

```powershell
cd D:\yovoiceagent-clean
git status
git switch -c feat/voice-phase2
git am "$env:USERPROFILE\Downloads\YoVoiceAgent-Phase2.patch"
dotnet test tests/CCaaS.Tests.Unit/CCaaS.Tests.Unit.csproj -c Release
dotnet build src/CCaaS.Workers.Telephony/CCaaS.Workers.Telephony.csproj -c Release
dotnet build src/CCaaS.Api/CCaaS.Api.csproj -c Release
dotnet run --project tools/CCaaS.VoiceSimulator/CCaaS.VoiceSimulator.csproj
```

If `git am` conflicts, stop and inspect; `git am --abort` returns to the pre-apply
state. Do not reset the repository or force-push. If the branch already exists,
inspect it instead of deleting it. The SDK required by the project is .NET 9.

Simulator: fixed date **2026-09-08**, timezone Asia/Dhaka, tomorrow's slots 10:00
and 16:30. Enter the following as separate lines:

```text
আগামীকাল সকাল দশটায় appointment নিতে চাই।
আমার নাম সোহাগ
01712345678
না, বিকাল সাড়ে চারটায়
হ্যাঁ
```

Only the final line should increment `Mutations` to 1. Use `/new` for a fresh
session. Use `/race` just before confirmation to simulate a failed write. `/quit`
exits. For cancellation/rescheduling use the printed `APT-...` reference and the
same phone number in a new session. These are synthetic records only.

## Database gate and controlled server test

A migration changes booking uniqueness from every historical booking per slot to
**one active, non-deleted confirmed booking per tenant/slot**. This permits reuse
following cancellation/rescheduling while retaining historical rows.

Before live enablement:

1. Pass .NET tests/builds above and review the migration against a restored SQL
   Server database. Generate/review migration SQL with the repository's EF tooling.
2. Test simultaneous booking attempts using separate SQL connections: exactly one
   different customer succeeds; loser gets no confirmation. Test cancellation then
   rebooking, failed reschedule preserving the original slot, and tenant boundaries.
3. Take a verified database backup. Apply the migration through the existing
   `Bootstrap__MigrateDatabase` mechanism or the reviewed migration SQL in a
   maintenance window. Build the API as well as the worker: the old API image does
   not contain this migration. Turn bootstrap migration/seeding back off afterward.
4. Keep `AI_VOICE_APPOINTMENT_WORKFLOW_V2=false` until the above pass. Then enable
   it only for a controlled test deployment. This flag applies to all local-AI
   calls handled by that worker; it is **not** per-extension isolation. Do not
   pretend it is the later parallel gateway rollout.

After the reviewed commit is on the server, these commands build the required
images without recreating running containers:

```bash
cd /opt/yovoiceagent
docker compose --env-file .env.server -f docker-compose.server.yml build ccaas-api telephony-worker
```

After migration has been verified and the test deployment's `.env.server` has
`AI_VOICE_APPOINTMENT_WORKFLOW_V2=true`, recreate just its worker:

```bash
docker compose --env-file .env.server -f docker-compose.server.yml up -d --no-deps telephony-worker
docker compose --env-file .env.server -f docker-compose.server.yml logs --since 5m --no-color telephony-worker
```

Expect `VOICE_DECISION` route `appointment-v2` and a `workflow_action`. Unknown
transcripts should prompt for clarification, not invoke Ollama. End-to-end speech
can still be slow/wrong with the currently measured STT. The existing worker
remains serial by default; this phase does not establish three-call capacity.

## Rollback

Set the flag false and recreate only the worker to use the existing conversation
path. Keep the old image/commit available. Do not automatically run the migration
Down: after cancelled history shares slots the original unfiltered index cannot
be restored without resolving those historical duplicates. Never delete booking
history to force a rollback. Test old code against the migrated schema before
using it as a full application rollback.

## Validation and remaining gates

Authoring environment: no .NET SDK, Docker or SQL Server available. **C# compilation,
new xUnit tests, EF migration execution and SQL concurrency tests were not run.**
The existing seven Python voice-report tests pass. `git diff --check` and clean-base
patch application are checked on delivery. These do not replace a C# build.

New xUnit cases cover parser variants, initial utterance not becoming a name,
readback/explicit yes, correction, replay, a simulated slot race, backend timeout,
language switching, timezone boundaries, tenant/session rejection, cancel and
reschedule. In-memory tests do not prove SQL transaction behavior.

Release targets remain unachieved/unmeasured:
- Meaningful reply from end of caller speech: 1 call p50 <=3s, p95 <=5s.
- Three concurrent calls p95 <=8s.
- Labeled human scenarios: appointment success >=90%; zero observed wrong or
  duplicate bookings and zero confirmations without DB success.
- Maintain the old pipeline rollback option for at least two weeks after cutover.

Phase 2 code can be reviewed now. STT selection needs a separate measured decision
before gateway/release, using human 8 kHz phone recordings and labeled meaning.

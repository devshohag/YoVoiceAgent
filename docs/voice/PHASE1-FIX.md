# Phase 1 follow-up: duplicate processing and interpretable timing

Parent: Phase 1 instrumentation commit c00f7ba (or its git-am equivalent).
Apply this follow-up only once, on feat/voice-phase1. No database migration.

## Changes

- Pending query excludes completed markers BEFORE the 100-event limit, accepting
  both compact legacy Guid strings and the hyphenated System.Text.Json format.
  Ended/deleted calls are excluded with explicit tenant matching.
- Cancelled semaphore acquisition cleans up the in-flight entry. Active turn count
  is measured at processing admission.
- `queue_wait` is semaphore wait; `poll_cycle` includes the complete poll/task batch;
  `turn_processing_total` covers worker processing, excludes pre-admission wait and
  caller audio playback. Do not treat it as endpoint-to-ear latency.
- STT inference records include decoded audio duration and VAD-retained duration,
  beam size, CPU threads, model, compute type. Report computes RTF per record only.
- Beam and CPU threads are configurable. Defaults retain beam=3, int8, VAD and
  condition_on_previous_text=false; 4 threads explicitly matches the previous
  Faster-Whisper default. No claimed speedup without replaying real audio.
- General LLM `endCall=true` no longer authorizes termination unless the existing
  explicit goodbye matcher accepts caller text. This is a guard, not evidence that
  the model caused the previous hangup. Existing goodbye/booking/failure/handoff
  paths still apply. Phase 2 will replace appointment conversation decisions.
- Decision route/language/end-call flags and hangup cause are logged without
  transcript or phone number. Existing application logs have not been scrubbed.
- Report CLI now defaults to compact stage summary; use --details for individual
  records/RTF. No more thousands of timing records printed for the basic command.

## Known limits

This fixes repeated scheduling after a persisted processed marker. It is NOT a
cross-process exactly-once guarantee: retain one telephony-worker replica. A crash
between business commit and processed-marker commit still requires durable
operation idempotency; Phase 2 must cover that before broader release.

An orphan whose DB call remains falsely active may still need lifecycle repair.
No historical events or recordings are deleted by this patch. The polling design
still waits for its current batch; this is measured, not yet a realtime gateway.

Actual decoded/recognized text is absent from the submitted timing report, so the
previous wrong reply and hangup root cause remain unconfirmed. Do not label this
patch as a Bengali recognition fix or a release-gate pass.

## Verify before deployment

On the PC, from repository root:

```powershell
dotnet test tests/CCaaS.Tests.Unit/CCaaS.Tests.Unit.csproj --filter FullyQualifiedName~PendingVoiceTurnsTests
dotnet build src/CCaaS.Workers.Telephony/CCaaS.Workers.Telephony.csproj -c Release
```

The regression tests exercise legacy/current completion markers, tenant mismatch,
ended calls, and SQL Server query translation without connecting to a database.
Run a real SQL smoke check through the server call before merging to main.

Python tests: `python3 -m unittest discover -s scripts/voice -p 'test_*.py'`.
Seven Python tests and Python compilation passed in the editing environment.
.NET SDK and Docker were unavailable. C# tests and build are provided but NOT
reported as passed.

After successful tests/build and pushing feat/voice-phase1, on the server:

```bash
cd /opt/yovoiceagent
git pull --ff-only origin feat/voice-phase1
docker compose --env-file .env.server -f docker-compose.server.yml build telephony-worker local-ai
```

Only after build succeeds:

```bash
docker compose --env-file .env.server -f docker-compose.server.yml up -d --no-deps telephony-worker local-ai
```

Make one call, then collect only the new interval:

```bash
docker compose --env-file .env.server -f docker-compose.server.yml logs --no-color --since 5m telephony-worker local-ai > /tmp/voice-phase1-fix.log
python3 scripts/voice/report.py < /tmp/voice-phase1-fix.log
python3 scripts/voice/report.py --details < /tmp/voice-phase1-fix.log > /tmp/voice-phase1-fix-details.json
```

Expected correctness: no completed event repeatedly runs STT/LLM/TTS. Check errors
as well as success counts. Restart once after a completed call and confirm it is
not replayed. No latency target is claimed by this patch.

For a controlled beam=1 experiment set WHISPER_BEAM_SIZE=1 in .env.server and
recreate local-ai. Replay the same private labeled human set at 1/3 offered load.
Compare entity accuracy/WER as well as time; revert if quality degrades. Do not
change model, beam and thread count all together, and do not enable a paid API.

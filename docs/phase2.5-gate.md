# Phase 2.5 English Voice Path Gate Report

**Date:** 12 September 2026  
**Scope:** Tasks 2.5.1 through 2.5.11  
**Verdict:** **NOT SIGNED: implementation foundations are present, but the measured voice gate is not yet proven.**

## Gate Criteria

| Criterion | Required | Recorded result | Status |
|---|---:|---:|---|
| Single-call caller-heard TTFA p50 | <= 3 s | Pending real RTP measurement | Not verified |
| Golden-call task success | >= 90% over 20 calls | Pending real golden calls | Not verified |
| API spend | $0 | No paid provider run recorded | Not verified |
| CPU isolation | Ollama/local-ai disjoint | `ollama=0-1`, `local-ai=2-3` defaults | Implemented; VPS runtime pending |
| STT model selection | benchmark at concurrency 1 and 3 | Harness ready; no WAV/runtime available locally | Not verified |

## Task Status

| Task | Delivered | Verification |
|---|---|---|
| 2.5.1 CPU isolation | Configurable disjoint Compose `cpuset` ranges | Compose config validation passed |
| 2.5.2 STT benchmark | `scripts/voice/bench.py` for `tiny.en`, `base.en`, `small.en`, concurrency 1/3 | Harness not run: no Python runtime or real WAV in this environment |
| 2.5.3 VAD trim | PCM WAV edge silence trimming with threshold/padding settings | Compose validation passed; runtime audio test pending |
| 2.5.4 speech adapter | TTS contract, ProviderUsage entity/writer, metered development STT adapter | Release build passed; database migration execution pending |
| 2.5.5 Piper English TTS | English default, cached voice, warmup, configurable 8 kHz output | Compose validation passed; container/audio test pending |
| 2.5.6 cached prompt audio | Versioned manifest, renderer, cache lookup endpoint | Manifest and Compose validation passed; rendering pending |
| 2.5.7 deterministic routing | Pure route classifier; appointment turns cannot fall through to LLM | 5 focused tests passed |
| 2.5.8 timeout/fallback | 2500 ms speech timeout, spoken fallback event, second-failure DTMF signal | Release build and Compose validation passed |
| 2.5.9 caller-heard TTFA | Caller speech-end marker and explicit first-heard RTP hook | Hook implemented; no transport currently supplies first-frame timestamp |
| 2.5.10 golden calls | 20-call manifest template and WER/CER/business-metric evaluator | Template verified; real calls and predictions pending |
| 2.5.11 gate report | This report | Complete, verdict unsigned |

## Validation Record

- `dotnet build CCaaS.sln -c Release --warnaserror`: passed during voice-task implementation.
- Angular production builds for the design phases: passed; an existing
  `agent-workspace.scss` budget warning remains.
- Docker Compose configuration validation: passed for development and server stacks after the
  voice configuration changes.
- Task 2.5.7 deterministic routing tests: `5 passed, 0 failed`.
- Task 2.5.10: exactly 20 golden-call placeholders verified.
- No real STT benchmark numbers, TTFA percentile numbers, WER/CER numbers, entity accuracy,
  intent accuracy, or task-success percentage are claimed here.

## Measurement Procedure

Run the STT benchmark on the target host with one recorded 8 kHz mono WAV:

```powershell
python scripts/voice/bench.py path\to\golden-8khz.wav --output TestResults\stt-benchmark.json
```

Populate `eval/golden/manifest.json` with 20 real, consented, labelled English phone calls and
run:

```powershell
python eval/run.py --manifest eval/golden/manifest.json --stt-url http://localhost:8090 --output TestResults\golden-report.json
```

The golden evaluator keeps WER/CER, entity accuracy, intent accuracy, and task success separate.
Do not substitute one metric for another. The TTFA gate must use the `time_to_first_audio` rows
written from the actual first RTP frame heard by the caller, not ARI playback acknowledgement or
TTS response completion.

## Open Blockers Before Sign-off

1. Install/enable Python and provide the same real 8 kHz WAV to run the model benchmark at
   concurrency 1 and 3.
2. Collect and label the 20-call golden set, including expected appointment outcome and
   predictions from the complete voice path.
3. Connect the AudioSocket/realtime transport to `RecordCallerHeardFirstAudioAsync` and record
   p50/p95 TTFA at concurrency 1 and 3.
4. Execute the SQL Server migration and ProviderUsage persistence checks.
5. Confirm no paid provider requests were made and record the $0 result.
6. Re-run the full Release suite and update this report with real measurements.

Until these numbers exist, Phase 2.5 is implementation-ready but not a passed performance gate.
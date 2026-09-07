# Phase 1 — measurement, baseline e3bb93e

This commit instruments the existing batch path. It does not replace telephony,
change models, introduce paid APIs or fix appointment semantics (Phase 2).

## Deploy

Build gate before replacing the worker:

```bash
cd /opt/yovoiceagent
docker compose --env-file .env.server -f docker-compose.server.yml build telephony-worker local-ai
```

Only after successful build:

```bash
docker compose --env-file .env.server -f docker-compose.server.yml up -d --no-deps telephony-worker local-ai
```

Other stack dependencies must already be running. Do not start live evaluation on
an otherwise stopped stack. Retain the previous image/deployment for rollback.

## Existing-call report

```bash
docker compose --env-file .env.server -f docker-compose.server.yml logs --no-color --since 15m telephony-worker local-ai > /tmp/voice-phase1.log
python3 scripts/voice/report.py < /tmp/voice-phase1.log > /tmp/voice-phase1-summary.json
cat /tmp/voice-phase1-summary.json
```

VOICE_TIMING lines contain schema_version, pipeline_version, call_id, turn_id,
turn_no, stage, started_at, ended_at, duration_ms, concurrency, outcome.
Durations use monotonic clocks; UTC timestamps correlate services. Unavailable
turn_no/concurrency are null, never an invented value. Python concurrency is
active speech HTTP requests at admission (includes queued work), not CPU threads.

Stages: recording including silence, recording file wait/upload, worker queue,
recording download, batch STT HTTP, decision, LLM HTTP (if used), batch TTS HTTP,
response upload, response delivery through ARI acknowledgement. Provider stages:
shared speech queue, STT model load, STT batch inference including lazy segment
iteration, TTS model load/synthesis/resample. Emit starts immediately, so a
missing completion can reveal a pending operation. `incomplete` indicates scope
exit without success; exceptions still follow existing application handling.

VOICE_EVENT_LINK correlates worker queue event ID to call/recording ID and
includes event age (poll discovery + queue + database work, wall-clock estimate).
VOICE_RECORDING_LINK maps recording name to recording ID and audio duration.
Response filenames include call and recording IDs. Logs omit transcript/phone.

Do not sum nested stages. Recording time includes caller speech, not just
endpoint delay. ARI Play acknowledgement is NOT first audio heard by caller.
This baseline cannot measure true endpoint-to-ear latency without annotated
speech end and client audio capture. Streaming flush gates remain NOT_MEASURED.
Provider inference RTF = stt_inference_batch duration / source audio duration;
HTTP RTF includes network/queue/model load. Compare separately.

## Labeled human golden set

Create an untracked directory /opt/yovoiceagent/voice-eval-data. Use
`golden-set.example.json` as a format example only; no real recordings supplied.
Collect 20–30 human telephony utterances with manually checked transcript,
intent, date/time/name/phone entities, UTC recording time and business timezone.
Include noisy speech, Banglish, number corrections and ambiguous times.
Do not substitute synthesized audio. Decode to mono 8kHz 16-bit PCM WAV; verify
negotiated uLaw at the phone/Asterisk. The bundled pjsip.conf already sets
`disallow=all` / `allow=ulaw`; deployed configuration must match. Resampling wideband does not establish
real narrowband telephone quality. Human label authors must resolve what was
actually said, not what the model predicted. Store recordings/labels privately,
not in git. Empty speech/noise scenarios belong in a separate robustness set;
this WER runner requires nonempty reference transcripts.

```bash
python3 scripts/voice/benchmark.py voice-eval-data/manifest.json --validate-only
```

Replay through the existing container network without installing packages:

```bash
docker run --rm --network ccaas-server_ccaas-net -v "$PWD/scripts/voice:/runner:ro" -v "$PWD/voice-eval-data:/data" python:3.12-slim python /runner/benchmark.py /data/manifest.json --output /data/stt-report.json
docker run --rm --network ccaas-server_ccaas-net -v "$PWD/scripts/voice:/runner:ro" -v "$PWD/voice-eval-data:/data" python:3.12-slim python /runner/benchmark.py /data/manifest.json --operation tts --output /data/tts-report.json
```

First request is labeled warmup and excluded from warm summaries; it is not
necessarily cold (model may already be loaded). Replays run isolated first,
then batches of up to 3 requests, retaining current server admission limits.
Final partial batch has fewer than 3 arrivals: use case count divisible by 3
for a clean three-arrival comparison. Benchmark concurrency is offered load.
Do not run live calls alongside isolated model measurements. A timeout does not
necessarily cancel server inference; any failed run is diagnostic, not a pass.
Script returns exit 1 on failure and reports failure count separately from
successful latency/WER. Results do not expose recognized transcript. TTS timing
is full-file HTTP response, not time to first audible chunk. Use representative
short prompts for TTS; it is not an STT quality input.

## Gates (committed before measurements)

See scripts/voice/gates.json. Streaming STT p95 with queue: <=1500ms at one call,
<=3000ms at three. Meaningful reply: one call p50<=3000ms/p95<=5000ms;
three calls p95<=8000ms. Task success >=90%; zero observed wrong/duplicate
bookings. Filler does not count as meaningful reply. Batch timings do not pass
streaming gates. A local failure triggers analysis, NEVER automatic paid API.
20–30 cases provide an initial baseline, not statistically strong production
accuracy or p95 estimates. Entity/task evaluation is Phase 2; WER is not task
success. Normalized WER applies NFC, case folding, Bengali digit normalization
and punctuation removal; explicitly report this normalization.

## Validation

`python3 -m unittest discover -s scripts/voice -p 'test_*.py'`

Compile worker in Docker before deploy. Live model, call latency, WER and task
success cannot be claimed until the real labeled dataset is run on the VPS.

Implementation validation: six Python unit tests, Python compilation and patch
whitespace checks passed in the editing environment. .NET SDK/Docker are not
available there, so the worker build remains an explicit unverified gate.

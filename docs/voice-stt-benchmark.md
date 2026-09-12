# English STT Benchmark

This report is intentionally empty until it is run against the same real 8 kHz mono WAV on the
target host. Do not copy latency numbers from another machine into this table.

## Command

```powershell
python scripts/voice/bench.py path\to\golden-8khz.wav --output TestResults\stt-benchmark.json
```

The default run compares `tiny.en`, `base.en`, and `small.en` at concurrency 1 and 3 using CPU
`int8`, beam size 3, and the same VAD and previous-text settings as `infra/local-ai/app.py`.

## Results

| Model | Concurrency | p50 seconds | p95 seconds | p50 RTF | p95 RTF | Transcript |
|---|---:|---:|---:|---:|---:|---|
| tiny.en | 1 | pending | pending | pending | pending | pending |
| tiny.en | 3 | pending | pending | pending | pending | pending |
| base.en | 1 | pending | pending | pending | pending | pending |
| base.en | 3 | pending | pending | pending | pending | pending |
| small.en | 1 | pending | pending | pending | pending | pending |
| small.en | 3 | pending | pending | pending | pending | pending |

## Selection

Select the model from the measured table using both latency and transcript quality. A model is
not considered better merely because its single-call RTF is lower; concurrency 3 is required for
the decision because the production service serves overlapping calls.
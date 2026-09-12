# Golden English Call Set

This directory is reserved for 20 labelled, real English 8 kHz mono phone calls. Do not add
synthetic recordings or inferred labels. Each call must include the WAV file and a manifest row
with the ground-truth transcript, intent, extracted entities, and expected booking result.

Copy `manifest.template.json` to `manifest.json`, replace every placeholder with reviewed labels,
and keep recordings out of public repositories when they contain personal data. The evaluator
accepts an optional prediction file so the four metrics remain separate:

- WER and CER: speech recognition quality
- entity accuracy: date, time, name, and contact extraction
- intent accuracy: requested operation
- task success: whether the expected appointment outcome was achieved

Run from the repository root:

```powershell
python eval/run.py --manifest eval/golden/manifest.json --stt-url http://localhost:8090
```

The template is intentionally not a passing dataset. The gate must be based on real labelled
calls and recorded predictions.
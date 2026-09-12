# Cached Prompt Audio

Common English prompt lines are described in `infra/local-ai/prompt_manifest.json`. Render them
once with:

```powershell
python scripts/voice/render_prompt_cache.py infra/local-ai/prompt_manifest.json .\prompt-cache
```

Copy the generated files into the `local-ai-models` volume at `/models/prompt-cache`. The local
speech service then serves a cache hit directly from `GET /v1/audio/prompts/{key}` without
starting Piper. A cache miss remains a normal `/v1/audio/speech` request, so a missing optional
prompt never makes the speech service unavailable.

The rendered files are mono 8 kHz PCM WAV because the Piper endpoint performs the telephony
resample introduced in Task 2.5.5. Keep the manifest versioned and replace audio only through a
reviewed change.
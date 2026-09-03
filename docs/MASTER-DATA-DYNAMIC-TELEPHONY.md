# Master Data, dynamic telephony and provider switching

This release preserves the working AI-to-human same-call handoff while moving tenant
configuration behind authenticated database-backed APIs and the **Master Data & Settings**
screen.

## First-time database update

Run this before starting the updated API against an existing database:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
Unblock-File .\scripts\setup-master-data-database.ps1
.\scripts\setup-master-data-database.ps1
docker compose up -d --build --force-recreate ccaas-api asterisk telephony-worker
```

For a changed Wi-Fi network, run `configure-local-asterisk.ps1` before recreating Asterisk.

## Managed data

- Typed tenant settings (telephony, recording, language and future categories)
- AI provider profiles for STT, LLM and TTS with priority and active flags
- Bangla and English language/voice profiles
- Human agents and presence
- SIP extensions using secret references rather than raw passwords
- Queues and ordered queue membership
- SIP trunks, DIDs and Asterisk node inventory

The browser no longer selects extension `1002`. The API selects the first available active
queue member by priority and penalty, then uses the configured development fallback only
when necessary.

## Recording behavior

ARI per-turn recordings remain the AI input. Asterisk `MixMonitor` additionally records the
whole customer channel, including the human handoff, and the worker uploads the finalized
WAV to object storage after the call. Call History therefore shows both turn recordings and
the final call recording.

## Provider policy

Development profiles include local Ollama and local Whisper endpoints. Production profiles
remain inactive until a real endpoint and secret-store reference are configured. Raw API
keys and SIP passwords must never be entered in master-data values.

The current browser Puter bridge remains available as a development adapter while server-side
STT/TTS workers are deferred. Provider profiles and interfaces are the stable swap boundary;
activating a paid provider must not change call, routing, appointment or recording logic.

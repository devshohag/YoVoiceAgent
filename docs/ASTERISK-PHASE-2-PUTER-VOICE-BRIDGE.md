# Asterisk Phase 2 - Puter development voice bridge

This phase provides a real multi-turn AI telephone loop without storing an OpenAI API key.
Because Puter.js is browser-only, an authenticated Angular page must remain open during calls.

## Flow

1. Caller dials `7000`; Asterisk answers and records one utterance.
2. Worker uploads WAV to MinIO and writes `AwaitingVoiceBridge`.
3. Angular `/voice-bridge` downloads the tenant-authorized recording.
4. Puter performs STT, conversation/tool selection, and TTS in the signed-in browser.
5. Approved tools execute through the existing authenticated .NET API.
6. Angular converts speech to mono PCM 8 kHz WAV and uploads it.
7. Worker downloads the response, Asterisk plays it, then records the next turn.

## Start

```powershell
docker compose up -d --build ccaas-api telephony-worker asterisk object-storage rabbitmq sqlserver redis
cd frontend
npm start
```

Open `http://localhost:4200/voice-bridge`, select the Appointment Agent, and click
**Start voice bridge**. Complete any Puter sign-in popup. Keep the page open, then call `7000`.

## Expected SQL events

```text
RecordingReady
AwaitingVoiceBridge
VoiceResponseSubmitted
VoiceResponsePlaybackStarted
RecordingStarted
```

The sequence repeats for each caller/AI turn. Appointment availability and booking continue to
use the same audited SQL-backed allowlisted tools used by the browser test playground.

## Constraints

- Development only: latency includes polling and browser-side AI requests.
- If the bridge page is closed, the call waits after the caller recording; the caller may hang up.
- Do not expose ARI or MinIO publicly.
- Production replaces the Angular bridge with server-side implementations of the existing STT,
  orchestration, and TTS interfaces. SIP, ARI, call sessions, recordings, tools, and tenant routing
  remain unchanged.

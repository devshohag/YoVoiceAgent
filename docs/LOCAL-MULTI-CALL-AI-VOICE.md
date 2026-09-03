# Free local multi-call AI voice

Development voice calls can run completely in Docker without Puter or an API key:

- Faster-Whisper `small` performs multilingual STT.
- The tenant's active Ollama LLM profile generates the reply.
- Piper generates `bn-BD` or `en-US` telephone WAV audio.
- `LocalAiVoiceProcessor` isolates history by `CallSessionId`/`AiConversationId`.
- `AiVoice:MaxConcurrentCalls` limits local machine load (default `2`).

## First start

```powershell
docker compose up -d --build --force-recreate `
  local-ai ollama-init ccaas-api asterisk telephony-worker
```

The first call is slower because Whisper and Piper voice files are downloaded into the
`local-ai-models` Docker volume. Ollama's `qwen3:4b` is pulled by `ollama-init` once and
retained in `ollama-data`.

Check readiness:

```powershell
Invoke-RestMethod http://localhost:8090/health
docker compose exec ollama ollama list
docker compose logs --since 5m local-ai telephony-worker ollama
```

## Switch between Local and Puter

In **Master Data & Settings → System settings**, use:

| Category | Key | Value |
| --- | --- | --- |
| `AiVoice` | `ProcessingMode` | `Local` or `Puter` |
| `AiVoice` | `MaxConcurrentCalls` | `2` |

Restart `telephony-worker` after changing concurrency. Local mode needs no browser tab.
Puter mode preserves the previous authenticated browser bridge.

## Test two simultaneous calls

Register caller extensions `1001` (`ccaas_demo_1001`) and `1003` (`ccaas_demo_1003`)
on two softphones/devices. Call `7000` from
both within a few seconds. Each call must create different `CallSessionId` and
`AiConversationId` values. Their transcripts and replies remain isolated even though the
same AI-agent profile and Ollama model are shared.

CPU-only development is intentionally bounded to two calls. Increase parallelism only when
RAM/VRAM and observed latency allow it. The local pipeline supports conversation and human
handoff; the Puter bridge remains available for the existing appointment tool-calling demo.

# Local Ollama human-handoff context

The live conversation can continue to use the Puter development bridge while the backend
creates the human-agent handoff summary through the tenant's active `Ollama`/`Llm` profile.
No paid API key is required.

## First-time model setup

```powershell
docker compose up -d ollama
docker compose exec ollama ollama pull qwen3:4b
docker compose up -d --build --force-recreate ccaas-api telephony-worker
```

The model is retained in the persistent `ollama-data` Docker volume.

## Runtime flow

1. Voice Bridge saves every customer and AI turn in SQL.
2. Before transfer it submits bounded conversation history to the API.
3. The API resolves the active tenant-scoped Ollama LLM profile from Master Data.
4. Ollama returns validated structured JSON: summary, intent, language, sentiment, collected
   details, unresolved items and a suggested opening.
5. The API stores `HumanHandoffContextPrepared`, updates the linked `AiConversation`, and the
   Agent Workspace shows the screen-pop while the original customer call rings/connects.

Ollama is attempted twice with a bounded timeout. If it is unavailable, a deterministic local
fallback is stored so the telephone transfer remains usable. Puter remains a development
fallback for the live STT/LLM/TTS bridge and can be replaced without changing this contract.

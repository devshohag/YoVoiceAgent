# AI Voice Agent series implementation guide

This .NET 9 edition contains a real first vertical slice for the 10-day public series.

## Implemented

- `ai` domain schema with agents, immutable configuration versions, conversations, turns,
  tool definitions/executions, and provider usage records.
- Tenant query filters and soft deletion through the existing `BaseEntity` convention.
- Provider-independent STT, summary, QA, sentiment, and transcript-redaction interfaces.
- Free development provider. Use a recording key beginning with `text:` to test the complete
  pipeline without an AI key or an audio file.
- PII redaction for email addresses, phone numbers, and likely payment-card numbers.
- Controlled/allowlisted tool execution with confirmation and an audit record.
- `CreateSupportTicket` development tool that demonstrates the boundary without allowing the
  model to query SQL directly.
- Human-handoff state and reason.
- REST APIs, Swagger discoverability, an Angular AI-agent configuration screen, and a unit test.

## API learning flow

1. Log in with the seeded development administrator and copy the JWT.
2. `POST /api/ai-agents` to create an agent.
3. `POST /api/ai-agents/process-recording` with the created agent id and this development value:

```json
{
  "aiAgentId": "REPLACE_WITH_AGENT_ID",
  "callSessionId": null,
  "customerId": null,
  "recordingObjectStorageKey": "text:Customer John called from +1 202-555-0198 and requested an appointment next Monday. Email john@example.com."
}
```

4. Inspect the redacted transcript, summary, sentiment, QA score and follow-up suggestion.
5. Call `POST /api/ai-conversations/{id}/tools`:

```json
{
  "toolName": "CreateSupportTicket",
  "argumentsJson": "{\"subject\":\"Confirm appointment availability\"}",
  "confirmed": true
}
```

6. Call `POST /api/ai-conversations/{id}/handoff?reason=Customer%20requested%20a%20person`.

## Windows setup

Install Visual Studio 2022 with the .NET 9 SDK and Node.js. From the solution directory:

```powershell
docker compose up -d
dotnet restore CCaaS.sln
dotnet build CCaaS.sln
dotnet test CCaaS.sln
./scripts/setup-ai-database.ps1
dotnet run --project src/CCaaS.Api
```

For Angular:

```powershell
cd frontend
npm ci
npm start
```

## What is intentionally not claimed as complete

This edition implements post-call intelligence and the safe business layer used by a future
live agent. Real-time audio streaming, STT partial results, TTS playback, barge-in, voice
activity detection, Asterisk External Media and warm transfer require a real PBX/provider and
are the next series milestone. The existing ARI listener still needs complete provider-tested
event-to-`CallSession` correlation before production use.

Do not publish the development AI provider as a real AI integration. In the article, explain
that it is a deterministic local adapter used to develop the architecture without paying for
an API key.

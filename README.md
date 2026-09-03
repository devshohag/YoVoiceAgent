# Omnichannel CCaaS — .NET 9 AI Voice Agent Series Edition

> This copy targets .NET 9 and adds a real, provider-independent AI post-call intelligence
> vertical slice. Start with [`docs/AI_VOICE_SERIES_GUIDE.md`](docs/AI_VOICE_SERIES_GUIDE.md).
> It includes a free local development voice pipeline (Faster-Whisper + Ollama + Piper),
> so live Asterisk calls work without an OpenAI key or an authenticated browser. See
> [`docs/LOCAL-MULTI-CALL-AI-VOICE.md`](docs/LOCAL-MULTI-CALL-AI-VOICE.md).

## New AI learning endpoints

- `GET/POST /api/ai-agents`
- `POST /api/ai-agents/process-recording`
- `GET /api/ai-conversations`
- `GET /api/ai-conversations/{id}`
- `POST /api/ai-conversations/{id}/tools`
- `POST /api/ai-conversations/{id}/handoff`

The Angular shell also includes an **AI Voice Agents** configuration screen.

This is a working scaffold for the Omnichannel CCaaS project proposal - a modular-monolith
ASP.NET Core backend, two background workers, an Angular frontend shell, and a full
docker-compose infrastructure stack (SQL Server, Redis, RabbitMQ, MinIO, Asterisk, Coturn,
Nginx, Prometheus, Grafana). It corresponds to the proposal's **Month 1 Foundation** roadmap
item plus a working vertical slice through Identity/Tenant/Organization/CRM/Conversation/Calls/
Campaign/Billing/Reporting/Channel, so you can start building each module's real business logic
instead of starting from an empty repo.

## What's real vs. what's a stub

| Area | Status |
|---|---|
| Solution structure (Domain/Application/Infrastructure/Api/Workers/Shared/Tests) | Real; run the included .NET 9 verification commands locally |
| Domain entities (14 original schemas plus the new `ai` schema) | Real |
| Multi-tenant query filters + soft delete (Section 7 security invariant) | Real |
| JWT auth (access + refresh token), PBKDF2 password hashing | Real |
| Tenant provisioning, Organization (branch/team/agent/presence), CRM, Conversation, Calls, Campaign, Billing, Reporting services + controllers | Real, basic business logic - extend as you go |
| Transactional outbox + RabbitMQ event bus | Real |
| Redis presence cache, MinIO object storage wrapper | Real |
| Hangfire wiring | Real wiring, job **bodies** are `NotImplementedException` TODOs |
| WhatsApp channel provider | Signature verification is real; the actual Meta Graph API send call is a TODO |
| Asterisk ARI client (originate) + RabbitMQ consumer | Real |
| Asterisk ARI event listener (inbound events -> CallService) | Real inbound call, recording/playback, lifecycle and human-handoff flow |
| Angular frontend | Real auth flow (login/guard/interceptor) + shell + unified inbox list wired to the API; SIP.js WebRTC phone and Customer 360 panel are marked TODO placeholders |
| Docker Compose stack | Real, `docker compose config` validated |
| Asterisk config (PJSIP/ARI/AMI, extensions 1001/1002) | Real, matches Milestone M2 |
| Local AI voice | Free Faster-Whisper STT + Ollama conversation + Piper Bangla/English TTS; per-call isolation and bounded concurrency |
| Puter voice bridge | Preserved as an optional browser development mode and appointment tool-calling demo |
| Unit tests | Real retry-policy, password-hasher and PII-redaction tests - not yet a full production suite |

## Important: this was built in a network-restricted sandbox

The environment that produced this template could **not reach NuGet** (`api.nuget.org` was
blocked), only npm/pip. That means:

- `CCaaS.Domain`, `CCaaS.Application`, and `CCaaS.Shared` have **zero external NuGet
  dependencies by design** and were `dotnet build`-verified successfully here.
- `CCaaS.Infrastructure`, `CCaaS.Api`, `CCaaS.Workers.Telephony`, `CCaaS.Workers.Channel`, and
  `CCaaS.Tests.Unit` all need EF Core / RabbitMQ.Client / StackExchange.Redis / Hangfire /
  Serilog / OpenTelemetry / health-check packages that could not be restored or
  compile-verified here.
- The **Angular frontend** *was* fully build-verified here (`ng build` succeeds in both dev and
  production configuration) because npm was reachable.
- The **docker-compose.yml** was validated with `docker compose config` (no Docker daemon was
  available in this sandbox to actually run the containers).

**First thing to do on your own machine:**

```bash
dotnet restore
dotnet build
```

If any package version pinned in `src/CCaaS.Infrastructure/CCaaS.Infrastructure.csproj` is
flagged as unavailable/deprecated by NuGet, bump it to the current version - the code was
written against current, stable APIs for each library, but exact version numbers could not be
confirmed against the live registry from here. Two specific spots are called out in code
comments as more likely to need a small adjustment:
- `ObservabilityExtensions.cs` - `AddRabbitMQ()` health check signature has changed across
  major versions of `AspNetCore.HealthChecks.Rabbitmq`.
- `Program.cs` - `MapPrometheusScrapingEndpoint()`'s namespace has moved between
  `OpenTelemetry.Exporter.Prometheus.AspNetCore` preview releases.

## Prerequisites

- .NET 9 SDK (requested learning target; see the runtime note below)
- Node.js 20+ and Angular CLI (`npm install -g @angular/cli`)
- Docker + Docker Compose

## Quick start

```bash
# 1. Bring up all infrastructure + the API + both workers
docker compose up --build

# 2. Once ccaas-api is healthy, create the initial EF Core migration (first time only -
#    this couldn't be generated in the sandbox that built this template, since
#    `dotnet ef` itself needs a restored Microsoft.EntityFrameworkCore.Design package)
cd src/CCaaS.Api
dotnet tool install --global dotnet-ef   # if you don't have it already
dotnet ef migrations add InitialCreate --project ../CCaaS.Infrastructure --startup-project .
dotnet ef database update --project ../CCaaS.Infrastructure --startup-project .

# 3. Frontend (separate terminal)
cd frontend
npm install
npm start   # ng serve, proxies to http://localhost:5000 per environment.development.ts
```

Useful URLs once everything is up:
- API Swagger UI: http://localhost:5000/swagger
- Hangfire dashboard: http://localhost:5000/hangfire
- Health checks: http://localhost:5000/health/live, http://localhost:5000/health/ready
- Prometheus metrics: http://localhost:5000/metrics
- RabbitMQ management: http://localhost:15672 (ccaas / ccaas_dev_password)
- MinIO console: http://localhost:9001 (ccaas_minio_admin / ccaas_minio_password)
- Grafana: http://localhost:3000 (admin / admin)
- Asterisk ARI: http://localhost:8088/ari
- Local speech health: http://localhost:8090/health

Local mode is the development default. On first startup Docker downloads the speech voices,
Whisper model and `qwen3:4b`; subsequent starts reuse Docker volumes. The browser Voice Bridge
page does not need to remain open in Local mode.

### Testing the telephony path (Milestone M2)

Register two SIP.js/WebRTC softphones (or any softphone that supports `ws://`) as extensions
`1001` / `1002` against `ws://localhost:8088/ws` with the passwords in
`infra/asterisk/conf/pjsip.conf`, then dial `1001` from `1002` (or vice versa) - this uses
plain `Dial()`, not the ARI app, specifically so you can validate the whole
WebRTC/PJSIP/Docker stack before your ARI event-handling code (the TODOs in
`AriEventListener.cs`) is finished. Dial `600` instead once you want to exercise the
`Stasis(ccaas)` application path that `CCaaS.Workers.Telephony` actually controls.

## Runtime decision: .NET 9 for this learning series

Every .NET project and Docker image in this edition targets .NET 9, as requested. The runtime
is suitable for learning and portfolio development, but it is out of support after
10 November 2026. Do not launch a new commercial deployment on an unsupported runtime; plan a
separate upgrade only when you are ready for that production milestone.

## Security checklist before this touches anything real

Everything below is a deliberately-weak dev default so you can `docker compose up` immediately
- treat this list as blocking before any non-local use:

- `Jwt:SigningKey` in every `appsettings.json` is a throw-away key generated for this template -
  regenerate with `openssl rand -base64 32` and keep it out of source control.
- SQL Server `sa` password, RabbitMQ/MinIO/Asterisk ARI/AMI credentials are all
  `ccaas_dev_password`-style placeholders in `docker-compose.yml` and `appsettings.json` - change
  every one of them.
- `HangfireDashboard` at `/hangfire` has no authorization filter configured - restrict it
  (Hangfire's `DashboardOptions.Authorization`) before deploying anywhere reachable.
- Asterisk's `[default]` outbound trunk in `pjsip.conf` is a placeholder - Section 8's
  "Regulatory/telecom boundary" note applies: use a real BTRC-licensed SIP/IPTSP provider, this
  software should not itself carry PSTN/mobile traffic.
- CORS is not configured yet on the API - add it scoped to your actual frontend origin(s).

## Where to go next (mapped to the proposal's 12-month roadmap)

This template covers the "Month 1 - Foundation" deliverable plus early groundwork for Months
2-10. Suggested build order, following the proposal's own critical-path reasoning (telephony
proven early, omnichannel after voice is stable):

1. **Finish the ARI event mapping** in `CCaaS.Workers.Telephony/AriEventListener.cs` (the
   biggest TODO in the repo) - this is what makes Milestone M2/M3 real.
2. Flesh out the SIP.js WebRTC phone panel in the Angular Agent Workspace
   (`frontend/src/app/features/agent-workspace`).
3. Build out the remaining channel providers (Messenger/Instagram/Email/SMS/Web Chat) the same
   way `WhatsAppChannelProvider` is structured, and complete the payload parsing TODO in
   `CCaaS.Workers.Channel/InboundChannelMessageConsumer.cs`.
4. Implement the Hangfire job bodies in `CCaaS.Infrastructure/BackgroundJobs/HangfireJobs.cs`
   (currently all throw `NotImplementedException` on purpose, so you notice they're unfinished).
5. Add role/permission seeding (the `Role`/`Permission`/`UserRole`/`RolePermission` tables exist;
   nothing seeds `PlatformAdmin`/`TenantAdmin`/`Supervisor`/`Agent` yet).
6. Add the AI provider implementations (`ISpeechToTextProvider`/`ISummaryProvider`/
   `IQaScoringProvider`) behind the abstractions already in `CCaaS.Application.Ai` - Section 16
   is explicit that the product must work with these disabled, which this template already does.

## Repo layout

```
CCaaS.sln
src/
  CCaaS.Api/                 ASP.NET Core host - controllers, Program.cs, SignalR hub
  CCaaS.Domain/               Entities, grouped by the proposal's schema names
  CCaaS.Application/          Service interfaces + business logic, zero external NuGet deps
  CCaaS.Infrastructure/       EF Core, RabbitMQ, Redis, MinIO, Hangfire, JWT, Serilog/OTel
  CCaaS.Workers.Telephony/    Asterisk ARI client + event listener + RabbitMQ consumer
  CCaaS.Workers.Channel/      Inbound channel webhook consumer
  CCaaS.Shared/                Cross-cutting interfaces/constants, zero external NuGet deps
tests/CCaaS.Tests.Unit/        xUnit tests against in-memory fakes (no DB/EF needed)
frontend/                      Angular 21 standalone app (auth, shell, 3 feature areas)
infra/
  asterisk/                    Dockerfile + PJSIP/ARI/AMI/dialplan config
  nginx/, prometheus/           Reverse proxy + metrics scrape config
docker-compose.yml
```

# Production readiness checklist

The application paths are production-shaped, but real carrier and paid speech services remain intentionally deferred.

## Required before deployment

1. Put SQL, JWT, RabbitMQ, MinIO, ARI and SIP extension credentials in a secret manager; never use the development defaults.
2. Terminate HTTPS at the ingress, enable Asterisk TLS/SRTP, restrict ARI/AMI to the private network, and allow only SIP/RTP provider ranges.
3. Replace the development SIP endpoints and DID routes with the licensed carrier configuration.
4. Configure the selected STT/TTS profiles and secret references in Master Data; keep Ollama as a controlled fallback if desired.
5. Use managed SQL/object storage backups, lifecycle policies, encryption and an explicit recording-consent/retention policy.
6. Run migrations as a release job before starting application replicas. Do not let every replica race to migrate.
7. Run API, worker and Angular tests, a two-party recorded handoff test, load tests, dependency/container scanning and tenant-isolation tests.
8. Alert on `/health/ready`, call failure/abandon rate, ARI disconnects, queue delay, STT failure, object-storage failure and outbox backlog.

## Implemented safeguards

- JWT role and tenant scoping on call/recording/workspace endpoints.
- Persistent ASP.NET data-protection keys.
- Global request limiting with HTTP 429 responses.
- Durable `TelephonyCommandProcessed` markers to prevent command replay after worker restart.
- Idempotent call termination across duplicate ARI lifecycle events.
- ARI reconnect with bounded exponential backoff and active-call context recovery.
- SQL transient-failure retry, object-storage upload retry and stale-call reconciliation.
- Dynamic Busy/NoAnswer/Unavailable fallback to the next available queue member.
- Agent presence automation: Available -> OnCall -> WrapUp -> Available after disposition.
- Hangfire dashboard exposed only in Development.
- Full-call recording stored outside SQL with tenant/call-scoped object keys.

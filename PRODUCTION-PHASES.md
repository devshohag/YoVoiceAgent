# Production remediation phases

| Phase | Scope and exit gate | Solo estimate |
|---|---|---:|
| 0 — Today | Server Compose, secret generation, private dependencies, SPA proxy, IP/NAT render, migration bootstrap, smoke test. One end-to-end test call passes. | 4–8 hours plus downloads |
| 1 — Call correctness | Full-call recording including human leg, definitive hangup lifecycle, stale-state cleanup, ARI event work off the read loop, retry/idempotency. Two callers cannot corrupt each other. | 3–5 days |
| 2 — Security | Disable anonymous tenant selection/register abuse, fail-closed tenant resolution, enforce RBAC, rotate all credentials, admin bootstrap/reset, rate limits, remove demo access. Cross-tenant tests pass. | 4–6 days |
| 3 — Agent/CRM | Atomic agent reservation, Available/OnCall/WrapUp recovery, disposition/notes, Customer 360, durable preliminary/final summary ordering, CRM FK and ownership fixes. | 4–6 days |
| 4 — AI reliability | Server-side media pipeline load tests, STT empty-result policy, Bangla/English benchmark, DTMF fallback, timeouts/circuit breakers, paid provider adapter. Two concurrent calls meet chosen latency SLO. | 5–8 days |
| 5 — Product operations | Frontend refresh-token handling, migrations in release workflow, health/metrics/alerts, encrypted off-host backup and restore drill, CI build/test/security scan. | 4–6 days |
| 6 — Telecom go-live | Licensed IPTSP/SIP trunk, DID routing, TLS/SRTP where provider supports it, provider IP allowlists, consent/retention policy, real-number UAT. | 3–7 days plus provider lead time |

Minimum credible controlled pilot: roughly 3–5 weeks solo after today's deployment. Today proves the stack and workflow; it does not close the audit findings.

## Test suites to add in order

1. Unit: tenant resolver fail-closed, handoff decision, summary ordering, lifecycle transitions, retry/idempotency keys.
2. Integration: SQL tenant isolation, appointment transaction, recording metadata, agent reservation race, CRM message FK/ownership.
3. Telephony: SIPp scenarios for answer, silence, concurrent calls, handoff answer/no-answer/busy, caller/agent hangup.
4. Security: anonymous registration, cross-tenant IDs, recording download authorization, role matrix, secret scan.
5. Load: 1 then 2 concurrent AI calls on this VPS; capture CPU, RAM, turn latency, error rate and RTP loss.

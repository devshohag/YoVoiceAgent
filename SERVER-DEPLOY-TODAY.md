# CCaaS v14 — deploy today (6 vCPU / 12 GB / 200 GB)

This is a controlled development deployment for 3–4 test agents and about 10–15 calls/hour. It is not a public PSTN production launch: there is no licensed SIP trunk, TLS/SRTP, hardened RBAC or completed security audit yet.

## 1. DNS/IP map

| Use | Today | Later production |
|---|---|---|
| Web UI/API | `http://REAL_IP` | `https://ccaas.yourdomain.com` |
| MicroSIP SIP server/domain | `REAL_IP`, TCP, port 5060 | TLS hostname, port 5061 |
| RTP | `REAL_IP`, UDP 10000–10100 | same public host, provider/firewall allowlist |
| ARI/AMI/SQL/Redis/MinIO/Ollama | Docker network only | Docker/private network only |

Do not put Docker's `172.x.x.x` address in MicroSIP. `SERVER_PUBLIC_IP` is the VPS public IPv4 shown by the hosting company.

## 2. Upload and bootstrap

Upload/extract this project into `/opt/ccaas`, then:

```bash
cd /opt/ccaas
sudo bash scripts/server/bootstrap-ubuntu.sh
sudo usermod -aG docker "$USER"
newgrp docker
bash scripts/server/generate-secrets.sh
nano .env.server
```

In `.env.server`, replace `SERVER_PUBLIC_IP=203.0.113.10` with the VPS public IPv4. The generator creates the other secrets. Keep the file private.

## 3. Validate and deploy

```bash
cd /opt/ccaas
bash scripts/server/preflight.sh
bash scripts/server/deploy.sh
```

First boot downloads/builds images and AI models, so it can take 10–30 minutes depending on network and CPU. Follow it with:

```bash
docker compose --env-file .env.server -f docker-compose.server.yml ps
docker compose --env-file .env.server -f docker-compose.server.yml logs -f --tail 100 ccaas-api telephony-worker local-ai ollama asterisk
```

Open `http://REAL_IP`. First-login demo values are:

- Tenant: `3FA85F64-5717-4562-B3FC-2C963F66AFA6`
- Email: `admin@ccaas.local`
- Temporary password: `Admin@12345`

Immediately after first successful login, edit `.env.server`:

```dotenv
BOOTSTRAP_MIGRATE_DATABASE=false
BOOTSTRAP_SEED_DEMO_DATA=false
```

Then run `bash scripts/server/deploy.sh`. The fixed demo password is unacceptable for any internet-facing real customer use; password reset/rotation is Phase 2.

## 4. MicroSIP test without paid SIP trunk

You do not need a paid SIP trunk for extension-to-extension or AI tests. Configure separate MicroSIP instances/devices:

| Device | Username | Password | SIP server/domain | Transport |
|---|---|---|---|---|
| Caller A | 1001 | `SIP_1001_PASSWORD` | `REAL_IP` | TCP |
| Human agent | 1002 | `SIP_1002_PASSWORD` | `REAL_IP` | TCP |
| Caller B | 1003 | `SIP_1003_PASSWORD` | `REAL_IP` | TCP |

Dial `7000` from 1001 for AI; request a human to transfer to 1002. For a concurrent test, call 7000 from 1001 and 1003. The package starts at concurrency 1 intentionally; the second call should be controlled/rejected, not corrupt the first. After the single-call test is stable, set `AI_VOICE_MAX_CONCURRENT_CALLS=2` and `LOCAL_AI_SPEECH_PARALLEL=2`, redeploy, and repeat while watching `docker stats`. On a CPU-only 12 GB VPS, two simultaneous local-AI turns may be slow.

## 5. Acceptance checklist

```bash
bash scripts/server/smoke-test.sh
docker compose --env-file .env.server -f docker-compose.server.yml exec asterisk asterisk -rx "pjsip show contacts"
docker stats --no-stream
```

- UI loads and login works.
- 1001/1002 register; 1003 is optional.
- 1001→7000 receives welcome, records speech, gets an AI reply.
- “human agent” request rings 1002 with context summary.
- Hangup ends the call and call state no longer says connected.
- Recording/transcript and final summary remain retrievable.
- No ARI disconnect loop, unhandled exception, OOM kill, or codec translation error.

## 6. Rollback and backup

Before changes run `bash scripts/server/backup.sh` and copy the result off-server. To inspect a failed release, do not delete volumes. Keep the previous project directory/image tag and start it with the same `.env.server` and named volumes.

## Known day-one limits

- HTTP SIP/web traffic is not private. Use only test data today.
- Port 5060/RTP is open publicly for development; restrict to tester/provider CIDRs as soon as possible.
- Local CPU AI quality/latency is development-grade.
- The review's security, lifecycle, recording, appointment, CRM, retry/idempotency and load-test blockers are not all solved by deployment packaging.
- The 10000–10100 RTP range is adequate for this tiny test, not a large call center.

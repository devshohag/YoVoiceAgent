# Asterisk Phase 1 - local inbound call lifecycle

This milestone intentionally stops before server-side AI. It proves that SIP, RTP, ARI,
the .NET worker, SQL Server, Asterisk recording, and MinIO storage work together.

## Configure the development tenant

The worker must attach calls to the same tenant used by the logged-in application. Create a
`.env` file beside `docker-compose.yml`:

```text
CCAAS_DEMO_TENANT_ID=3fa85f64-5717-4562-b3fc-2c963f66afa6
```

The canonical development tenant is `3FA85F64-5717-4562-B3FC-2C963F66AFA6`.

## Start the stack

```powershell
docker compose up -d --build asterisk telephony-worker object-storage sqlserver rabbitmq redis
docker compose logs telephony-worker --tail 100
```

Expected worker log:

```text
Connected to Asterisk ARI application ccaas
```

## Configure MicroSIP on Windows

Create an account:

- SIP server: `127.0.0.1`
- SIP proxy: empty
- Username/login: `1001`
- Password: `ccaas_demo_1001`
- Transport: UDP
- Media encryption: disabled
- Allowed codec: PCMU/ulaw

The second available test account is `1002` / `ccaas_demo_1002`.

Confirm registration:

```powershell
docker compose exec asterisk asterisk -rx "pjsip show contacts"
```

## Make the call

Dial `7000` from extension `1001`.

Expected behavior:

1. Asterisk sends `StasisStart` to the worker.
2. The worker creates `calls.CallSession`, answers, and plays the welcome sound.
3. A beep starts a maximum 30-second WAV recording.
4. Three seconds of silence (or `#`) finishes the recording.
5. The worker uploads the WAV to MinIO bucket `call-recordings`.
6. SQL recording metadata is created, a goodbye sound plays, and the call hangs up.

## Verify SQL

```sql
SELECT TOP 10 Id, TenantId, ExternalCallId, FromNumber, ToNumber,
       StartedAt, AnsweredAt, EndedAt, TalkDurationSeconds, Status, HangupCause, RecordingId
FROM calls.CallSession
ORDER BY StartedAt DESC;

SELECT TOP 30 CallSessionId, EventType, OccurredAt
FROM calls.CallEvent
ORDER BY OccurredAt DESC;

SELECT TOP 10 CallSessionId, ObjectStorageKey, DurationSeconds,
       SizeBytes, RetainUntil, CreatedAt
FROM calls.Recording
ORDER BY CreatedAt DESC;
```

Expected event progression includes `StasisStart`, `Answered`, `WelcomePlaybackStarted`,
`RecordingStarted`, `RecordingReady`, and `StasisEnd` or `ChannelDestroyed`.

## Troubleshooting

```powershell
docker compose logs asterisk --tail 200
docker compose logs telephony-worker --tail 200
docker compose exec asterisk asterisk -rx "pjsip show endpoints"
docker compose exec asterisk asterisk -rx "ari show apps"
```

If MicroSIP registers but audio is one-way, confirm Windows Firewall allows Docker Desktop,
UDP 5060, and UDP 10000-10100. A production deployment will additionally configure the
public/private RTP addresses, TLS/SRTP, provider IP allowlists, and a licensed SIP trunk.

## Next phase

`RecordingReady` becomes the handoff point for speech-to-text, the existing approved AI tool
pipeline, text-to-speech, and ARI playback. Puter.js remains a browser development provider;
real phone calls will use a server-side provider behind the existing interfaces.

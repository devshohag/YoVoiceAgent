# Phase 2: AI to human handoff (development)

This phase adds an audited same-call transfer from the browser-powered AI voice bridge to
the development human-agent extension `1002`. Existing booking, voucher, call recording,
supervisor and appointment-master behavior remains unchanged.

## Run

From PowerShell in the extracted project root:

```powershell
.\scripts\configure-local-asterisk.ps1
docker compose up -d --build --force-recreate asterisk telephony-worker ccaas-api
```

Keep the Angular Voice Bridge open and started. Register the caller softphone as `1001`.
Register a second MicroSIP/softphone account as `1002` using the same SIP server, password
and TCP settings as the local Asterisk configuration.

Both development endpoints are deliberately locked to `ulaw` (PCMU). Do not enable Opus
for Zoiper extension `1002`; the development Asterisk image has no Opus-to-ulaw translation
path and will otherwise drop the call immediately after the human answers.

## Test

1. Open **Agent Workspace** in one authenticated browser tab.
2. Open and start **Voice Bridge** in another authenticated browser tab.
3. Call `7000` from extension `1001`.
4. Say: "I want to speak with a human agent."
5. The AI says the hold sentence, then extension `1002` rings.
6. Answer `1002`; the customer and human continue on the original live call.
7. Hang up. Agent Workspace and the call event audit show the handoff lifecycle.

Expected events include `HumanHandoffStarted`, `HumanHandoffDialplanEntered`, and
`HumanHandoffCompleted`.

## Scope

This is a development transfer foundation. Production queue selection, agent presence,
skills, SLA/overflow routing, secure WebRTC, consultation/conference controls and PSTN SIP
trunks are intentionally future phases.

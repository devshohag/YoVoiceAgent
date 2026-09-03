# Puter AI Agent Test Console

This development-only screen proves that a saved tenant AI agent can control a real model response without an OpenAI API key.

## Run

1. Start the API and infrastructure with Docker Compose.
2. Run the Angular development server from `frontend` with `npm start`.
3. Log in to CCaaS and create an active AI agent.
4. Open **AI Agent Test** or select **Test this agent** from an agent card.
5. Send a customer message. Puter may open a sign-in window on first use.
6. Use **Speak reply** for TTS or **Upload audio** for file transcription.

With both automation options enabled, uploading an audio file runs the complete development pipeline: transcription, AI generation, speech synthesis, and attempted autoplay. If the browser blocks autoplay, use the rendered audio player's Play button.

## Boundary

Puter runs in the browser for learning and development. It never receives database credentials. Production telephony will use the existing provider interfaces through a server-side implementation. Business actions must continue through allowlisted .NET endpoints with tenant, permission, input, and audit checks.

## Next slice

Connect Asterisk ARI events to a CallSession, map a DID/route to an AI agent, capture a recording, then pass the recording through the server-side speech provider boundary. Real-time bidirectional audio comes after the recording pipeline is stable.

## Controlled development tools

The playground starts an audited `AiConversation`, persists customer/AI/system turns, and exposes only three allowlisted tools: `check_appointment_availability`, `book_appointment`, and `request_human_handoff`. Tool calls travel through the authenticated .NET API; Puter never receives database access. Booking requires explicit customer confirmation. SQL Server enforces one booking per tenant availability slot, and a retry by the same contact returns the existing booking.

# Voice Timeout and Spoken Fallback

STT and TTS use `AiVoice:SpeechTimeoutMilliseconds`, defaulting to 2500 ms. A failed turn records
a `VoiceSpokenFallbackRequested` event with a short caller-facing prompt. On the second
consecutive failure it also records `VoiceDtmfFallbackRequested`, allowing the telephony layer to
offer DTMF or route to a human. The existing third-failure hangup reconciliation remains active.
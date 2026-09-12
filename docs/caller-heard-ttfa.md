# Caller-Heard TTFA Instrumentation

The worker records a `CallerSpeechEnded` event when an Asterisk caller recording finishes. The
`AriEventListener.RecordCallerHeardFirstAudioAsync` hook accepts the timestamp supplied by the
audio transport when the first RTP frame is actually heard by the caller and writes a
`CallStageTiming` row with stage `time_to_first_audio`.

The ARI playback acknowledgement is deliberately not used as TTFA: it only confirms that
Asterisk accepted the playback request. The AudioSocket/realtime gateway must call the hook with
the observed first-frame timestamp before the Phase 2.5 latency gate can be signed.

The timing row includes UTC start/end timestamps, measured duration, concurrency, provider,
pipeline version, and a measurement marker for downstream audits.
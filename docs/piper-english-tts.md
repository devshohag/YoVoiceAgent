# Piper English TTS

The local speech service now uses Piper English (`en-US`) by default. Piper keeps loaded voices
in an in-process cache, and the service warms the default voice during startup when
`PIPER_WARMUP=true`, removing the cold first request from the call path.

Generated audio is resampled to the configured telephony rate, `8000` Hz by default, mono,
16-bit PCM WAV. Bengali remains available by sending `language: "bn-BD"` in the speech request.

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `PIPER_DEFAULT_LANGUAGE` | `en-US` | Voice used when a request omits language |
| `PIPER_OUTPUT_SAMPLE_RATE` | `8000` | Telephony output sample rate |
| `PIPER_WARMUP` | `true` | Load the default Piper voice during service startup |

The `/health` response exposes the selected defaults and warmup setting. Both English and
Bengali voices remain cached independently when requested.
import asyncio
import json
import os
import struct
import subprocess
import tempfile
import wave
from contextlib import asynccontextmanager
from functools import lru_cache
from pathlib import Path

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import FileResponse
from faster_whisper import WhisperModel
from huggingface_hub import hf_hub_download
from piper import PiperVoice
from pydantic import BaseModel
from starlette.background import BackgroundTask

app = FastAPI(title="CCaaS Local Speech", version="1.0")
model_name = os.getenv("WHISPER_MODEL", "small")
device = os.getenv("WHISPER_DEVICE", "cpu")
compute_type = os.getenv("WHISPER_COMPUTE_TYPE", "int8")
parallelism = max(1, int(os.getenv("SPEECH_NUM_PARALLEL", "2")))
speech_slots = asyncio.Semaphore(parallelism)
vad_threshold = max(0, int(os.getenv("VAD_SILENCE_THRESHOLD", "500")))
vad_padding_ms = max(0, int(os.getenv("VAD_PADDING_MS", "80")))
piper_default_language = os.getenv("PIPER_DEFAULT_LANGUAGE", "en-US")
piper_output_sample_rate = max(8000, int(os.getenv("PIPER_OUTPUT_SAMPLE_RATE", "8000")))
piper_warmup = os.getenv("PIPER_WARMUP", "true").lower() in ("1", "true", "yes")
prompt_cache_dir = Path(os.getenv("PROMPT_CACHE_DIR", "/models/prompt-cache"))
prompt_manifest_path = Path(os.getenv("PROMPT_MANIFEST", "/models/prompt-cache/manifest.json"))


@lru_cache(maxsize=1)
def whisper_model() -> WhisperModel:
    return WhisperModel(model_name, device=device, compute_type=compute_type,
                        download_root="/models/whisper")


@asynccontextmanager
async def lifespan(_app: FastAPI):
    if piper_warmup:
        await asyncio.to_thread(load_voice, piper_default_language)
    yield


app.router.lifespan_context = lifespan


def trim_wav_silence(source_path: str, target_path: str) -> str:
    """Trim quiet PCM WAV edges while retaining a small amount of speech padding."""
    with wave.open(source_path, "rb") as source:
        channels = source.getnchannels()
        sample_width = source.getsampwidth()
        frame_rate = source.getframerate()
        frame_count = source.getnframes()
        frames = source.readframes(frame_count)

    if sample_width != 2 or frame_rate <= 0 or channels <= 0:
        return source_path

    frame_size = channels * sample_width
    window_frames = max(1, frame_rate // 50)
    active_windows = []
    for offset in range(0, frame_count, window_frames):
        window = frames[offset * frame_size:(offset + window_frames) * frame_size]
        samples = struct.unpack(f"<{len(window) // 2}h", window)
        peak = max((abs(sample) for sample in samples), default=0)
        active_windows.append(peak > vad_threshold)

    active = [index for index, is_active in enumerate(active_windows) if is_active]
    if not active:
        return source_path

    padding_frames = frame_rate * vad_padding_ms // 1000
    start = max(0, active[0] * window_frames - padding_frames)
    end = min(frame_count, (active[-1] + 1) * window_frames + padding_frames)
    if start == 0 and end == frame_count:
        return source_path

    with wave.open(target_path, "wb") as target:
        target.setnchannels(channels)
        target.setsampwidth(sample_width)
        target.setframerate(frame_rate)
        target.writeframes(frames[start * frame_size:end * frame_size])
    return target_path


VOICE_FILES = {
    "bn-BD": "bn/bn_BD/google/medium/bn_BD-google-medium.onnx",
    "en-US": "en/en_US/lessac/medium/en_US-lessac-medium.onnx",
}


@lru_cache(maxsize=4)
def load_voice(language: str) -> PiperVoice:
    selected = "bn-BD" if language.lower().startswith("bn") else "en-US"
    filename = VOICE_FILES[selected]
    model_path = hf_hub_download("rhasspy/piper-voices", filename=filename,
                                 local_dir="/models/piper")
    config_path = hf_hub_download("rhasspy/piper-voices", filename=filename + ".json",
                                  local_dir="/models/piper")
    return PiperVoice.load(model_path, config_path=config_path)


@app.get("/health")
async def health():
    return {"status": "healthy", "whisperModel": model_name,
            "device": device, "parallelism": parallelism,
            "piperDefaultLanguage": piper_default_language,
            "piperOutputSampleRate": piper_output_sample_rate,
            "piperWarmed": piper_warmup}


@app.post("/v1/audio/transcriptions")
async def transcribe(file: UploadFile = File(...), language: str = Form("auto")):
    suffix = Path(file.filename or "caller.wav").suffix or ".wav"
    async with speech_slots:
        with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as source:
            source.write(await file.read())
            source_path = source.name
        trimmed_path = source_path + ".trimmed.wav"
        try:
            input_path = trim_wav_silence(source_path, trimmed_path)

            def run():
                segments, info = whisper_model().transcribe(
                    input_path,
                    language=None if language in ("", "auto") else language,
                    vad_filter=True,
                    beam_size=3,
                    condition_on_previous_text=False,
                )
                text = " ".join(segment.text.strip() for segment in segments).strip()
                return text, info.language, info.language_probability

            text, detected, confidence = await asyncio.to_thread(run)
            return {"text": text, "language": detected, "confidence": confidence,
                    "model": model_name}
        finally:
            Path(source_path).unlink(missing_ok=True)
            Path(trimmed_path).unlink(missing_ok=True)


class SpeechRequest(BaseModel):
    input: str
    language: str | None = None
    voice: str | None = None


def prompt_key_is_safe(key: str) -> bool:
    return bool(key) and all(character.isalnum() or character in "-_" for character in key)


@app.get("/v1/audio/prompts/{key}")
async def cached_prompt(key: str):
    if not prompt_key_is_safe(key):
        raise HTTPException(status_code=400, detail="Invalid prompt key")
    prompt_path = prompt_cache_dir / f"{key}.wav"
    if not prompt_path.is_file():
        raise HTTPException(status_code=404, detail="Prompt audio is not cached")
    return FileResponse(prompt_path, media_type="audio/wav", filename=f"{key}.wav")


@app.get("/v1/audio/prompts")
async def cached_prompts():
    if not prompt_manifest_path.is_file():
        return {"prompts": []}
    return {"prompts": json.loads(prompt_manifest_path.read_text(encoding="utf-8"))}


@app.post("/v1/audio/speech")
async def speech(request: SpeechRequest):
    if not request.input.strip():
        raise HTTPException(status_code=400, detail="Speech text is required")
    async with speech_slots:
        work = tempfile.mkdtemp(prefix="ccaas-tts-")
        native_path = str(Path(work) / "native.wav")
        phone_path = str(Path(work) / "telephone.wav")
        try:
            def synthesize():
                language = request.language or piper_default_language
                voice = load_voice(language)
                with wave.open(native_path, "wb") as output:
                    voice.synthesize_wav(request.input.strip(), output)
                subprocess.run([
                    "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                    "-i", native_path, "-ar", str(piper_output_sample_rate), "-ac", "1",
                    "-c:a", "pcm_s16le", phone_path,
                ], check=True)

            await asyncio.to_thread(synthesize)
            return FileResponse(phone_path, media_type="audio/wav",
                                filename="response.wav",
                                background=BackgroundTask(CleanupFiles(work)))
        except Exception:
            CleanupFiles(work)()
            raise


class CleanupFiles:
    def __init__(self, directory: str):
        self.directory = Path(directory)

    def __call__(self):
        for child in self.directory.glob("*"):
            child.unlink(missing_ok=True)
        self.directory.rmdir()

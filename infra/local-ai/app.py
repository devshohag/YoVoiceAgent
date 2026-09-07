import asyncio
import os
import subprocess
import tempfile
import wave
from functools import lru_cache
from pathlib import Path

from fastapi import FastAPI, File, Form, HTTPException, UploadFile, Request
from uuid import uuid4
from voice_timing import context, stage
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
active_speech_requests = 0


@lru_cache(maxsize=1)
def whisper_model() -> WhisperModel:
    return WhisperModel(model_name, device=device, compute_type=compute_type,
                        download_root="/models/whisper")


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


@app.middleware("http")
async def timing_context(request: Request, call_next):
    global active_speech_requests
    measured = request.url.path in ('/v1/audio/transcriptions', '/v1/audio/speech')
    if measured:
        active_speech_requests += 1
    token = context.set({"call_id": request.headers.get("x-call-id", "benchmark"),
        "turn_id": request.headers.get("x-turn-id", str(uuid4())), "turn_no": None,
        "concurrency": active_speech_requests, "request_id": str(uuid4())})
    try:
        return await call_next(request)
    finally:
        context.reset(token)
        if measured:
            active_speech_requests -= 1


@app.get("/health")
async def health():
    return {"status": "healthy", "whisperModel": model_name,
            "device": device, "parallelism": parallelism}


@app.post("/v1/audio/transcriptions")
async def transcribe(file: UploadFile = File(...), language: str = Form("auto")):
    suffix = Path(file.filename or "caller.wav").suffix or ".wav"
    with stage("speech_queue"):
        await speech_slots.acquire()
    try:
        with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as source:
            source.write(await file.read())
            source_path = source.name
        try:
            def run():
                with stage("stt_model_load"):
                    model = whisper_model()
                with stage("stt_inference_batch"):
                    segments, info = model.transcribe(
                        source_path,
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

    finally:
        speech_slots.release()


class SpeechRequest(BaseModel):
    input: str
    language: str = "bn-BD"
    voice: str | None = None


@app.post("/v1/audio/speech")
async def speech(request: SpeechRequest):
    if not request.input.strip():
        raise HTTPException(status_code=400, detail="Speech text is required")
    with stage("speech_queue"):
        await speech_slots.acquire()
    try:
        work = tempfile.mkdtemp(prefix="ccaas-tts-")
        native_path = str(Path(work) / "native.wav")
        phone_path = str(Path(work) / "telephone.wav")
        try:
            def synthesize():
                with stage("tts_model_load"):
                    voice = load_voice(request.language)
                with stage("tts_synthesis_batch"):
                    with wave.open(native_path, "wb") as output:
                        voice.synthesize_wav(request.input.strip(), output)
                with stage("tts_resample"):
                    subprocess.run([
                        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                        "-i", native_path, "-ar", "8000", "-ac", "1",
                        "-c:a", "pcm_s16le", phone_path,
                    ], check=True)

            await asyncio.to_thread(synthesize)
            return FileResponse(phone_path, media_type="audio/wav",
                                filename="response.wav",
                                background=BackgroundTask(CleanupFiles(work)))
        except Exception:
            CleanupFiles(work)()
            raise

    finally:
        speech_slots.release()


class CleanupFiles:
    def __init__(self, directory: str):
        self.directory = Path(directory)

    def __call__(self):
        for child in self.directory.glob("*"):
            child.unlink(missing_ok=True)
        self.directory.rmdir()

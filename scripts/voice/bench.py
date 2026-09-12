#!/usr/bin/env python3
"""Benchmark Faster-Whisper models on one WAV at concurrency 1 and 3."""

from __future__ import annotations

import argparse
import concurrent.futures
import json
import statistics
import time
import wave
from pathlib import Path
from typing import Any


def audio_duration(path: Path) -> float:
    with wave.open(str(path), "rb") as source:
        return source.getnframes() / source.getframerate()


def transcribe(model: Any, path: Path, language: str | None) -> dict[str, Any]:
    started = time.perf_counter()
    segments, info = model.transcribe(
        str(path),
        language=language,
        vad_filter=True,
        beam_size=3,
        condition_on_previous_text=False,
    )
    text = " ".join(segment.text.strip() for segment in segments).strip()
    elapsed = time.perf_counter() - started
    return {
        "elapsed_seconds": elapsed,
        "rtf": elapsed / AUDIO_SECONDS,
        "language": info.language,
        "language_probability": info.language_probability,
        "transcript": text,
    }


def run_model(model_name: str, path: Path, language: str | None,
              compute_type: str, device: str, concurrency: int) -> dict[str, Any]:
    from faster_whisper import WhisperModel

    print(f"Loading {model_name} ({device}, {compute_type})...", flush=True)
    model = WhisperModel(model_name, device=device, compute_type=compute_type)
    started = time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as pool:
        futures = [pool.submit(transcribe, model, path, language) for _ in range(concurrency)]
        samples = [future.result() for future in futures]
    wall_seconds = time.perf_counter() - started
    elapsed = [sample["elapsed_seconds"] for sample in samples]
    return {
        "model": model_name,
        "concurrency": concurrency,
        "wall_seconds": wall_seconds,
        "p50_seconds": statistics.median(elapsed),
        "p95_seconds": percentile(elapsed, 95),
        "p50_rtf": statistics.median(sample["rtf"] for sample in samples),
        "p95_rtf": percentile([sample["rtf"] for sample in samples], 95),
        "language": samples[0]["language"],
        "transcript": samples[0]["transcript"],
        "samples": samples,
    }


def percentile(values: list[float], percentile_value: int) -> float:
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    position = (len(ordered) - 1) * percentile_value / 100
    lower = int(position)
    upper = min(lower + 1, len(ordered) - 1)
    fraction = position - lower
    return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("audio", type=Path, help="8 kHz mono WAV used for every run")
    parser.add_argument("--models", nargs="+", default=["tiny.en", "base.en", "small.en"])
    parser.add_argument("--language", default="en", help="Whisper language code; use auto to detect")
    parser.add_argument("--compute-type", default="int8")
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--output", type=Path, help="Optional JSON result file")
    return parser.parse_args()


def main() -> None:
    global AUDIO_SECONDS
    args = parse_args()
    if not args.audio.is_file():
        raise SystemExit(f"Audio file was not found: {args.audio}")
    if args.audio.suffix.lower() != ".wav":
        raise SystemExit("The benchmark requires a WAV file so its duration is measured consistently.")

    with wave.open(str(args.audio), "rb") as source:
        if source.getnchannels() != 1 or source.getframerate() != 8000:
            raise SystemExit("The benchmark input must be an 8 kHz mono WAV file.")
    AUDIO_SECONDS = audio_duration(args.audio)
    if AUDIO_SECONDS <= 0:
        raise SystemExit("The benchmark input must contain audio.")

    language = None if args.language.lower() == "auto" else args.language
    results = []
    for model_name in args.models:
        for concurrency in (1, 3):
            results.append(run_model(model_name, args.audio, language,
                                     args.compute_type, args.device, concurrency))

    report = {
        "audio": str(args.audio),
        "audio_seconds": AUDIO_SECONDS,
        "device": args.device,
        "compute_type": args.compute_type,
        "results": results,
    }
    print("\nmodel       concurrency  p50 sec  p95 sec  p50 RTF  p95 RTF  transcript")
    print("----------- -----------  -------  -------  -------  -------  ----------")
    for result in results:
        print(f"{result['model']:<11} {result['concurrency']:>11}  "
              f"{result['p50_seconds']:>7.2f}  {result['p95_seconds']:>7.2f}  "
              f"{result['p50_rtf']:>7.2f}  {result['p95_rtf']:>7.2f}  "
              f"{result['transcript']}")
    if args.output:
        args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    AUDIO_SECONDS = 0.0
    main()
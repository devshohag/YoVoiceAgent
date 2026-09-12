#!/usr/bin/env python3
"""Replay labelled golden calls and report STT/business metrics separately."""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


def words(text: str) -> list[str]:
    return re.findall(r"[\w']+", text.casefold())


def distance(left: list[str], right: list[str]) -> int:
    row = list(range(len(right) + 1))
    for left_index, left_value in enumerate(left, 1):
        next_row = [left_index]
        for right_index, right_value in enumerate(right, 1):
            next_row.append(min(
                next_row[-1] + 1,
                row[right_index] + 1,
                row[right_index - 1] + (left_value != right_value),
            ))
        row = next_row
    return row[-1]


def error_rate(expected: str, actual: str, tokenise: bool = True) -> float:
    left = words(expected) if tokenise else list(expected.casefold())
    right = words(actual) if tokenise else list(actual.casefold())
    return distance(left, right) / max(1, len(left))


def multipart(path: Path, boundary: str) -> bytes:
    content = path.read_bytes()
    return (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{path.name}\"\r\n"
            "Content-Type: audio/wav\r\n\r\n").encode() + content + f"\r\n--{boundary}--\r\n".encode()


def transcribe(url: str, path: Path) -> str:
    boundary = "GoldenCallBoundary"
    request = urllib.request.Request(
        f"{url.rstrip('/')}/v1/audio/transcriptions",
        data=multipart(path, boundary),
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
    )
    with urllib.request.urlopen(request) as response:
        return json.loads(response.read()) .get("text", "")


def load_predictions(path: Path | None) -> dict[str, dict[str, Any]]:
    if path is None:
        return {}
    value = json.loads(path.read_text(encoding="utf-8"))
    return {item["id"]: item for item in value}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--stt-url", default="http://localhost:8090")
    parser.add_argument("--predictions", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    cases = json.loads(args.manifest.read_text(encoding="utf-8"))
    if len(cases) != 20:
        raise SystemExit(f"Golden set must contain exactly 20 calls; found {len(cases)}")
    incomplete = [case["id"] for case in cases if not case.get("transcript") or not case.get("intent")]
    if incomplete:
        raise SystemExit("Manifest has incomplete labels: " + ", ".join(incomplete))

    predictions = load_predictions(args.predictions)
    results = []
    for case in cases:
        audio = args.manifest.parent / case["audio"]
        if not audio.is_file():
            raise SystemExit(f"Audio file was not found for {case['id']}: {audio}")
        try:
            actual_transcript = transcribe(args.stt_url, audio)
        except urllib.error.URLError as error:
            raise SystemExit(f"Could not reach STT provider at {args.stt_url}: {error}") from error
        prediction = predictions.get(case["id"], {})
        predicted_entities = prediction.get("entities", {})
        expected_entities = case.get("entities", {})
        entity_total = len(expected_entities)
        entity_correct = sum(predicted_entities.get(key) == value for key, value in expected_entities.items())
        results.append({
            "id": case["id"],
            "wer": error_rate(case["transcript"], actual_transcript),
            "cer": error_rate(case["transcript"], actual_transcript, tokenise=False),
            "entity_accuracy": entity_correct / max(1, entity_total),
            "intent_correct": prediction.get("intent") == case["intent"],
            "task_success": prediction.get("bookingOutcome") == case.get("expectedBooking"),
            "transcript": actual_transcript,
        })

    report = {
        "calls": len(results),
        "wer": sum(item["wer"] for item in results) / len(results),
        "cer": sum(item["cer"] for item in results) / len(results),
        "entity_accuracy": sum(item["entity_accuracy"] for item in results) / len(results),
        "intent_accuracy": sum(item["intent_correct"] for item in results) / len(results),
        "task_success": sum(item["task_success"] for item in results) / len(results),
        "results": results,
    }
    output = json.dumps(report, indent=2)
    print(output)
    if args.output:
        args.output.write_text(output, encoding="utf-8")


if __name__ == "__main__":
    main()
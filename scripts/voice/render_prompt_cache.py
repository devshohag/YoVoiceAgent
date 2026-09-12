#!/usr/bin/env python3
"""Render a JSON prompt manifest through local-ai and write a reusable WAV cache."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from urllib.request import Request, urlopen


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--base-url", default="http://localhost:8090")
    args = parser.parse_args()
    prompts = json.loads(args.manifest.read_text(encoding="utf-8"))
    args.output.mkdir(parents=True, exist_ok=True)
    rendered = []
    for prompt in prompts:
        key = prompt["key"]
        request = Request(
            f"{args.base_url.rstrip('/')}/v1/audio/speech",
            data=json.dumps({"input": prompt["text"], "language": prompt.get("language", "en-US")}).encode(),
            headers={"Content-Type": "application/json"},
        )
        with urlopen(request) as response:
            (args.output / f"{key}.wav").write_bytes(response.read())
        rendered.append(prompt)
    (args.output / "manifest.json").write_text(json.dumps(rendered, indent=2), encoding="utf-8")
    print(f"Rendered {len(rendered)} prompts to {args.output}")


if __name__ == "__main__":
    main()
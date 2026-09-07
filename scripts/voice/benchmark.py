#!/usr/bin/env python3
"""Stdlib only. Human 8kHz PCM WAV input; never fabricates ground truth."""
import argparse
import concurrent.futures
import json
import math
import re
import time
import unicodedata
import urllib.request
import uuid
import wave
from pathlib import Path


def tokens(text):
    text = unicodedata.normalize('NFC', text).translate(str.maketrans('০১২৩৪৫৬৭৮৯', '0123456789'))
    return re.sub(r'[^\w\s\u0980-\u09ff]', ' ', text.lower()).split()


def distance(a, b):
    row = list(range(len(b)+1))
    for i, x in enumerate(a, 1):
        nxt = [i]
        for j, y in enumerate(b, 1):
            nxt.append(min(nxt[-1]+1, row[j]+1, row[j-1]+(x != y)))
        row = nxt
    return row[-1]


def percentile(values, p):
    if not values:
        return None
    return sorted(values)[max(0, math.ceil(len(values)*p)-1)]


def validate(manifest):
    rows = json.loads(manifest.read_text(encoding='utf-8'))
    if not isinstance(rows, list) or not rows:
        raise ValueError('Manifest must contain human-labeled cases')
    seen = set()
    for r in rows:
        for field in ('id', 'audio', 'transcript', 'intent', 'entities', 'recorded_at', 'timezone', 'human_recorded'):
            if field not in r:
                raise ValueError('Missing ' + field)
        if not r['human_recorded'] or not r['transcript'].strip() or r['transcript'].startswith('REPLACE') or r['id'] in seen:
            raise ValueError('Require unique ID, human audio and a nonempty ground truth transcript')
        if r['intent'] not in ('book', 'cancel', 'reschedule', 'query') or not isinstance(r['entities'], dict):
            raise ValueError('Invalid intent/entities')
        seen.add(r['id'])
        path = (manifest.parent / r['audio']).resolve()
        if not path.is_relative_to(manifest.parent.resolve()):
            raise ValueError('Audio must be inside dataset directory')
        with wave.open(str(path)) as w:
            if (w.getframerate(), w.getnchannels(), w.getsampwidth(), w.getcomptype()) != (8000, 1, 2, 'NONE'):
                raise ValueError('Require 8kHz mono 16-bit PCM WAV decoded from human telephony audio')
            r['audio_duration_ms'] = w.getnframes()/8
        if r['audio_duration_ms'] <= 0:
            raise ValueError('Empty audio')
        r['_path'] = path
    return rows


def request_case(url, case, concurrency, language, timeout, warmup=False, operation="stt"):
    boundary = uuid.uuid4().hex
    body = (f'--{boundary}\r\nContent-Disposition: form-data; name="language"\r\n\r\n{language}\r\n'
            f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="caller.wav"\r\n'
            'Content-Type: audio/wav\r\n\r\n').encode() + case['_path'].read_bytes() + f'\r\n--{boundary}--\r\n'.encode()
    rid = str(uuid.uuid4())
    req = urllib.request.Request(url.rstrip('/')+'/v1/audio/transcriptions', data=body, headers={
        'Content-Type': 'multipart/form-data; boundary='+boundary,
        'X-Call-Id': 'benchmark', 'X-Turn-Id': rid, 'X-Test-Concurrency': str(concurrency)})
    if operation == 'tts':
        req = urllib.request.Request(url.rstrip('/')+'/v1/audio/speech',
            data=json.dumps({'input': case['transcript'], 'language': 'bn-BD'}).encode(),
            headers={'Content-Type': 'application/json', 'X-Call-Id': 'benchmark',
                     'X-Turn-Id': rid, 'X-Test-Concurrency': str(concurrency)})
    result = dict(case_id=case['id'], turn_id=rid, concurrency=concurrency,
                  warmup=warmup, audio_duration_ms=case['audio_duration_ms'], stage=operation+'_http_batch')
    start = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            data = json.load(response) if operation == 'stt' else {'audio_bytes': len(response.read())}
        ref, hyp = (tokens(case['transcript']), tokens(data['text'])) if operation == 'stt' else ([], [])
        result.update(outcome='success', word_errors=distance(ref, hyp), reference_words=len(ref), model=data.get('model'))
    except Exception as ex:
        # Never include response bodies or transcript/PII in the report.
        result.update(outcome='error', error_type=type(ex).__name__)
    result['duration_ms'] = (time.perf_counter()-start)*1000
    result['http_rtf'] = result['duration_ms']/case['audio_duration_ms'] if operation == 'stt' else None
    return result


def main():
    p = argparse.ArgumentParser()
    p.add_argument('manifest', type=Path)
    p.add_argument('--url', default='http://local-ai:8080')
    p.add_argument('--output', type=Path, default=Path('voice-benchmark.json'))
    p.add_argument('--language', default='auto')
    p.add_argument('--operation', choices=['stt', 'tts'], default='stt')
    p.add_argument('--timeout', type=float, default=180)
    p.add_argument('--validate-only', action='store_true')
    args = p.parse_args()
    cases = validate(args.manifest)
    if args.validate_only:
        print(f'Validated {len(cases)} labeled human recordings'); return
    records = [request_case(args.url, cases[0], 1, args.language, args.timeout, True, args.operation)]
    # One isolated request at a time, then batches of three; no caller-side executor backlog.
    for concurrency in (1, 3):
        with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as pool:
            for start in range(0, len(cases), concurrency):
                futures = [pool.submit(request_case, args.url, c, concurrency, args.language, args.timeout, False, args.operation)
                           for c in cases[start:start+concurrency]]
                records.extend(f.result() for f in futures)
    summaries = []
    for c in (1, 3):
        group = [r for r in records if r['concurrency'] == c and not r['warmup']]
        ok = [r for r in group if r['outcome'] == 'success']
        words = sum(r['reference_words'] for r in ok)
        summaries.append(dict(concurrency=c, samples=len(group), failures=len(group)-len(ok),
            successful_p50_ms=percentile([r['duration_ms'] for r in ok], .5),
            successful_p95_ms=percentile([r['duration_ms'] for r in ok], .95),
            normalized_wer=sum(r['word_errors'] for r in ok)/words if words else None))
    report = dict(measurement='batch HTTP, NOT streaming flush or caller-heard latency',
        operation=args.operation, streaming_gate='NOT_MEASURED', task_accuracy='NOT_MEASURED', language=args.language,
        warning='Small sample; verify actual negotiated codec. WER excludes failed requests.',
        summaries=summaries, records=records)
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(summaries, indent=2))
    return 1 if any(r['outcome'] != 'success' for r in records) else 0

if __name__ == '__main__':
    raise SystemExit(main())

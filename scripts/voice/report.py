#!/usr/bin/env python3
"""Read Docker logs from stdin; retain timing only, never SQL or transcript lines."""
import collections
import json
import math
import sys

def summarize(lines):
    groups = collections.defaultdict(list)
    unfinished = collections.Counter()
    records = []
    for line in lines:
        if 'VOICE_TIMING ' not in line:
            continue
        try:
            r = json.loads(line.split('VOICE_TIMING ', 1)[1])
            records.append({k: r.get(k) for k in ('call_id', 'turn_id', 'stage', 'started_at', 'ended_at', 'duration_ms', 'concurrency', 'outcome')})
            key = (r['stage'], r.get('outcome', 'unknown'))
            if r.get('duration_ms') is not None:
                groups[key].append(float(r['duration_ms']))
            else:
                unfinished[r['stage']] += 1
        except (ValueError, KeyError, TypeError):
            continue
    rows = []
    for (stage, outcome), values in sorted(groups.items()):
        values.sort()
        rows.append(dict(stage=stage, outcome=outcome, n=len(values),
            p50_ms=values[math.ceil(len(values)*.5)-1], p95_ms=values[math.ceil(len(values)*.95)-1]))
    return {'records': records, 'stages': rows, 'started_records': dict(unfinished),
            'note': 'Stages overlap/nest: DO NOT SUM. ARI ack is not caller-heard playback. Missing completion may be running, crashed or outside log window.'}

if __name__ == '__main__':
    print(json.dumps(summarize(sys.stdin), indent=2))

"""Provider stage timings. No transcripts, audio or phone numbers in logs."""
import contextvars
import json
import time
from contextlib import contextmanager
from datetime import datetime, timezone

context = contextvars.ContextVar('voice_timing', default={})

def utc():
    return datetime.now(timezone.utc).isoformat()

@contextmanager
def stage(name):
    started, tick = utc(), time.perf_counter()
    base = dict(context.get())
    base.update(schema_version=1, pipeline_version='batch-v1', stage=name,
                started_at=started, ended_at=None, duration_ms=None, outcome='started')
    print('VOICE_TIMING ' + json.dumps(base), flush=True)
    outcome = 'error'
    try:
        yield base
        outcome = 'success'
    finally:
        base.update(ended_at=utc(), duration_ms=(time.perf_counter()-tick)*1000, outcome=outcome)
        print('VOICE_TIMING ' + json.dumps(base), flush=True)

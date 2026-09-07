import importlib.util
import json
import tempfile
import unittest
import wave
from pathlib import Path

spec = importlib.util.spec_from_file_location('benchmark', Path(__file__).with_name('benchmark.py'))
b = importlib.util.module_from_spec(spec)
spec.loader.exec_module(b)

class BenchmarkTests(unittest.TestCase):
    def test_insert_delete_substitute(self):
        self.assertEqual(b.distance(['a', 'b'], ['a', 'c', 'd']), 2)
        self.assertEqual(b.distance(['a'], []), 1)
    def test_nearest_rank(self):
        self.assertEqual(b.percentile(list(range(1, 21)), .95), 19)
        self.assertIsNone(b.percentile([], .95))
    def test_digit_normalization(self):
        self.assertEqual(b.tokens('১০ টা'), b.tokens('10 টা'))
    def test_reject_synthetic_and_wrong_rate(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d)
            with wave.open(str(p/'a.wav'), 'wb') as w:
                w.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
                w.writeframes(b'\x00\x00'*160)
            r = dict(id='a', audio='a.wav', transcript='test', intent='query', entities={},
                     recorded_at='2026-09-07T00:00:00Z', timezone='Asia/Dhaka', human_recorded=False)
            m = p/'manifest.json'
            m.write_text(json.dumps([r]))
            with self.assertRaises(ValueError): b.validate(m)
            r['human_recorded'] = True
            m.write_text(json.dumps([r]))
            with self.assertRaises(ValueError): b.validate(m)


class RequestTests(unittest.TestCase):
    def test_http_stt_tts_and_failure(self):
        import io
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as d:
            audio = Path(d)/'a.wav'
            audio.write_bytes(b'test fixture')
            case = dict(id='fixture', _path=audio, transcript='১০ টা', audio_duration_ms=1000)
            with patch.object(b.urllib.request, 'urlopen', return_value=io.BytesIO(json.dumps({'text':'10 টা'}).encode())) as send:
                result = b.request_case('http://example', case, 1, 'auto', 1)
                self.assertEqual(result['word_errors'], 0)
                self.assertEqual(result['outcome'], 'success')
                self.assertIn(b'auto', send.call_args.args[0].data)
            with patch.object(b.urllib.request, 'urlopen', return_value=io.BytesIO(b'RIFF')):
                result = b.request_case('http://example', case, 1, 'auto', 1, operation='tts')
                self.assertEqual(result['outcome'], 'success')
                self.assertIsNone(result['http_rtf'])
            with patch.object(b.urllib.request, 'urlopen', side_effect=TimeoutError('private detail')):
                result = b.request_case('http://example', case, 3, 'auto', 1)
                self.assertEqual(result['outcome'], 'error')
                self.assertNotIn('private detail', json.dumps(result))

    def test_timing_failure_keeps_correlation_without_text(self):
        import contextlib
        import io
        spec = importlib.util.spec_from_file_location('voice_timing', Path(__file__).resolve().parents[2]/'infra/local-ai/voice_timing.py')
        timing = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(timing)
        token = timing.context.set({'call_id':'c', 'turn_id':'t'})
        output = io.StringIO()
        try:
            with contextlib.redirect_stdout(output):
                with self.assertRaises(ValueError):
                    with timing.stage('stt'):
                        raise ValueError('private transcript')
        finally:
            timing.context.reset(token)
        rows = [json.loads(line.split('VOICE_TIMING ', 1)[1]) for line in output.getvalue().splitlines()]
        self.assertEqual([r['outcome'] for r in rows], ['started', 'error'])
        self.assertEqual(rows[1]['turn_id'], 't')
        self.assertGreaterEqual(rows[1]['duration_ms'], 0)
        self.assertNotIn('private transcript', output.getvalue())

class ReportTests(unittest.TestCase):
    def test_rtf_uses_same_record_duration(self):
        spec = importlib.util.spec_from_file_location('report', Path(__file__).with_name('report.py'))
        report = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(report)
        result = report.summarize(['VOICE_TIMING ' + json.dumps(dict(call_id='c', turn_id='t',
            stage='stt_inference_batch', outcome='success', duration_ms=900,
            audio_duration_ms=3000, vad_audio_duration_ms=2000))])
        self.assertAlmostEqual(result['records'][0]['inference_rtf'], .3)
        self.assertEqual(result['records'][0]['vad_audio_duration_ms'], 2000)

if __name__ == '__main__': unittest.main()

import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SOURCE = Path(__file__).resolve().parents[2] / 'scripts'
class OperationsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        shutil.copytree(SOURCE, self.root / 'scripts', ignore=shutil.ignore_patterns('__pycache__'))
        sys.path.insert(0, str(self.root / 'scripts'))
    def tearDown(self):
        sys.path.remove(str(self.root / 'scripts'))
        for name in ('demo', 'benchmark', 'init_local'):
            sys.modules.pop(name, None)
        self.temporary.cleanup()
    def cli(self, script, *arguments, env=None):
        return subprocess.run([sys.executable, str(self.root / 'scripts' / script), *arguments], cwd=self.root, text=True, capture_output=True, env={**os.environ, **(env or {})}, timeout=15)
    def load(self, filename):
        name = filename.removesuffix('.py').replace('-', '_')
        spec = importlib.util.spec_from_file_location(name, self.root / 'scripts' / filename)
        module = importlib.util.module_from_spec(spec); sys.modules[name] = module; spec.loader.exec_module(module)
        return module

    def test_initializer_retains_existing_realm(self):
        local = self.root / '.local'; local.mkdir(mode=0o700)
        realm = local / 'realm.json'; realm.write_text('existing realm')
        result = self.cli('init-local.py')
        self.assertNotEqual(0, result.returncode)
        self.assertFalse((self.root / '.env').exists())
        self.assertEqual('existing realm', realm.read_text())


    def test_initializer_rejects_unsafe_layouts(self):
        outside = self.root / 'outside'; outside.mkdir()
        local = self.root / '.local'; local.symlink_to(outside, target_is_directory=True)
        self.assertNotEqual(0, self.cli('init-local.py').returncode)
        self.assertFalse((self.root / '.env').exists()); self.assertEqual([], list(outside.iterdir()))
        local.unlink(); local.mkdir(mode=0o755)
        self.assertNotEqual(0, self.cli('init-local.py').returncode)
        local.chmod(0o700)
        result = self.cli('init-local.py'); self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(0o600, (self.root / '.env').stat().st_mode & 0o777)
        for line in (self.root / '.env').read_text().splitlines():
            self.assertNotIn(line.split('=', 1)[1], result.stdout + result.stderr)


    def test_demo_help_needs_no_credentials(self):
        result = self.cli('demo.py', '--help')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('--faults', result.stdout); self.assertFalse((self.root / '.env').exists())
        module = self.load('demo.py'); self.assertTrue(callable(module.token))


    def test_demo_endpoints_are_local_and_configurable(self):
        with patch.dict(os.environ, {'ORDERS_API_URL': 'http://127.0.0.1:54320', 'ORDERS_WORKER_URL': 'http://localhost:54321'}):
            module = self.load('demo.py')
            self.assertEqual('http://127.0.0.1:54320', module.API)
            self.assertEqual('http://localhost:54321', module.WORKER)
        result = self.cli('demo.py', '--help', env={'ORDERS_API_URL': 'https://external.example'})
        self.assertNotEqual(0, result.returncode)
        self.assertIn('loopback', result.stderr)


    def test_demo_preserves_non_json_error_status(self):
        module = self.load('demo.py')
        error = module.urllib.error.HTTPError('http://localhost', 503, 'Unavailable', {}, io.BytesIO(b'private proxy response'))
        with patch.object(module.urllib.request, 'build_opener') as opener:
            opener.return_value.open.side_effect = error
            status, body = module.request('GET', 'http://localhost')
        self.assertEqual(503, status); self.assertEqual('non_json_response', body['error'])
        self.assertNotIn('private proxy response', json.dumps(body))


    def test_compose_has_deadline_and_recovery(self):
        module = self.load('demo.py')
        with patch.object(module.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, 'ok', '')) as execute:
            self.assertEqual('ok', module.compose('ps'))
            self.assertEqual(45, execute.call_args.kwargs.get('timeout'))
        operations = []
        def failed_stop(*args):
            operations.append(args)
            if args[0] == 'stop': raise RuntimeError('partial stop')
        with patch.object(module, 'compose', side_effect=failed_stop):
            with self.assertRaises(RuntimeError):
                with module.stopped_services('broker'): self.fail('stop failed')
        self.assertEqual([('stop', 'broker'), ('start', 'broker')], operations)


    def test_broker_drain_requires_expected_group(self):
        module = self.load('demo.py')
        self.assertTrue(hasattr(module, 'broker_drained'))
        for groups, expected in [([], False), ([{'name': 'other', 'pending': 0, 'lag': 0}], False),
            ([{'name': 'orders-worker', 'pending': 0, 'lag': None}], False),
            ([{'name': 'orders-worker', 'pending': 1, 'lag': 0}], False),
            ([{'name': 'orders-worker', 'pending': 0, 'lag': 0}], True)]:
            with patch.object(module, 'compose', return_value=json.dumps(groups)):
                self.assertEqual(expected, module.broker_drained())


    def test_demo_records_sanitized_partial_failure(self):
        module = self.load('demo.py'); self.assertTrue(hasattr(module, 'main'))
        secret = 'credential-value-that-must-stay-private'; (self.root / '.env').write_text('SECRET=' + secret + '\n')
        output = self.root / 'evidence.json'
        def fail(faults, checks):
            checks.append('first check passed'); raise RuntimeError('failed with ' + secret)
        captured = io.StringIO()
        with patch.object(module, 'run', side_effect=fail), contextlib.redirect_stdout(captured):
            self.assertEqual(1, module.main(['--output', str(output)]))
        result = json.loads(output.read_text()); self.assertEqual('failed', result['status']); self.assertEqual(1, result['checks_passed'])
        self.assertNotIn(secret, output.read_text() + captured.getvalue())


    def test_benchmark_keeps_setup_failure_evidence(self):
        (self.root / '.env').write_text('ORDERS_CLIENT_SECRET=fixture-only\n')
        output = self.root / 'benchmark.json'
        result = self.cli('benchmark.py', '--requests', '1', '--concurrency', '1', '--output', str(output), env={'ORDERS_KEYCLOAK_URL': 'http://127.0.0.1:1'})
        self.assertNotEqual(0, result.returncode)
        self.assertTrue(output.exists(), result.stderr)
        evidence = json.loads(output.read_text()); self.assertEqual('failed', evidence['status']); self.assertEqual('setup', evidence['phase'])


    def test_benchmark_polls_completion_concurrently(self):
        import threading
        self.load('demo.py'); module = self.load('benchmark.py'); barrier = threading.Barrier(2, timeout=2)
        def complete(*args): barrier.wait(); return True
        def accepted(*args): return 202, {'id': __import__('uuid').uuid4().hex}
        with patch.object(module, 'token', return_value='fixture'), patch.object(module, 'ledger', return_value={'orders': {}}), patch.object(module, 'request', side_effect=accepted), patch.object(module, 'fulfilled', side_effect=complete):
            result = module.run_baseline(4, 2)
        self.assertEqual(4, result['fulfilled']); self.assertEqual('passed', result['status'])
        self.assertIsNotNone(result['completion_latency_ms']['p95'])


    def test_nuget_gate_checks_severity_and_report_shape(self):
        report = self.root / 'nuget.json'
        report.write_text(json.dumps({'version': 1, 'projects': [{'path': 'app.csproj', 'frameworks': [{'transitivePackages': [{'id': 'dependency', 'vulnerabilities': [{'severity': 'High', 'advisoryurl': 'https://example.invalid/advisory'}]}]}]}]}))
        self.assertEqual(1, self.cli('check-vulnerabilities.py', '--nuget', str(report)).returncode)
        report.write_text(json.dumps({'version': 1, 'projects': [{'path': 'app.csproj'}]}))
        self.assertEqual(0, self.cli('check-vulnerabilities.py', '--nuget', str(report)).returncode)
        report.write_text('{}')
        self.assertEqual(2, self.cli('check-vulnerabilities.py', '--nuget', str(report)).returncode)


    def test_ci_actions_use_immutable_revisions(self):
        import re
        text = (SOURCE.parent / '.github/workflows/ci.yml').read_text()
        actions = re.findall(r'uses:\s+(\S+)', text)
        self.assertGreaterEqual(len(actions), 3)
        for action in actions: self.assertRegex(action, r'^[^@]+@[0-9a-f]{40}$')


    def test_runtime_images_use_recorded_digests(self):
        import re
        dockerfile = (SOURCE.parent / 'Dockerfile').read_text(); compose = (SOURCE.parent / 'compose.yaml').read_text()
        images = re.findall(r'^FROM (\S+)', dockerfile, re.M) + re.findall(r'^\s+image:\s*(\S+)', compose, re.M)
        remote = [image for image in images if not image.startswith('orders-retrospective:')]
        self.assertGreaterEqual(len(remote), 5)
        for image in remote: self.assertRegex(image, r'@sha256:[0-9a-f]{64}$')


    def test_trivy_gate_checks_severity_and_report_shape(self):
        report = self.root / 'trivy.json'
        report.write_text(json.dumps({'SchemaVersion': 2, 'Results': [{'Vulnerabilities': [{'PkgName': 'library', 'Severity': 'CRITICAL', 'VulnerabilityID': 'CVE-FIXTURE'}]}]}))
        self.assertEqual(1, self.cli('check-vulnerabilities.py', '--trivy', str(report)).returncode)
        report.write_text(json.dumps({'SchemaVersion': 2, 'Results': []}))
        self.assertEqual(0, self.cli('check-vulnerabilities.py', '--trivy', str(report)).returncode)
        report.write_text('{}'); self.assertEqual(2, self.cli('check-vulnerabilities.py', '--trivy', str(report)).returncode)


    def test_snapshot_integrity_and_non_overwriting_restore(self):
        self.load('demo.py'); self.assertTrue((self.root / 'scripts/ledger-snapshot.py').exists())
        module = self.load('ledger-snapshot.py')
        ledger = {'schemaVersion': 1, 'orders': {}, 'requests': {}, 'outbox': {}, 'receipts': []}
        with patch.object(module, 'compose', return_value=json.dumps(['data', json.dumps(ledger), 'version', '7', 'first-write', '0'])):
            snapshot = module.capture(); self.assertEqual('7', snapshot['source_version']); module.verify(snapshot)
        altered = dict(snapshot); altered['sha256'] = '0' * 64
        with self.assertRaises(ValueError): module.verify(altered)
        calls = []
        def redis(*args, **kwargs):
            calls.append((args, kwargs)); return '[]' if args[0] == 'ps' else '1'
        with patch.object(module, 'compose', side_effect=redis): module.restore(snapshot, 'restore-fixture')
        self.assertIn('EXISTS', calls[-1][0][-3]); self.assertEqual(ledger, json.loads(calls[-1][1]['input']))
        with patch.object(module, 'compose', side_effect=['[]', '0']):
            with self.assertRaises(FileExistsError): module.restore(snapshot, 'existing-fixture')
        with patch.object(module, 'compose', return_value=json.dumps([{'State': 'running'}])):
            with self.assertRaises(RuntimeError): module.restore(snapshot)


    def test_snapshot_rejects_runtime_incompatible_dates_and_versions(self):
        import hashlib
        import copy
        self.load('demo.py'); module = self.load('ledger-snapshot.py')
        order_id, event_id = '1' * 32, '2' * 32
        ledger = {'schemaVersion': 1, 'orders': {order_id: {'id': order_id, 'eventId': event_id, 'owner': 'review', 'sku': 'SKU', 'quantity': 1, 'status': 'accepted'}},
                  'requests': {'a' * 64: {'fingerprint': 'b' * 64, 'orderId': order_id, 'version': 1}},
                  'outbox': {event_id: {'eventId': event_id, 'orderId': order_id, 'version': 1}}, 'receipts': []}
        def snapshot(value):
            return {'format': 1, 'recorded_at': '2021-01-01T00:00:00Z', 'source_key': 'orders-ledger-v1', 'source_version': '1', 'ledger': value, 'sha256': hashlib.sha256(module.canonical(value).encode()).hexdigest()}
        for value in [None, '2026-09-29T01:02:03Z', '2026-09-29T01:02:03.1234567+05:30']:
            valid = copy.deepcopy(ledger); valid['orders'][order_id]['acceptedAt'] = value
            module.verify(snapshot(valid))
        invalid = []
        for field in ('acceptedAt', 'fulfilledAt'):
            for value in ['yesterday', True, 123, '2026-02-30T01:02:03Z', '2026-09-29T01:02:03', '2026-09-29T01:02:03+14:01', '2026-09-29T01:02:03+00:60', '2026-09-29T01:02:03-00:99', '0001-01-01T00:00:00+01:00']:
                candidate = copy.deepcopy(ledger); candidate['orders'][order_id][field] = value; invalid.append(snapshot(candidate))
        for collection in ('requests', 'outbox'):
            for version in [True, 1.0, '1']:
                candidate = copy.deepcopy(ledger); next(iter(candidate[collection].values()))['version'] = version; invalid.append(snapshot(candidate))
        candidate = snapshot(ledger); candidate['format'] = True; invalid.append(candidate)
        for candidate in invalid:
            with self.subTest(candidate=candidate), patch.object(module, 'compose') as command:
                with self.assertRaises(ValueError): module.restore(candidate, 'invalid-snapshot')
                command.assert_not_called()

# TESTS

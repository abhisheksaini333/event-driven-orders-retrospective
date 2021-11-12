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
        with patch.object(module.urllib.request, 'urlopen', side_effect=error):
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

# TESTS

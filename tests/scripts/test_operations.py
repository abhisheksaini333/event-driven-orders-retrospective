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

# TESTS

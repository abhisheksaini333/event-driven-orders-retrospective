import contextlib
import hashlib
import io
import json
import os
import sys
import unittest
from unittest.mock import patch
from test_operations import OperationsTests

class MaintenanceTests(unittest.TestCase):
    setUp=OperationsTests.setUp
    cli=OperationsTests.cli
    load=OperationsTests.load
    def tearDown(self):
        OperationsTests.tearDown(self)
        for name in ('ledger_snapshot','check_vulnerabilities'):sys.modules.pop(name,None)
    def snapshot(self,module):
        ledger={'schemaVersion':1,'orders':{},'requests':{},'outbox':{},'receipts':[]}
        return {'format':1,'recorded_at':'2021-01-01T00:00:00Z','source_key':'orders-ledger-v1','source_version':'1','ledger':ledger,'sha256':hashlib.sha256(module.canonical(ledger).encode()).hexdigest()}

    def test_percentile_uses_nearest_rank(self):
        module=self.load('benchmark.py')
        self.assertEqual(module.percentile(list(range(1,9)),.95),8)
        self.assertEqual(module.percentile([1,2,3],.5),2)

    def test_percentile_rejects_invalid_observations_and_fractions(self):
        module=self.load('benchmark.py')
        for values,fraction in (([float('nan')],.5),([-1],.5),([1],True),([1],1.1)):
            with self.assertRaises(ValueError):module.percentile(values,fraction)

    def test_baseline_limits_apply_before_external_setup(self):
        module=self.load('benchmark.py')
        with patch.object(module,'token',side_effect=AssertionError('unexpected token request')):
            for count,concurrency in ((0,1),(1,0),(True,1),(1,17)):
                with self.assertRaises(ValueError):module.run_baseline(count,concurrency)

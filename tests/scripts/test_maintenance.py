import contextlib
import hashlib
import io
import json
import os
import sys
import unittest
from unittest.mock import patch
import test_operations as operations

class MaintenanceTests(unittest.TestCase):
    setUp=operations.OperationsTests.setUp
    cli=operations.OperationsTests.cli
    load=operations.OperationsTests.load
    def tearDown(self):
        operations.OperationsTests.tearDown(self)
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

    def test_benchmark_report_preserves_existing_evidence(self):
        module=self.load('benchmark.py');output=self.root/'result.json';output.write_text('existing')
        with patch.object(module,'run_baseline',return_value={'status':'passed'}),contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(FileExistsError):module.main(['--output',str(output)])
        self.assertEqual(output.read_text(),'existing')

    def test_loopback_origins_require_valid_ports(self):
        module=self.load('demo.py')
        for url in ('http://localhost:0','http://localhost:65536','http://localhost:bad'):
            with patch.dict(os.environ,{'MAINTENANCE_URL':url}),self.assertRaises(ValueError):module.local_origin('MAINTENANCE_URL','http://localhost')

    def test_authenticated_requests_refuse_redirects(self):
        module=self.load('demo.py')
        handler=module.NoRedirect()
        self.assertIsNone(handler.redirect_request(None,None,302,'redirect',{},'https://outside.invalid'))
        with patch.object(module.urllib.request,'urlopen',side_effect=AssertionError('unsafe opener')),patch.object(module.urllib.request,'build_opener') as opener:
            response=opener.return_value.open.return_value.__enter__.return_value
            response.status=200;response.read.return_value=b'{}'
            self.assertEqual(module.request('GET','http://localhost/test',token='secret')[0],200)

#!/usr/bin/env python3
"""Authenticated synthetic end-to-end checks. --faults briefly stops this Compose project's services."""
import argparse
from contextlib import contextmanager
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
def local_origin(name, fallback):
    value = os.environ.get(name, fallback)
    parsed = urllib.parse.urlparse(value)
    if parsed.scheme not in ('http', 'https') or parsed.hostname not in ('localhost', '127.0.0.1', '::1') or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ('', '/'):
        raise ValueError(name + ' must be a loopback HTTP(S) origin.')
    return value.rstrip('/')

API = local_origin('ORDERS_API_URL', 'http://127.0.0.1:4320')
KEYCLOAK = local_origin('ORDERS_KEYCLOAK_URL', 'http://127.0.0.1:4322')
WORKER = local_origin('ORDERS_WORKER_URL', 'http://127.0.0.1:4321')
def load_env():
    return dict(line.split('=', 1) for line in (ROOT / '.env').read_text().splitlines() if '=' in line and not line.lstrip().startswith('#'))

def decode_response(response):
    raw = response.read(1048577)
    if len(raw) > 1048576:
        return {'error': 'response_too_large'}
    if not raw:
        return None
    try:
        return json.loads(raw)
    except (json.JSONDecodeError, UnicodeDecodeError):
        return {'error': 'non_json_response', 'bytes': len(raw)}

def request(method, url, data=None, token=None, key=None, headers=None):
    actual = {'Content-Type': 'application/json', **(headers or {})}
    if token:
        actual['Authorization'] = 'Bearer ' + token
    if key:
        actual['Idempotency-Key'] = key
    body = json.dumps(data).encode() if data is not None else None
    try:
        with urllib.request.urlopen(urllib.request.Request(url, data=body, method=method, headers=actual), timeout=15) as response:
            return response.status, decode_response(response)
    except urllib.error.HTTPError as error:
        return error.code, decode_response(error)

def token(reader=False):
    body = urllib.parse.urlencode({'grant_type': 'client_credentials', 'client_id': 'reader-cli' if reader else 'orders-cli',
                                  'client_secret': load_env()['READER_CLIENT_SECRET' if reader else 'ORDERS_CLIENT_SECRET']}).encode()
    req = urllib.request.Request(KEYCLOAK + '/realms/orders/protocol/openid-connect/token', data=body)
    with urllib.request.urlopen(req, timeout=10) as response:
        return json.load(response)['access_token']

def compose(*args):
    result = subprocess.run(['docker', 'compose', *args], cwd=ROOT, capture_output=True, text=True, timeout=45)
    if result.returncode:
        raise RuntimeError(f'Compose {args[0]} failed: {result.stderr[-1500:]}')
    return result.stdout.strip()

@contextmanager
def stopped_services(*services):
    try:
        compose('stop', *services)
        yield
    finally:
        compose('start', *services)

def ledger():
    raw = compose('exec', '-T', 'redis', 'redis-cli', '--raw', 'HGET', 'orders-ledger-v1', 'data')
    return json.loads(raw) if raw else {'orders': {}, 'requests': {}, 'outbox': {}, 'receipts': []}

def until(check, timeout=90):
    end = time.monotonic() + timeout
    last = None
    while time.monotonic() < end:
        try:
            last = check()
            if last:
                return last
        except (OSError, urllib.error.URLError, subprocess.CalledProcessError):
            pass
        time.sleep(0.5)
    raise AssertionError(f'Timed out after {timeout}s; last result={last!r}')

def fulfilled(bearer, order_id):
    status, body = request('GET', API + '/orders/' + order_id, token=bearer)
    return status == 200 and body.get('status') == 'fulfilled'

def run(faults):
    checks = []
    def passed(name):
        checks.append(name)
        print('PASS ' + name, flush=True)
    until(lambda: request('GET', API + '/health/ready')[0] == 200)
    bearer = until(token)
    reader = token(True)
    payload = {'sku': 'SYNTHETIC-BOOK', 'quantity': 2}
    key = 'demo-' + uuid.uuid4().hex
    assert request('POST', API + '/orders', payload, key=key)[0] == 401
    passed('missing authentication returns 401')
    assert request('POST', API + '/orders', payload, reader, key)[0] == 403
    passed('verified reader token without writer role returns 403')
    assert request('POST', API + '/orders', {'sku': 'SYNTHETIC-BOOK', 'quantity': 0}, bearer, key)[0] == 400
    passed('invalid quantity rejected with 400')
    status, order = request('POST', API + '/orders', payload, bearer, key)
    assert status == 202, (status, order)
    assert request('POST', API + '/orders', payload, bearer, key)[0] == 200
    assert request('POST', API + '/orders', {'sku': 'SYNTHETIC-BOOK', 'quantity': 3}, bearer, key)[0] == 409
    passed('accepted order, identical replay and changed-content conflict')
    assert request('GET', API + '/orders/' + order['id'], token=reader)[0] == 404
    passed('other identity cannot read order')
    until(lambda: fulfilled(bearer, order['id']))
    passed('real Dapr pubsub delivers and worker fulfills')
    assert request('POST', WORKER + '/events/orders', {'data': {}})[0] == 401
    passed('worker callback requires sidecar token')
    concurrent_key = 'race-' + uuid.uuid4().hex
    with ThreadPoolExecutor(max_workers=8) as pool:
        responses = list(pool.map(lambda _: request('POST', API + '/orders', payload, bearer, concurrent_key), range(16)))
    assert [r[0] for r in responses].count(202) == 1, responses
    assert all(r[0] in (200, 202) for r in responses), responses
    assert len({r[1]['id'] for r in responses}) == 1
    passed('16 concurrent retries create exactly one durable order')
    envelope = {'specversion': '1.0', 'id': uuid.uuid4().hex, 'source': 'retrospective-test', 'type': 'orders.accepted',
                'datacontenttype': 'application/json', 'data': {'eventId': order['eventId'], 'orderId': order['id'], 'version': 1}}
    for _ in range(3):
        compose('exec', '-T', 'broker', 'redis-cli', 'XADD', 'orders.accepted', '*', 'data', json.dumps(envelope))
    until(lambda: all(group['pending'] == 0 and group['lag'] == 0 for group in json.loads(compose('exec', '-T', 'broker', 'redis-cli', '--json', 'XINFO', 'GROUPS', 'orders.accepted'))))
    state = ledger()
    assert state['receipts'].count(order['eventId']) == 1
    passed('three broker duplicate deliveries retain one fulfillment receipt')
    if faults:
        with stopped_services('broker'):
            failed_key = 'outage-' + uuid.uuid4().hex
            status, pending = request('POST', API + '/orders', payload, bearer, failed_key)
            assert status == 202, (status, pending)
            until(lambda: pending['eventId'] in ledger()['outbox'])
            compose('restart', 'api')
            until(lambda: request('GET', API + '/health/ready')[0] == 200)
            status, replay = request('POST', API + '/orders', payload, bearer, failed_key)
            assert status == 200 and replay['id'] == pending['id'], (status, replay)
            passed('broker outage leaves durable outbox across API restart')
        until(lambda: fulfilled(bearer, pending['id']))
        until(lambda: pending['eventId'] not in ledger()['outbox'])
        passed('broker recovery drains outbox and fulfills pending order')
        with stopped_services('worker-dapr', 'worker'):
            status, queued = request('POST', API + '/orders', payload, bearer, 'worker-' + uuid.uuid4().hex)
            assert status == 202
            until(lambda: queued['eventId'] not in ledger()['outbox'])
        until(lambda: fulfilled(bearer, queued['id']))
        passed('queued broker event survives worker downtime')
        compose('restart', 'redis')
        until(lambda: fulfilled(bearer, order['id']))
        assert request('POST', API + '/orders', payload, bearer, key)[1]['id'] == order['id']
        passed('state Redis restart retains order and idempotency record')
    result = {'recorded_at': datetime.now(timezone.utc).isoformat(), 'checks_passed': len(checks), 'fault_injection': faults, 'checks': checks}
    print(json.dumps(result, indent=2))
    return result

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--faults', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    result = run(args.faults)
    if args.output:
        args.output.write_text(json.dumps(result, indent=2) + '\n')

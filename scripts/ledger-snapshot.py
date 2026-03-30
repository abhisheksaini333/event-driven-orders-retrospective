#!/usr/bin/env python3
"""Export validated state snapshots and restore only into an absent local ledger key."""
import argparse
from datetime import datetime, timezone, timedelta
import hashlib
import json
import os
from pathlib import Path
import re
from demo import compose, strict_json

MAXIMUM_BYTES = 16 * 1024 * 1024
IDENTIFIER = re.compile(r'[a-f0-9]{32}')
DIGEST = re.compile(r'[A-Fa-f0-9]{64}')

def validate_timestamp(value):
    if value is None: return
    if not isinstance(value, str) or not re.fullmatch(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})', value):
        raise ValueError('Invalid order timestamp.')
    try:
        if value[-1] != 'Z' and int(value[-2:]) > 59: raise ValueError('Invalid offset minute.')
        parsed = datetime.fromisoformat(value)
        if abs(parsed.utcoffset()) > timedelta(hours=14): raise ValueError('Invalid timestamp offset.')
        parsed.astimezone(timezone.utc)  # DateTimeOffset also requires the UTC instant to fit years 1..9999.
    except (ValueError, OverflowError) as error:
        raise ValueError('Invalid order timestamp.') from error

def validate_ledger(ledger):
    if not isinstance(ledger, dict) or type(ledger.get('schemaVersion', 1)) is not int or ledger.get('schemaVersion', 1) != 1:
        raise ValueError('Unsupported ledger schema.')
    for name in ('orders', 'requests', 'outbox'):
        if not isinstance(ledger.get(name), dict): raise ValueError('Missing ledger collection.')
    if not isinstance(ledger.get('receipts'), list): raise ValueError('Missing receipt list.')
    orders = ledger['orders']; events = set()
    for key, order in orders.items():
        if not isinstance(order, dict) or not IDENTIFIER.fullmatch(key) or order.get('id') != key:
            raise ValueError('Invalid order identifier.')
        for field in ('acceptedAt', 'fulfilledAt'): validate_timestamp(order.get(field))
        event = order.get('eventId'); owner = order.get('owner'); sku = order.get('sku'); quantity = order.get('quantity')
        if not isinstance(event, str) or not IDENTIFIER.fullmatch(event) or event in events:
            raise ValueError('Invalid order event.')
        if not isinstance(owner, str) or not owner.strip() or len(owner) > 256 or any(ord(char) < 32 or ord(char) == 127 for char in owner):
            raise ValueError('Invalid order owner.')
        if not isinstance(sku, str) or not re.fullmatch(r'[A-Z0-9-]{1,32}', sku) or type(quantity) is not int or not 1 <= quantity <= 1000:
            raise ValueError('Invalid order payload.')
        if order.get('status') not in ('accepted', 'fulfilled'): raise ValueError('Invalid order status.')
        accepted, fulfilled = order.get('acceptedAt'), order.get('fulfilledAt')
        if order['status'] == 'accepted' and fulfilled is not None or accepted is not None and fulfilled is not None and datetime.fromisoformat(fulfilled) < datetime.fromisoformat(accepted):
            raise ValueError('Inconsistent lifecycle timestamps.')
        if (order['status'] == 'fulfilled') != (event in ledger['receipts']): raise ValueError('Inconsistent fulfillment receipt.')
        events.add(event)
    receipts = ledger['receipts']
    if any(not isinstance(item, str) or item not in events for item in receipts) or len(receipts) != len(set(receipts)):
        raise ValueError('Invalid or duplicate receipt.')
    for key, request in ledger['requests'].items():
        if not isinstance(request, dict) or not DIGEST.fullmatch(key) or request.get('orderId') not in orders or not isinstance(request.get('fingerprint'), str) or not DIGEST.fullmatch(request['fingerprint']) or type(request.get('version', 1)) is not int or request.get('version', 1) != 1:
            raise ValueError('Invalid idempotency reference.')
    for key, event in ledger['outbox'].items():
        if not isinstance(event, dict) or event.get('eventId') != key or event.get('orderId') not in orders or type(event.get('version')) is not int or event.get('version') != 1 or orders[event['orderId']]['eventId'] != key:
            raise ValueError('Invalid outbox reference.')
    return ledger

def canonical(ledger):
    return json.dumps(ledger, sort_keys=True, separators=(',', ':'), allow_nan=False)

def valid_key(key):
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,63}', key): raise ValueError('Invalid Redis key.')
    return key

def capture(key='orders-ledger-v1'):
    raw = compose('exec', '-T', 'redis', 'redis-cli', '--json', 'HGETALL', valid_key(key))
    fields = strict_json(raw)
    if isinstance(fields, list):
        if len(fields) % 2: raise ValueError('Malformed Redis hash response.')
        if len(set(fields[::2])) != len(fields[::2]): raise ValueError('Duplicate Redis hash field.')
        fields = dict(zip(fields[::2], fields[1::2]))
    if not isinstance(fields, dict) or not fields: raise FileNotFoundError('Ledger is absent.')
    data = fields.get('data')
    if not isinstance(data, str) or len(data.encode()) > MAXIMUM_BYTES: raise ValueError('Invalid ledger payload size.')
    if int(fields.get('version', '0')) <= 0: raise ValueError('Invalid source state version.')
    ledger = validate_ledger(strict_json(data)); encoded = canonical(ledger)
    return {'format': 1, 'recorded_at': datetime.now(timezone.utc).isoformat(), 'source_key': key,
            'source_version': str(fields['version']), 'ledger': ledger, 'sha256': hashlib.sha256(encoded.encode()).hexdigest()}

def verify(snapshot):
    if not isinstance(snapshot, dict) or type(snapshot.get('format')) is not int or snapshot.get('format') != 1: raise ValueError('Unsupported snapshot format.')
    if not isinstance(snapshot.get('source_key'), str): raise ValueError('Missing snapshot source key.')
    valid_key(snapshot['source_key'])
    version = snapshot.get('source_version')
    if not isinstance(version, str) or not re.fullmatch(r'[1-9][0-9]{0,18}', version) or int(version) > 9223372036854775807: raise ValueError('Invalid snapshot source version.')
    if not isinstance(snapshot.get('recorded_at'), str): raise ValueError('Missing snapshot capture timestamp.')
    validate_timestamp(snapshot['recorded_at'])
    ledger = validate_ledger(snapshot.get('ledger')); encoded = canonical(ledger)
    if len(encoded.encode()) > MAXIMUM_BYTES: raise ValueError('Snapshot exceeds size limit.')
    if hashlib.sha256(encoded.encode()).hexdigest() != snapshot.get('sha256'): raise ValueError('Snapshot digest mismatch.')
    return encoded

def restore(snapshot, key='orders-ledger-v1'):
    encoded = verify(snapshot); valid_key(key)
    raw = compose('ps', '--all', '--format', 'json', 'api', 'worker').strip()
    try: services = json.loads(raw) if raw else []
    except json.JSONDecodeError: services = [json.loads(line) for line in raw.splitlines() if line.strip()]
    if isinstance(services, dict): services = [services]
    if any(item['State'] in ('running', 'restarting', 'paused') for item in services):
        raise RuntimeError('Stop API and worker before restoring state.')
    operation = "if redis.call('EXISTS', KEYS[1]) ~= 0 then return 0 end; redis.call('HSET', KEYS[1], 'data', ARGV[1], 'version', 1, 'first-write', 0); return 1"
    result = compose('exec', '-T', 'redis', 'redis-cli', '--raw', '-x', 'EVAL', operation, '1', key, input=encoded)
    if result.strip() != '1': raise FileExistsError('Restore refused because the target key already exists.')

def main(argv=None):
    parser = argparse.ArgumentParser(); commands = parser.add_subparsers(dest='action', required=True)
    export = commands.add_parser('export'); export.add_argument('--output', type=Path, required=True); export.add_argument('--key', default='orders-ledger-v1')
    load = commands.add_parser('restore'); load.add_argument('--input', type=Path, required=True); load.add_argument('--key', default='orders-ledger-v1')
    args = parser.parse_args(argv)
    try:
        if args.action == 'export':
            snapshot = capture(args.key)
            with os.fdopen(os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), 'w') as target:
                json.dump(snapshot, target, indent=2); target.write('\n'); target.flush(); os.fsync(target.fileno())
        else:
            if args.input.stat().st_size > MAXIMUM_BYTES * 2: raise ValueError('Snapshot file exceeds size limit.')
            snapshot = strict_json(args.input.read_text()); restore(snapshot, args.key)
        print(json.dumps({'status': 'passed', 'action': args.action, 'orders': len(snapshot['ledger']['orders'])})); return 0
    except (OSError, ValueError, KeyError, TypeError, RuntimeError) as error:
        print(json.dumps({'status': 'failed', 'action': args.action, 'error_type': type(error).__name__})); return 1

if __name__ == '__main__':
    raise SystemExit(main())

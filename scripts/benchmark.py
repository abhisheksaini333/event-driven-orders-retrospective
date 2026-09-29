#!/usr/bin/env python3
"""Repeatable acceptance and completion baseline; synthetic data, no retries hidden from results."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
from pathlib import Path
import platform
import statistics
import time
import uuid
from demo import API, fulfilled, ledger, request, token, until

parser = argparse.ArgumentParser()
parser.add_argument('--requests', type=int, default=100)
parser.add_argument('--concurrency', type=int, default=4)
parser.add_argument('--output', type=Path)
args = parser.parse_args()
if not 1 <= args.requests <= 1000 or not 1 <= args.concurrency <= 16:
    parser.error('Use 1..1000 requests and concurrency 1..16 for this bounded demo.')
bearer = token()
prefix = 'bench-' + uuid.uuid4().hex
initial_orders = len(ledger()['orders'])
def send(index):
    started = time.perf_counter()
    status, body = request('POST', API + '/orders', {'sku': 'SYNTHETIC-LOAD', 'quantity': 1}, bearer, f'{prefix}-{index}')
    return status, (time.perf_counter() - started) * 1000, body.get('id')
started = time.perf_counter()
with ThreadPoolExecutor(max_workers=args.concurrency) as pool:
    responses = list(pool.map(send, range(args.requests)))
accept_seconds = time.perf_counter() - started
accepted = [r for r in responses if r[0] == 202]
for _, _, order_id in accepted:
    until(lambda: fulfilled(bearer, order_id), timeout=120)
completion_seconds = time.perf_counter() - started
latencies = sorted(r[1] for r in responses)
result = {'recorded_at': datetime.now(timezone.utc).isoformat(), 'host': platform.platform(), 'concurrency': args.concurrency,
          'requests': args.requests, 'initial_ledger_orders': initial_orders, 'accepted': len(accepted),
          'http_statuses': {str(code): sum(r[0] == code for r in responses) for code in sorted({r[0] for r in responses})},
          'acceptance_seconds': round(accept_seconds, 3), 'acceptance_requests_per_second': round(args.requests / accept_seconds, 2),
          'http_latency_ms': {'p50': round(statistics.median(latencies), 2), 'p95': round(latencies[max(0, int(len(latencies) * .95) - 1)], 2), 'max': round(max(latencies), 2)},
          'all_accepted_orders_observed_fulfilled_seconds': round(completion_seconds, 3), 'fulfilled': len(accepted),
          'scope': 'Local Docker Desktop; includes JWT verification, durable Redis CAS/AOF and concurrent dispatcher. No capacity or production claim.'}
print(json.dumps(result, indent=2))
if args.output:
    args.output.write_text(json.dumps(result, indent=2) + '\n')
if len(accepted) != args.requests:
    raise SystemExit(1)

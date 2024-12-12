#!/usr/bin/env python3
"""Record acceptance, transport failures and completion without hiding retries."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import platform
import statistics
import time
import uuid
from demo import API, fulfilled, ledger, request, token, until

def percentile(values, fraction):
    if type(fraction) not in (int,float) or not math.isfinite(fraction) or not 0 <= fraction <= 1 or any(type(value) not in (int,float) or not math.isfinite(value) or value < 0 for value in values):
        raise ValueError("percentile requires finite nonnegative observations and a fraction in [0,1]")
    return round(sorted(values)[max(0, math.ceil(len(values) * fraction) - 1)], 2) if values else None

def run_baseline(requests, concurrency):
    if type(requests) is not int or not 1 <= requests <= 1000 or type(concurrency) is not int or not 1 <= concurrency <= 16:
        raise ValueError("use 1..1000 requests and concurrency 1..16")
    bearer = token(); initial_orders = len(ledger()['orders']); prefix = 'bench-' + uuid.uuid4().hex
    def send(index):
        started = time.perf_counter()
        try:
            status, body = request('POST', API + '/orders', {'sku': 'SYNTHETIC-LOAD', 'quantity': 1}, bearer, f'{prefix}-{index}')
            order_id = body.get('id') if isinstance(body, dict) else None
            if status == 202 and (not isinstance(order_id, str) or len(order_id) != 32):
                return status, (time.perf_counter() - started) * 1000, None, 'InvalidAcceptedResponse', started
            return status, (time.perf_counter() - started) * 1000, order_id, None, started
        except Exception as error:
            return 0, (time.perf_counter() - started) * 1000, None, type(error).__name__, started
    started = time.perf_counter()
    with ThreadPoolExecutor(max_workers=concurrency) as pool:
        responses = list(pool.map(send, range(requests)))
    accept_seconds = time.perf_counter() - started
    accepted = [item for item in responses if item[0] == 202 and item[2] and item[3] is None]
    deadline = time.monotonic() + 120
    def complete(item):
        try:
            until(lambda: fulfilled(bearer, item[2]), timeout=max(0, deadline - time.monotonic()))
            return (time.perf_counter() - item[4]) * 1000, None
        except Exception as error:
            return None, type(error).__name__
    with ThreadPoolExecutor(max_workers=concurrency) as pool:
        completion = list(pool.map(complete, accepted))
    completion_latencies = [value for value, error in completion if error is None]
    completion_errors = [error for _, error in completion if error]
    completed = len(completion_latencies)
    completion_seconds = time.perf_counter() - started
    latencies = [item[1] for item in responses]
    return {'recorded_at': datetime.now(timezone.utc).isoformat(), 'host': platform.platform(), 'concurrency': concurrency,
            'requests': requests, 'initial_ledger_orders': initial_orders, 'accepted': len(accepted), 'fulfilled': completed,
            'http_statuses': {str(code): sum(item[0] == code for item in responses) for code in sorted({item[0] for item in responses})},
            'request_errors': [item[3] for item in responses if item[3]], 'completion_errors': completion_errors,
            'completion_latency_ms': {'p50': percentile(completion_latencies, .5), 'p95': percentile(completion_latencies, .95), 'max': round(max(completion_latencies), 2) if completion_latencies else None},
            'acceptance_seconds': round(accept_seconds, 3), 'acceptance_requests_per_second': round(requests / max(accept_seconds, 0.000001), 2),
            'http_latency_ms': {'p50': round(statistics.median(latencies), 2), 'p95': percentile(latencies, .95), 'max': round(max(latencies), 2)},
            'all_accepted_orders_observed_fulfilled_seconds': round(completion_seconds, 3) if completed == len(accepted) else None,
            'status': 'passed' if completed == requests else 'failed', 'scope': 'Bounded local synthetic workload; failures are included and no client retries are hidden.'}

def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument('--requests', type=int, default=100); parser.add_argument('--concurrency', type=int, default=4)
    parser.add_argument('--output', type=Path); args = parser.parse_args(argv)
    if not 1 <= args.requests <= 1000 or not 1 <= args.concurrency <= 16:
        parser.error('Use 1..1000 requests and concurrency 1..16.')
    try:
        result = run_baseline(args.requests, args.concurrency)
    except Exception as error:
        result = {'recorded_at': datetime.now(timezone.utc).isoformat(), 'status': 'failed', 'phase': 'setup', 'error_type': type(error).__name__, 'requests': args.requests}
    print(json.dumps(result, indent=2))
    if args.output:
        descriptor = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        try:
            with os.fdopen(descriptor, 'w') as stream:
                json.dump(result, stream, indent=2, allow_nan=False); stream.write('\n')
                stream.flush(); os.fsync(stream.fileno())
        except BaseException:
            args.output.unlink(missing_ok=True)
            raise
    return 0 if result['status'] == 'passed' else 1

if __name__ == '__main__':
    raise SystemExit(main())

# Operations and failure exercises

## Startup and diagnosis

Run `python3 scripts/init-local.py` once, then `docker compose up -d --build`. Wait for Keycloak realm import and Dapr components to initialize. `python3 scripts/demo.py` polls readiness. `/health/live` means the process is running; `/health/ready` confirms state-store access, not broker health or fulfilled orders.

Useful commands, scoped to this Compose project:

```sh
docker compose ps
docker compose logs --tail 80 api worker api-dapr worker-dapr
docker compose logs --tail 40 keycloak
docker compose exec -T broker redis-cli --json XINFO GROUPS orders.accepted
python3 scripts/demo.py --faults
```

Application logs record deferred outbox failure types without tokens or full request payloads. Dapr warns that actors/workflows and the scheduler are unavailable; this exercise uses neither. It also warns that standalone mTLS is disabled; local container isolation and sidecar API/callback tokens are the selected local controls.

Compose Dapr containers use their service names over the private network. Sharing the API container's network namespace caused a reproducible Docker restart failure (`namespace path ... no such file or directory`); independent container networking avoids coupling process recovery to that namespace lifetime.

## Failure matrix

| Trigger | Expected result | Recovery / proof |
|---|---|---|
| Missing, invalid or expired JWT | 401 | Obtain a valid client token; HTTP tests cover missing, malformed and expired cases |
| Valid reader token submits an order | 403 | Writer role required; signed-token and live Keycloak checks |
| Cross-identity read | 404 | Owner is the verified `sub`; live integration check |
| Invalid SKU/quantity/key | 400 | Correct payload; no new order |
| Same key with different body | 409 | Retain original request or select a new key for a new order |
| Concurrent identical requests | One 202; remaining requests replay existing order | 16 concurrent real HTTP retries plus domain concurrency test |
| Cancellation before commit | No mutation | Domain cancellation test |
| Client connection lost after commit | Client may not know result | Repeat the same key/payload; never invent a new key to resolve an unknown result |
| Broker unavailable | API can accept into durable state; outbox remains | Fault script stops broker, restarts API, verifies replay, restores broker and observes fulfillment |
| Crash after publish, before outbox removal | Event may publish again | Worker checks persisted receipt; no extra fulfillment transition |
| Worker unavailable | Broker retains queued events | Fault script stops worker, submits order, starts worker and waits for fulfillment |
| Duplicate broker event | SUCCESS; receipt unchanged | Three duplicate deliveries, consumer lag/pending drain, one receipt |
| State Redis restart | Durable state reloaded from AOF | Fault script restarts Redis and verifies prior order and request replay |
| State unavailable / CAS exhaustion | API 503; worker callback fails and Dapr retries | Retain idempotency key, restore dependency, inspect backlog; partition/long-outage campaigns are not yet run |
| Unknown event version or event/order mismatch | DROP | Domain tests; no fulfillment effect |
| Disk/volume loss | State or queued work can be lost | AOF is not a backup; independent backup/restore procedures are required |

## Recovery rules

Keep the `.env`, `.local/realm.json` and Keycloak volume together. Recreating only the import file does not rotate credentials in an existing realm: Keycloak skips importing an existing realm. Do not print bearer tokens, generated secrets, or a fully interpolated Compose config into evidence or CI logs.

`docker compose down` retains both state and broker volumes. `docker compose up -d` resumes their contents. Resetting a disposable lab is a separate destructive action: remove only this project's containers/volumes and ignored credential files when you intentionally want to discard every order, replay record, broker event and Keycloak identity. Do not run volume deletion against a shared environment.

For backlog inspection, query the Redis stream group and use authenticated order reads. The scripts inspect the synthetic ledger through `redis-cli` solely for test assertions; a real operator interface should avoid exposing owner identifiers or full business records. No real payment, inventory reservation, shipment, email or other external side effect exists here.

## Performance procedure

Run `python3 scripts/benchmark.py --requests 100 --concurrency 4 --output .local/benchmark.json`. It records starting ledger size, every response status, acceptance latency p50/p95/max, throughput and time until all accepted orders are observed fulfilled. It does not hide server failures behind client retries.

Use the same CPU/memory allocation, image versions, ledger size and request/concurrency settings when comparing runs. Existing state increases serialization work, so a later run on a larger ledger is not a like-for-like speed regression. Completion timing includes polling, and the result is a local baseline rather than a service-level objective. The configured maximum of 1,000 requests and concurrency 16 bounds accidental load.

Before considering a larger deployment, validate TLS and secret rotation, rate limiting, retention, durable dead-letter/quarantine, external-effect idempotency, backups, state migration, container vulnerabilities, sustained concurrency, and network/disk failure recovery. These are concrete extensions beyond the current synthetic exercise.

New orders include trusted UTC `acceptedAt` and `fulfilledAt` timestamps. Existing stored orders without those fields retain null timestamps; replay never invents a historical acceptance time.

The default admission limit is 10,000 orders. Existing identical requests continue to replay at that limit; new requests require capacity intervention. The check occurs inside the same CAS mutation as acceptance.

Runtime bounds are configured under `Limits`: MaximumOrders, CasAttempts, BatchSize, DispatchIntervalMs, MaximumStateBytes, ReadAttempts and MaximumReadDelayMs. Invalid or out-of-range values fail at startup. Defaults preserve the bounded local workload; increasing one limit does not establish capacity.

Write requests are limited per authenticated subject (`Limits:WritesPerMinute`, default 1,000). A 429 response includes Retry-After. The local limiter is per process; a distributed deployment needs an ingress or shared policy. Retry with the same idempotency key after the window.

The `Orders.Core` meter reports submission outcomes, CAS contention, last-observed outbox depth and oldest pending age. Unknown legacy acceptance times produce an unknown (`NaN`) age. Gauges update during dispatcher reads; they are observations, not a synchronous queue query. No owner, key or order ID is used as a metric label.

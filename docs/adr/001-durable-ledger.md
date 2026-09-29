# ADR 001: a single compare-and-swap ledger

Status: accepted for the bounded synthetic workload.

## Decision

Store orders, request fingerprints, outbox entries and fulfillment receipts under one Dapr state key, `orders-ledger-v1`. Every mutation reads a snapshot and its ETag, then writes with `concurrency: first-write` and `consistency: strong`. A conflicting write retries against a new snapshot, up to 32 attempts with bounded jitter. Exhaustion returns 503 and callers retain their original idempotency key.

`keyPrefix: none` deliberately allows the API and worker app IDs to share the same Redis key. Both Dapr components are scoped to those app IDs. On a missing key, Redis first-write version `0` creates the ledger. Tagged Redis source confirms the `first-write` marker prevents a later version-zero writer from overwriting the winner. Existing Dapr numeric ETags must be read from raw response headers because .NET's typed ETag property expects quoted HTTP entity tags. See [version/source notes](../versions.md).

## Alternatives

| Approach | Tradeoff |
|---|---|
| Publish then save an order | Crash can publish work without durable accepted state |
| Separate order, idempotency and outbox keys | Requires demonstrated transactional conditional-write semantics across all chosen state components |
| One atomic aggregate | Simple, testable atomicity; whole-document writes and global contention limit scale |
| Relational DB with unique constraints and transactional outbox | Better growth path, with a separate data model and polling/CDC implementation |

The aggregate avoids inferring rollback or conditional multi-key guarantees from Dapr's generic API. It is an explicit teaching tradeoff, not an assertion that a whole-database JSON document is an ideal production design.

## State transitions and crash points

```mermaid
sequenceDiagram
  participant C as Client
  participant A as API
  participant S as Durable ledger
  participant B as Pub/sub broker
  participant W as Worker
  C->>A: JWT, Idempotency-Key, SKU, quantity
  A->>S: CAS order + fingerprint + pending event
  S-->>A: committed
  A-->>C: 202 Accepted + Location
  A->>B: publish pending event
  Note over A,B: Crash here leaves event pending; next run republishes
  A->>S: CAS remove outbox entry
  B->>W: at-least-once event delivery
  W->>S: CAS fulfilled + receipt
  Note over W,S: Lost acknowledgment permits duplicate delivery
  W-->>B: SUCCESS, including known duplicate
```

A submitted request is uniquely scoped to `(JWT sub, Idempotency-Key)`, hashed before storage. The canonical SKU and quantity form the content fingerprint. Reusing that key and payload returns the existing order, even after a process restart. Changed content returns 409. The API returns 404 for another identity's order. A timeout after commit is ambiguous to the client; retrying the same key resolves the ambiguity safely.

The event's `eventId` must match the one persisted on the referenced order, and its version must be 1. Unrecognized or malformed events return `DROP`; transient dependency errors return non-success HTTP so Dapr redelivers. A receipt and the `fulfilled` transition share one CAS write. This produces one **recorded fulfillment effect**, not universal exactly-once delivery and not an exactly-once external payment/shipment guarantee.

## Trust boundaries

| Boundary | Control | Remaining assumption |
|---|---|---|
| Client to API | JWT signature, issuer, audience, expiry and role verification; 4 KiB body cap; field validation | Keycloak issuer and signing keys are trusted |
| Client identity to order | Persisted owner comes from `sub`, never the body; ownership checked on read | Credentials are not shared between customers |
| App to Dapr | Separate generated `DAPR_API_TOKEN`; Dapr ports are not published to the host | A local Docker administrator can inspect containers/secrets |
| Dapr to worker | Constant-time comparison of generated callback token; no public order API on worker | Possession of the callback token grants callback access |
| App state to worker event | Event ID/order ID/version checked against persisted order | State Redis remains authoritative and protected |
| Local container network | Loopback host publishing; only synthetic data; no live external integration | HTTP and unencrypted Redis are local demo settings |
| Kubernetes | HTTPS issuer and Redis TLS in exercise manifests; Dapr control-plane identity | Actual network policy, secrets, certificate trust and deployment require platform validation |

## Consequences

The durable outbox survives broker downtime because state and broker use separate Redis instances. Worker downtime accumulates events in the broker. There is no automatic dead-letter queue: malformed messages are dropped and transient failures continue retrying under the configured Redis processing/redelivery intervals. Production recovery would require durable quarantine, alerting and operator redrive policy.

The ledger grows indefinitely. Scaling horizontally increases CAS contention and serialization cost. Before extending this workload, shard aggregates or use a relational transactional outbox, define retention and tenant boundaries, and measure real failure/recovery behavior again. Only one writer service and one worker replica are configured by default.

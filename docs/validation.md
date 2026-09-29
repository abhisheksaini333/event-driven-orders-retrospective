# Validation record

Recorded 2026-09-29 on macOS arm64, Docker Desktop Engine 28.0.4, with SDK 10.0.401 and the versions pinned in Compose. Evidence contains synthetic test data and no credentials.

| Check | Evidence | Scope |
|---|---|---|
| Domain and HTTP/adapter tests | [unit-tests.txt](evidence/unit-tests.txt) | 26 passing tests: replay/restart, concurrent keys, conflict, publish failure and lost acknowledgment, cancellation, validation, ownership, JWT boundaries, ETags |
| Docker build | [Dockerfile](../Dockerfile), repeat with `docker compose up -d --build` | Linux arm64 build and actual runtime startup succeeded |
| Full-stack integration | [integration.json](evidence/integration.json) | 13 passing checks against real Keycloak, Dapr and Redis, including broker/API/worker/state restart exercises |
| Repeatable performance baseline | [benchmark.json](evidence/benchmark.json) | 100/100 accepted and fulfilled; concurrency 4; starting ledger 8 orders |
| NuGet vulnerability metadata | [dependency-audit.txt](evidence/dependency-audit.txt) | No vulnerabilities reported by configured NuGet sources for app dependencies and transitives at check time |
| Resolved container images | [image-digests.json](evidence/image-digests.json) | Digests inspected from the images used locally |
| Kubernetes rendering | [kubernetes-rendered.yaml](evidence/kubernetes-rendered.yaml) | `kubectl kustomize infra/k8s` succeeded; no cluster apply |
| Test-first evidence | [domain red](evidence/tdd-domain-red.txt), [domain green](evidence/tdd-domain-green.txt), [HTTP red](evidence/tdd-http-red.txt), [Dapr regression red](evidence/tdd-dapr-red.txt) | Failed-before-implementation domain/HTTP tests and actual integration-discovered ETag regression |

The 100-request local run measured **152.08 accepted requests/second**, **12.35 ms p50**, **100.20 ms p95**, **224.40 ms max**, and **3.786 seconds** until all accepted orders were observed fulfilled. Request acceptance took 0.658 seconds. These numbers include JWT verification, durable state writes and a concurrent outbox dispatcher. They are a baseline for this host and ledger size; they do not define capacity or a latency guarantee.

## Repeat the checks

```sh
dotnet restore --locked-mode
dotnet test --configuration Release --no-restore --nologo
dotnet list src/Orders/Orders.csproj package --vulnerable --include-transitive
docker compose config --quiet
docker compose up -d --build
python3 scripts/demo.py --faults --output .local/integration.json
python3 scripts/benchmark.py --requests 100 --concurrency 4 --output .local/benchmark.json
kubectl kustomize infra/k8s
```

The [hosted Ubuntu verification run](https://github.com/abhisheksaini333/event-driven-orders-retrospective/actions/runs/36570170827) passed at commit `67aac5c`: locked restore, 26 tests, dependency audit, Docker build, 13 fault-injection checks and 40/40 accepted and fulfilled orders. Its runner measurements are separate from the local benchmark above.

## Checks still outside this record

- Kubernetes deployment and Argo CD synchronization, issuer/TLS/network-policy/secret-manager integration.
- Container image vulnerability scanning and a full application security assessment; NuGet metadata alone does not cover those.
- Sustained/large-ledger load, multiple worker replicas, network partitions, machine power loss or independent backup restore.
- Real payments, inventory reservations or shipments; fulfillment is only the atomic synthetic state transition.
- A process kill precisely between publication and outbox removal. The lost-publication-acknowledgment unit test exercises the resulting durable state and duplicate replay; broker/API/worker restart behavior is additionally exercised live.

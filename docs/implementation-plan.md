# Orders retrospective implementation plan

Implementation plan for the synthetic order-processing exercise.

The approved scope is a synthetic ASP.NET Core orders API and worker, Dapr Redis state/pubsub, Keycloak OIDC, durable retry and idempotency, local Docker execution, evidence, and reviewable Kubernetes/Argo CD exercises.

1. Write and run failing domain tests for idempotent submission, conflicting reuse, duplicate delivery, restart, cancellation, and publication failure. Implement a compare-and-swap ledger containing orders, request fingerprints, outbox entries and receipts.
2. Write failing HTTP tests for missing/invalid authentication, insufficient permission, ownership and malformed input. Implement JWT bearer verification, policies, validation and worker callback authentication.
3. Add the Dapr HTTP adapter, selected Redis components, generated local credentials and Docker Compose. Verify actual tagged Dapr source semantics before choosing first-write ETags and shared key prefix.
4. Run the real Keycloak/Dapr/Redis stack, inject failure/restart/duplicate delivery, record output. Add a repeatable load baseline without treating laptop results as capacity guarantees.
5. Add API contract, trust-boundary ADR, recovery runbook, provenance and historical-version manifest. Validate manifests and CI commands; distinguish cluster exercises from deployed evidence.

Modern .NET 10 compatibility runtime is chosen because .NET 6 is out of support. The API uses the minimal-host style introduced with .NET 6. Historical pins are reference material, not runtime security guidance.

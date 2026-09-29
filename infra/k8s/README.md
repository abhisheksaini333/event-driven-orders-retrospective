# Kubernetes and Argo CD exercise

These manifests are rendered locally; cluster execution is not yet validated. Docker Compose is the fully runnable local path. This directory deliberately expects existing platform dependencies and credentials; it does not provision infrastructure.

Before an isolated cluster exercise, install Dapr 1.18.4 with its control-plane mTLS, make the built application image available to the cluster, and provide TLS Redis instances (state and broker), an HTTPS Keycloak issuer with the same `orders-api` audience and `roles` mappings, persistent storage and a backup policy.

Required namespace resources:

| Resource | Required fields |
|---|---|
| ConfigMap `orders-platform` | `oidc-authority`: HTTPS realm URL reachable from pods |
| Secret `orders-sidecar-api-token` | `token`: random token shared by app and sidecar |
| Secret `orders-worker-callback-token` | `token`: random worker callback token |
| Secret `orders-platform-redis` | `state-host`, `state-password`, `broker-host`, `broker-password` |

Render without contacting a cluster: `kubectl kustomize infra/k8s`. Review rendered objects, resource limits, security context, issuer DNS/certificates, image digest, Dapr secret access and namespace policy before applying in your own sandbox. Configure network policy to permit only the API ingress, pod sidecars, Dapr control plane, DNS, Keycloak and Redis. Network-policy and secret-manager integration are intentionally platform-specific and not provided as a false universal policy.

`application.example.yaml` is excluded from Kustomize. Replace its placeholder repository and revision with reviewed values. It has no automated sync, prune or self-heal. Use Argo CD's manual diff/sync in your sandbox to observe drift and rollback to a previously validated immutable application image. Do not use historical Argo CD 2.1.7 on a network; that pin only establishes the 2021 design context.

# Runtime and component versions

| Component | Executed local version | Reference version |
|---|---|---|
| .NET SDK / runtime | 10.0.401 / 10.0.11 | 6.0.100 / 6.0.0 |
| Dapr | 1.18.4 | 1.5.0 |
| Keycloak | 26.7.4 | 15.0.2 |
| Redis | 7.4.5-alpine | — |
| Argo CD | Not executed; manifest exercise only | 2.1.7 |

The executable targets .NET 10 and uses ASP.NET Core minimal hosting. The SDK is pinned in `global.json`, dependencies are locked in `packages.lock.json`, and container tags are explicit. .NET 6 is no longer supported; the earlier pins in [versions.json](../infra/historical-2021/versions.json) are comparison inputs rather than executable deployment settings. The current code also uses newer C# syntax and is not asserted to compile unchanged with the earlier SDK. [Microsoft .NET download](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Component semantics inspected

The selected Dapr implementation was checked against tagged source before configuring it:

- [Redis state, v1.18.4](https://github.com/dapr/components-contrib/blob/v1.18.4/state/redis/redis.go): numeric versions, first-write marker and ETag checks. This application intentionally does not rely on Redis multi-key transaction rollback.
- [Dapr state prefix handling, v1.18.4](https://github.com/dapr/dapr/blob/v1.18.4/pkg/components/state/state_config.go): the `none` prefix strategy is necessary for both app IDs to share the ledger.
- [Redis pub/sub, v1.18.4](https://github.com/dapr/components-contrib/blob/v1.18.4/pubsub/redis/redis.go): consumer group behavior and processing/redelivery metadata.
- [Dapr runtime v1.18.4](https://github.com/dapr/dapr/releases/tag/v1.18.4) and [Keycloak 26.7.4](https://github.com/keycloak/keycloak/releases/tag/26.7.4): selected release identifiers.

These checks establish the selected component behavior; substituting a state store or a Dapr version requires new concurrency and failure tests. The real integration run caught the unquoted numeric ETag/.NET parser mismatch, now covered by `DaprAdapterTests.Reads_unquoted_Dapr_numeric_ETag`.

## Reference release dates

| Release | Published | Official source |
|---|---|---|
| .NET 6 | 2021-11-08 | [Microsoft announcement](https://devblogs.microsoft.com/dotnet/announcing-net-6/) |
| Dapr 1.5.0 | 2021-11-11 | [Dapr release](https://github.com/dapr/dapr/releases/tag/v1.5.0) |
| Keycloak 15.0.2 | 2021-08-20 | [Keycloak release](https://github.com/keycloak/keycloak/releases/tag/15.0.2) |
| Argo CD 2.1.7 | 2021-11-17 | [Argo CD release](https://github.com/argoproj/argo-cd/releases/tag/v2.1.7) |

Application source, configuration and documentation are MIT licensed. Dapr, .NET, Keycloak, Redis and Argo CD have independent upstream licensing; no vendor source has been copied into this repository. Container image tags are version-pinned; the locally resolved image digests are recorded in [image-digests.json](evidence/image-digests.json). The local app digest describes the tested build and is not a published registry image.

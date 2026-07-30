---
layout: default
title: Scaling
---

# Scaling

Authagonal is designed to scale both vertically and horizontally with no special configuration.

## Stateless by design

All persistent state is stored in the backing store — Azure Table Storage, DynamoDB on the AWS backend, or PostgreSQL on the self-hosted SQL backend. There is no in-process state that requires sticky sessions or coordination between instances:

- **Signing keys**: loaded from Table Storage, refreshed hourly
- **Authorization codes and refresh tokens**: stored in Table Storage with single-use enforcement
- **SAML replay prevention**: request IDs tracked in Table Storage with atomic delete
- **OIDC state and PKCE verifiers**: stored in Table Storage
- **Client and provider configuration**: fetched per-request from Table Storage

## Cookie encryption (data protection)

ASP.NET Core's Data Protection keys are automatically persisted to Azure Blob Storage when using a real Azure Storage connection string. This means cookies signed by one instance can be decrypted by any other instance, no sticky sessions required.

For local development with Azurite, data protection keys fall back to the default file-based store.

You can also point to an explicit blob URI via configuration (the managed-identity path, preferred in production):

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

On the AWS backend, pass an S3 client + bucket to `AddAuthagonalAwsStorage` to persist the key ring to S3, without it the key ring is in-memory and cookies break on restart and across nodes. See [Installation → AWS backend](installation#aws-backend).

## Per-instance caches

A small number of read-heavy, slow-changing values are cached in memory per instance to reduce Table Storage round-trips:

| Data | Cache duration | Impact of staleness |
|---|---|---|
| OIDC discovery documents | 60 minutes (configurable) | Delayed awareness of IdP key rotation |
| SAML IdP metadata | 60 minutes (configurable) | Same |
| CORS allowed origins | 60 minutes (configurable) | New origins take up to an hour to propagate |

These caches are acceptable for production use. All durations are configurable via the `Cache` configuration section, see [Configuration](configuration). If you need immediate propagation, restart the affected instances.

## Rate limiting

Abuse-prone endpoints (registration per IP, password reset per target email, SCIM per client, dynamic client registration per IP, see [Configuration → Rate Limiting](configuration#rate-limiting)) are protected by a built-in rate limiter.

Limits are enforced **in-process per node** behind the `IRateLimiter` seam, so with N instances the effective ceiling is N× the configured value. That's deliberate: the limiter is a backstop against runaway abuse of a single node, and the authoritative global limit belongs at the edge (WAF / ingress / CDN), which sees all traffic before it's load-balanced.

## Clustering

Multiple instances coordinate through a **leader election** and a **cross-node event bus**, both behind pluggable backends:

- **Leader election**: a lease-based election (`Cluster:LeaseTtlSeconds`, default 30s, renewed at roughly half that interval). Exactly one node holds the lease; leadership transfers automatically when the leader dies. Leader-gated work, currently signing key rotation (when enabled), runs only on the leader to avoid concurrent key generation.
- **Event bus**: cross-node notifications (e.g. cache invalidation in multi-tenant hosts), polled every `Cluster:PollIntervalSeconds` (default 3s).

Each instance generates a random 12-hex-char node ID at startup to identify itself; it is not persisted.

### Backends

The **default is in-process**: a single node is always its own leader, and events are local-only, correct for one instance with zero configuration. Multi-node deployments swap in a real backend via the `configureClustering` callback on `AddAuthagonal`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: leadership + event bus via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// PostgreSQL: leadership via a conditional-upsert lease row, event bus via an
// append-only log in the same database (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` register the event bus only, keeping the in-process (always-leader) lease, use them on nodes that must receive cluster events but must never contend for leadership.

SQLite is the exception to all of this: it serializes writers, so a SQLite deployment is one process by construction and the in-process defaults are already correct. Horizontal scaling on the self-hosted SQL backend means PostgreSQL.

> **Note:** with the in-process default on multiple nodes, *every* node believes it is the leader. That's harmless for most workloads, but enable a real lease backend before turning on `Auth:KeyRotationEnabled` across multiple instances.

See the [Configuration](configuration#cluster) page for all cluster settings.

### Multi-tenant deployments

In multi-tenant mode (`AddAuthagonalCore()`), no background services are registered, `TokenCleanupService`, `GrantReconciliationService`, `SigningKeyRotationService`, and the config seed services are all part of the single-tenant `AddAuthagonal()` composition. The host manages these per-tenant.

## Name-index hot partition

Admin name-prefix search is backed by the `UserFirstNames` / `UserLastNames` index tables, which use a **single hot partition**. At scale this caps index-write throughput at roughly 2,000 ops/sec, which can become a bottleneck on user create/update under heavy load. If you don't expose admin name search, set `Storage:NameIndexesEnabled = false` to skip these writes entirely. See [Configuration](configuration).

## Trusted-proxy and internal endpoints

When running multiple instances behind a load balancer:

- **Forwarded headers**: rate limiting and lockout key on the client IP, resolved from `X-Forwarded-For`. Set `ForwardedHeaders:KnownNetworks` to your ingress / pod CIDR so the client IP can't be spoofed across instances. `ForwardedHeaders:ForwardLimit` defaults to `1`. See [Configuration](configuration#forwarded-headers-trusted-proxy).
- **Internal endpoints**: `/_internal/backchannel-logout` is guarded by source IP (loopback / private only) unless `Cluster:Secret` is set, in which case callers must present the secret in the `X-Cluster-Secret` header (compared in constant time). Set the secret whenever internal traffic is routed through anything that rewrites the source IP.

## Scaling recommendations

**Vertical scaling**: increase CPU and memory on a single instance. Useful for handling more concurrent requests per instance.

**Horizontal scaling**: run multiple instances behind a load balancer. No sticky sessions or shared caches required. Each instance is fully independent.

**Scale to zero**: Authagonal supports scale-to-zero deployments (e.g., Azure Container Apps with `minReplicas: 0`). The first request after idle will have a cold start of a few seconds while the .NET runtime initializes and signing keys are loaded from storage.

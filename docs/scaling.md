---
layout: default
title: Scaling
---

# Scaling

Authagonal is designed to scale both vertically and horizontally with no special configuration.

## Stateless by design

All persistent state is stored in Azure Table Storage. There is no in-process state that requires sticky sessions or coordination between instances:

- **Signing keys** — loaded from Table Storage, refreshed hourly
- **Authorization codes and refresh tokens** — stored in Table Storage with single-use enforcement
- **SAML replay prevention** — request IDs tracked in Table Storage with atomic delete
- **OIDC state and PKCE verifiers** — stored in Table Storage
- **Client and provider configuration** — fetched per-request from Table Storage

## Cookie encryption (data protection)

ASP.NET Core's Data Protection keys are automatically persisted to Azure Blob Storage when using a real Azure Storage connection string. This means cookies signed by one instance can be decrypted by any other instance — no sticky sessions required.

For local development with Azurite, data protection keys fall back to the default file-based store.

You can also point to an explicit blob URI via configuration:

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

## Per-instance caches

A small number of read-heavy, slow-changing values are cached in memory per instance to reduce Table Storage round-trips:

| Data | Cache duration | Impact of staleness |
|---|---|---|
| OIDC discovery documents | 60 minutes (configurable) | Delayed awareness of IdP key rotation |
| SAML IdP metadata | 60 minutes (configurable) | Same |
| CORS allowed origins | 60 minutes (configurable) | New origins take up to an hour to propagate |

These caches are acceptable for production use. All durations are configurable via the `Cache` configuration section — see [Configuration](configuration). If you need immediate propagation, restart the affected instances.

## Rate limiting

Registration endpoints are protected by a built-in distributed rate limiter (5 registrations per IP per hour). When running multiple instances, rate limit counts are automatically shared between all instances via a gossip protocol — no external coordination required.

### How it works

Each instance maintains its own counters in memory using a CRDT G-Counter. Instances discover each other via UDP multicast and exchange state over HTTP every few seconds. The consolidated count across all instances is used to make rate limiting decisions.

This means rate limits are enforced globally: if a client hits 3 different instances, all 3 know the total is 3, not 1 each.

### Cluster configuration

Clustering is **enabled by default** with zero configuration. Instances on the same network discover each other automatically via UDP multicast (`239.42.42.42:19847`).

For environments where multicast is unavailable (some cloud VPCs), configure a load-balanced internal URL as a fallback:

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

To disable clustering entirely (local-only rate limiting):

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

See the [Configuration](configuration) page for all cluster settings.

### Graceful degradation

- **No peers found** — works as a local-only rate limiter (each instance enforces its own limit)
- **Peer unreachable** — that peer's last-known state is still used; stale peers are pruned after 30 seconds
- **Multicast unavailable** — discovery fails silently; gossip falls back to `InternalUrl` if configured

## Scaling recommendations

**Vertical scaling** — increase CPU and memory on a single instance. Useful for handling more concurrent requests per instance.

**Horizontal scaling** — run multiple instances behind a load balancer. No sticky sessions or shared caches required. Each instance is fully independent.

**Scale to zero** — Authagonal supports scale-to-zero deployments (e.g., Azure Container Apps with `minReplicas: 0`). The first request after idle will have a cold start of a few seconds while the .NET runtime initializes and signing keys are loaded from storage.

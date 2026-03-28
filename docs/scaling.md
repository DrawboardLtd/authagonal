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
| OIDC discovery documents | 60 minutes | Delayed awareness of IdP key rotation |
| SAML IdP metadata | 60 minutes | Same |
| CORS allowed origins | 60 minutes | New origins take up to an hour to propagate |

These caches are acceptable for production use. If you need immediate propagation, restart the affected instances.

## Rate limiting

Authagonal does not include built-in rate limiting. Rate limiting should be applied at the infrastructure layer (load balancer, API gateway, or reverse proxy) where it has a unified view of all traffic across instances.

## Scaling recommendations

**Vertical scaling** — increase CPU and memory on a single instance. Useful for handling more concurrent requests per instance.

**Horizontal scaling** — run multiple instances behind a load balancer. No sticky sessions or shared caches required. Each instance is fully independent.

**Scale to zero** — Authagonal supports scale-to-zero deployments (e.g., Azure Container Apps with `minReplicas: 0`). The first request after idle will have a cold start of a few seconds while the .NET runtime initializes and signing keys are loaded from storage.

---
layout: default
title: Configuration
---

# Configuration

Authagonal is configured via `appsettings.json` or environment variables. Environment variables use `__` as the section separator (e.g., `Storage__ConnectionString`).

## Required Settings

| Setting | Env Variable | Description |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Azure Table Storage connection string |
| `Issuer` | `Issuer` | The public base URL of this server (e.g., `https://auth.example.com`) |

## Authentication

| Setting | Default | Description |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Cookie session lifetime (sliding) |
| `Auth:MaxFailedAttempts` | `5` | Failed login attempts before account lockout |
| `Auth:LockoutDurationMinutes` | `10` | Account lockout duration after max failed attempts |
| `Auth:MaxRegistrationsPerIp` | `5` | Maximum registrations per IP address within the window |
| `Auth:RegistrationWindowMinutes` | `60` | Registration rate limiting window |
| `Auth:EmailVerificationExpiryHours` | `24` | Email verification link lifetime |
| `Auth:PasswordResetExpiryMinutes` | `60` | Password reset link lifetime |
| `Auth:MfaChallengeExpiryMinutes` | `5` | MFA challenge token lifetime |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | MFA setup token lifetime (for forced enrollment) |
| `Auth:Pbkdf2Iterations` | `100000` | PBKDF2 iteration count for password hashing |
| `Auth:RefreshTokenReuseGraceSeconds` | `60` | Grace window for concurrent refresh token reuse |
| `Auth:SigningKeyLifetimeDays` | `90` | RSA signing key lifetime before automatic rotation |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | How often signing keys are reloaded from storage |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Interval between cookie security stamp checks |

## Cache and Timeouts

| Setting | Default | Description |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | How long CORS allowed origins are cached |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | OIDC discovery document cache duration |
| `Cache:SamlMetadataCacheMinutes` | `60` | SAML IdP metadata cache duration |
| `Cache:OidcStateLifetimeMinutes` | `10` | OIDC authorization state parameter lifetime |
| `Cache:SamlReplayLifetimeMinutes` | `10` | SAML AuthnRequest ID lifetime (replay prevention) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Table Storage health check timeout |

## Background Services

| Setting | Default | Description |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Initial delay before first expired token cleanup |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Expired token cleanup interval |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Initial delay before first grant reconciliation |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Grant reconciliation interval |

## Clients

Clients are defined in the `Clients` array and seeded on startup. Each client can have:

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientName": "My Application",
      "ClientSecretHashes": ["sha256-hash-here"],
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email", "custom-scope"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "AlwaysIncludeUserClaimsInIdToken": false,
      "AccessTokenLifetimeSeconds": 1800,
      "IdentityTokenLifetimeSeconds": 300,
      "AuthorizationCodeLifetimeSeconds": 300,
      "AbsoluteRefreshTokenLifetimeSeconds": 2592000,
      "SlidingRefreshTokenLifetimeSeconds": 1296000,
      "RefreshTokenUsage": "OneTime",
      "MfaPolicy": "Enabled",
      "ProvisioningApps": ["my-backend"]
    }
  ]
}
```

### Grant Types

| Grant Type | Use Case |
|---|---|
| `authorization_code` | Interactive user login (web apps, SPAs, mobile) |
| `client_credentials` | Service-to-service communication |
| `refresh_token` | Token renewal (requires `AllowOfflineAccess: true`) |

### Refresh Token Usage

| Value | Behavior |
|---|---|
| `OneTime` (default) | Each refresh issues a new refresh token. Old one is invalidated with a 60-second grace window for concurrent requests. Replay after the grace window revokes all tokens for that user+client. |
| `ReUse` | Same refresh token is reused until expiry. |

### Provisioning Apps

The `ProvisioningApps` array references app IDs defined in the `ProvisioningApps` configuration section. When a user authorizes through this client, they are provisioned into those apps via TCC. See [Provisioning](provisioning) for details.

## Provisioning Apps

Define downstream applications that users should be provisioned into:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-api-key"
    },
    "analytics": {
      "CallbackUrl": "https://analytics.example.com/provisioning",
      "ApiKey": "another-key"
    }
  }
}
```

See [Provisioning](provisioning) for the full TCC protocol specification.

## MFA Policy

Multi-factor authentication is enforced per-client via the `MfaPolicy` property:

| Value | Behavior |
|---|---|
| `Disabled` (default) | No MFA challenge, even if the user has MFA enrolled |
| `Enabled` | Challenge users who have MFA enrolled; don't force enrollment |
| `Required` | Challenge enrolled users; force enrollment for users without MFA |

```json
{
  "Clients": [
    {
      "ClientId": "secure-app",
      "MfaPolicy": "Required"
    }
  ]
}
```

When `MfaPolicy` is `Required` and the user hasn't enrolled MFA, login returns `{ mfaSetupRequired: true, setupToken: "..." }`. The setup token authenticates the user to the MFA setup endpoints (via `X-MFA-Setup-Token` header) so they can enroll before getting a cookie session.

Federated logins (SAML/OIDC) skip MFA — the external identity provider handles it.

### IAuthHook Override

The `IAuthHook.ResolveMfaPolicyAsync` method can override the client policy per-user:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

## Password Policy

Customize password strength requirements:

```json
{
  "PasswordPolicy": {
    "MinLength": 10,
    "MinUniqueChars": 3,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": false
  }
}
```

| Property | Default | Description |
|---|---|---|
| `MinLength` | `8` | Minimum password length |
| `MinUniqueChars` | `2` | Minimum number of distinct characters |
| `RequireUppercase` | `true` | Require at least one uppercase letter |
| `RequireLowercase` | `true` | Require at least one lowercase letter |
| `RequireDigit` | `true` | Require at least one digit |
| `RequireSpecialChar` | `true` | Require at least one non-alphanumeric character |

The policy is enforced on password reset and admin user registration. The login UI fetches the active policy from `GET /api/auth/password-policy` to display requirements dynamically.

## SAML Providers

Define SAML identity providers in configuration. These are seeded on startup:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com", "example.org"]
    }
  ]
}
```

| Property | Required | Description |
|---|---|---|
| `ConnectionId` | Yes | Stable identifier (used in URLs like `/saml/{connectionId}/login`) |
| `ConnectionName` | No | Display name (defaults to ConnectionId) |
| `EntityId` | Yes | SAML Service Provider entity ID |
| `MetadataLocation` | Yes | URL to the IdP's SAML metadata XML |
| `AllowedDomains` | No | Email domains routed to this provider via SSO |

## OIDC Providers

Define OIDC identity providers in configuration. These are seeded on startup:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

| Property | Required | Description |
|---|---|---|
| `ConnectionId` | Yes | Stable identifier (used in URLs like `/oidc/{connectionId}/login`) |
| `ConnectionName` | No | Display name (defaults to ConnectionId) |
| `MetadataLocation` | Yes | URL to the IdP's OpenID Connect discovery document |
| `ClientId` | Yes | OAuth2 client ID registered with the IdP |
| `ClientSecret` | Yes | OAuth2 client secret (protected via `ISecretProvider` at startup) |
| `RedirectUrl` | Yes | OAuth2 redirect URI registered with the IdP |
| `AllowedDomains` | No | Email domains routed to this provider via SSO |

> **Note:** Providers can also be managed at runtime via the [Admin API](admin-api). Config-seeded providers are upserted on every startup, so config changes take effect on restart.

## Secret Provider

Client secrets and OIDC provider secrets can optionally be stored in Azure Key Vault:

| Setting | Description |
|---|---|
| `SecretProvider:VaultUri` | Key Vault URI (e.g., `https://my-vault.vault.azure.net/`). If not set, secrets are treated as plaintext. |

When configured, secret values that look like Key Vault references are resolved at runtime. Uses `DefaultAzureCredential` for authentication.

## Admin API

| Setting | Default | Description |
|---|---|---|
| `AdminApi:Enabled` | `true` | Set to `false` to disable all admin endpoints (they won't be registered) |
| `AdminApi:Scope` | `authagonal-admin` | JWT scope required to access admin endpoints. Change this to match your existing scope name (e.g., `projects-identity-admin` for IdentityServer migrations). |

## Email

By default, Authagonal uses a no-op email service that silently discards all emails. To enable email delivery, register an `IEmailService` implementation before calling `AddAuthagonal()`.

The built-in `EmailService` uses SendGrid. To use it, register it explicitly:

```csharp
services.AddSingleton<IEmailService, EmailService>();
services.AddAuthagonal(configuration);
```

| Setting | Description |
|---|---|
| `Email:SendGridApiKey` | SendGrid API key for sending emails |
| `Email:SenderEmail` | Sender email address |
| `Email:SenderName` | Sender display name |
| `Email:VerificationTemplateId` | SendGrid dynamic template ID for email verification |
| `Email:PasswordResetTemplateId` | SendGrid dynamic template ID for password reset |

Emails to `@example.com` addresses are silently skipped (useful for testing).

## Cluster

Authagonal instances automatically form a cluster to share rate limit state. Clustering is enabled by default with zero configuration.

| Setting | Env Variable | Default | Description |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Master switch for clustering. Set to `false` for local-only rate limiting. |
| `Cluster:MulticastGroup` | `Cluster__MulticastGroup` | `239.42.42.42` | UDP multicast group for peer discovery |
| `Cluster:MulticastPort` | `Cluster__MulticastPort` | `19847` | UDP multicast port for peer discovery |
| `Cluster:InternalUrl` | `Cluster__InternalUrl` | *(none)* | Load-balanced fallback URL for gossip when multicast is unavailable |
| `Cluster:Secret` | `Cluster__Secret` | *(none)* | Shared secret for gossip endpoint authentication (recommended when `InternalUrl` is set) |
| `Cluster:GossipIntervalSeconds` | `Cluster__GossipIntervalSeconds` | `5` | How often instances exchange rate limit state |
| `Cluster:DiscoveryIntervalSeconds` | `Cluster__DiscoveryIntervalSeconds` | `10` | How often instances announce themselves via multicast |
| `Cluster:PeerStaleAfterSeconds` | `Cluster__PeerStaleAfterSeconds` | `30` | Drop peers not heard from after this many seconds |

**Zero-config (default):** Instances discover each other via UDP multicast. Works in Kubernetes, Docker Compose, or any shared network.

**Multicast disabled (e.g., some cloud VPCs):**

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

**Clustering fully disabled:**

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

See [Scaling](scaling) for more details on how distributed rate limiting works.

## Rate Limiting

Built-in per-IP rate limits are enforced across all instances via the cluster gossip protocol:

| Endpoint | Limit | Window |
|---|---|---|
| `POST /api/auth/register` | 5 registrations | 1 hour |

When clustering is enabled, these limits are consolidated across all instances. When disabled, each instance enforces its own limit independently.

## CORS

CORS is configured dynamically. Origins from all registered clients' `AllowedCorsOrigins` are automatically allowed, with a 60-minute cache.

## Full Example

```json
{
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..."
  },
  "Issuer": "https://auth.example.com",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "SigningKeyLifetimeDays": 90
  },
  "Cluster": {
    "Enabled": true
  },
  "AdminApi": {
    "Enabled": true,
    "Scope": "authagonal-admin"
  },
  "Authentication": {
    "CookieLifetimeHours": 48
  },
  "PasswordPolicy": {
    "MinLength": 8,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": true
  },
  "Email": {
    "SendGridApiKey": "SG.xxx",
    "FromAddress": "noreply@example.com",
    "FromName": "Example Auth",
    "VerificationTemplateId": "d-xxx",
    "PasswordResetTemplateId": "d-yyy"
  },
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com"]
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "...",
      "ClientSecret": "...",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["gmail.com"]
    }
  ],
  "ProvisioningApps": {
    "backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret"
    }
  },
  "Clients": [
    {
      "ClientId": "web",
      "ClientName": "Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "MfaPolicy": "Enabled",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```

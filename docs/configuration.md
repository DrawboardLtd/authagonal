---
layout: default
title: Configuration
---

# Configuration

Authagonal is configured via `appsettings.json` or environment variables. Environment variables use `__` as the section separator (e.g., `Storage__ConnectionString`).

## Required Settings

Storage can be configured one of two ways, supply **either** `Storage:ConnectionString` **or** `Storage:TableServiceUri` (the managed-identity path, preferred in production).

| Setting | Env Variable | Description |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Azure Table Storage connection string with an account key. Suitable for dev / Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Managed-identity Table Storage endpoint, e.g. `https://{account}.table.core.windows.net/`. Alternative to `Storage:ConnectionString` and **preferred in production**: authenticates via `DefaultAzureCredential` so no access key ever lands in a secret. The host must grant the workload identity the **Storage Table Data Contributor** role. |
| `Issuer` | `Issuer` | The public base URL of this server (e.g., `https://auth.example.com`) |

## Storage

| Setting | Env Variable | Default | Description |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(none)* | Connection string with account key (see Required Settings). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(none)* | Managed-identity Table Storage URI (see Required Settings). Takes precedence over `Storage:ConnectionString` when both are set. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Whether to maintain the `UserFirstNames` / `UserLastNames` prefix-search index tables that back admin name-prefix search. Set `false` on hosts that don't expose admin name search to skip those writes. **Scaling note:** these indexes use a single hot partition and cap throughput at roughly 2,000 ops/sec at scale, disable them if you don't need name search. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | Base URL the `/connect/authorize` endpoint redirects to for the login SPA (login, step-up, and consent screens). Set this when the login UI is served from a different origin than the server; defaults to the relative `/login` path served by the bundled SPA. |

## Authentication

| Setting | Default | Description |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Cookie session lifetime (sliding) |
| `Authentication:AlwaysSecureCookie` | `false` | Force the session cookie's `Secure` flag unconditionally. The default (`SameAsRequest`) already yields a Secure cookie behind a TLS-terminating proxy that forwards `X-Forwarded-Proto: https`. |
| `Auth:MaxFailedAttempts` | `5` | Failed login attempts before account lockout |
| `Auth:LockoutDurationMinutes` | `10` | Account lockout duration after max failed attempts |
| `Auth:MaxRegistrationsPerIp` | `5` | Maximum registrations per IP address within the window |
| `Auth:RegistrationWindowMinutes` | `60` | Registration rate limiting window |
| `Auth:MaxPasswordResetsPerEmail` | `3` | Maximum password-reset emails per target address within the window (keyed on the email, not the caller IP, so one address can't be email-bombed) |
| `Auth:PasswordResetWindowMinutes` | `60` | Password-reset rate limiting window |
| `Auth:AutoConfirmEmailDomains` | *(empty)* | Email domains (string array) whose self-service registrations are auto-confirmed, they skip the verification email. Empty (the default) means every registration must verify. Intended only for dev/test; never list a domain that can receive real mail. |
| `Auth:EmailVerificationExpiryHours` | `24` | Email verification link lifetime |
| `Auth:PasswordResetExpiryMinutes` | `60` | Password reset link lifetime |
| `Auth:MfaChallengeExpiryMinutes` | `5` | MFA challenge token lifetime |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | MFA setup token lifetime (for forced enrollment) |
| `Auth:Pbkdf2Iterations` | `100000` | PBKDF2 iteration count for password hashing |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Opt-in grace window (seconds) for concurrent refresh token reuse. `0` (default) keeps the strict posture: any reuse of a consumed refresh token revokes all tokens for that user+client. Set `> 0` to treat a reuse within the window as an idempotent retry (re-delivers the successor tokens), useful for mobile clients with connectivity flaps. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Enable the `POST /connect/register` dynamic client registration endpoint (RFC 7591). Off by default because open registration can be abused in multi-tenant deployments. See [Dynamic Client Registration](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | RSA signing key lifetime before automatic rotation |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | How often signing keys are reloaded from storage |
| `Auth:KeyRotationEnabled` | `false` | Enable automatic signing key rotation |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | How often to check if the active key needs rotation |
| `Auth:KeyRotationLeadTimeDays` | `14` | Rotate when the active key expires within this many days |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Interval between cookie security stamp checks |

## Data Protection

ASP.NET Core Data Protection keys (which encrypt the session cookie) must be shared across instances, see [Scaling](scaling#cookie-encryption-data-protection). Persistence options, in precedence order:

| Setting | Default | Description |
|---|---|---|
| `DataProtection:BlobUri` | *(none)* | Explicit Azure Blob URI for the key ring (e.g. `https://{account}.blob.core.windows.net/dataprotection/keys.xml`). Authenticates via `DefaultAzureCredential`, the preferred production path alongside `Storage:TableServiceUri`. |
| *(fallback)* | — | When `DataProtection:BlobUri` is unset and `Storage:ConnectionString` points at a real storage account (not Azurite), keys are persisted to a `dataprotection` container in that account automatically. With Azurite, keys fall back to the default file-based store. |

On the AWS backend, pass an S3 client + bucket to `AddAuthagonalAwsStorage` to persist the key ring to S3, see [Installation → AWS backend](installation#aws-backend).

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

## Roles

Roles are defined in the `Roles` array and seeded on startup, alongside clients, scopes and
providers. Seeding them matters most when a scope is gated with
[`AllowedRoles`](scopes#role-gated-scopes): a scope gated on a role that nothing creates is gated
against everybody, including the operator who configured it, and it fails silently — the scope is
simply never granted.

```json
{
  "Roles": [
    {
      "Name": "staff-admin",
      "Description": "Internal staff console",
      "Members": [ "ada@example.com", "grace@example.com" ]
    }
  ]
}
```

| Field | Description |
|---|---|
| `Name` | The role name, as used in `Scope.AllowedRoles` and on the `roles` token claim |
| `Description` | Human-readable; updated on later boots when the seed states one |
| `Members` | Emails placed in the role on every boot. An address with no user yet is skipped with a warning and retried next boot — startup never depends on an account someone has not created |

Seeding is **additive and idempotent**. It never removes a role or revokes a membership: config is
not the system of record for who holds what, so a role granted through the admin API survives the
next restart.

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
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
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
| `urn:ietf:params:oauth:grant-type:device_code` | Device authorization grant (RFC 8628) for input-constrained devices |

### Refresh Token Usage

| Value | Behavior |
|---|---|
| `OneTime` (default) | Each refresh issues a new refresh token and invalidates the old one. By default (`Auth:RefreshTokenReuseGraceSeconds = 0`) any reuse of a consumed token immediately revokes all tokens for that user+client, there is **no** grace window on by default. Set `Auth:RefreshTokenReuseGraceSeconds` to a positive value to opt into a retry-tolerance window. |
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

Federated logins (SAML/OIDC) honour the MFA policy too: an MFA-enrolled user is routed through the MFA challenge after the external IdP authenticates them, and `Required` forces enrollment for federated users without MFA.

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
| `EntityId` | Yes | **This server's** SP entity ID, the identifier you register at the IdP, not the IdP's own entity ID |
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

Upstream OIDC client secrets and TOTP / MFA seeds can be stored in Azure Key Vault instead of in plaintext:

| Setting | Description |
|---|---|
| `SecretProvider:VaultUri` | Key Vault URI (e.g., `https://my-vault.vault.azure.net/`). If not set, the **plaintext** provider is used and secrets are stored as-is in Table Storage. |

When configured, secret values that look like Key Vault references are resolved at runtime. Uses `DefaultAzureCredential` for authentication.

> ⚠️ **Production: set `SecretProvider:VaultUri`.** The default secret provider is **plaintext**. When `SecretProvider:VaultUri` is unset, upstream OIDC client secrets and TOTP / MFA seeds are written to Azure Table Storage in cleartext, and therefore appear in cleartext in any [backup](backup-restore). For any production deployment, configure `SecretProvider:VaultUri` so these secrets are stored in Key Vault.

## Admin API

| Setting | Default | Description |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Enabled by default.** Set to `false` to disable all admin endpoints (they won't be registered). |
| `AdminApi:Scope` | `authagonal-admin` | JWT scope required to access admin endpoints. Change this to match your existing scope name (e.g., `projects-identity-admin` for IdentityServer migrations). |

> ⚠️ **The admin API is enabled by default and is highly privileged.** The admin scope grants full management and user impersonation, anyone holding a token with `AdminApi:Scope` can mint tokens for any user, manage clients, and read/write all configuration. Network-restrict the admin endpoints (the `/api/v1/*` admin routes), and tightly control who can be issued the admin scope. As a defence-in-depth measure the scope is *reserved*: it can never be granted to an OAuth client (see [Admin API](admin-api)) and cannot be issued through the impersonation endpoint. Set `AdminApi:Enabled = false` entirely if the admin API is not used.

## Consent

Per-client consent can be enabled with the `RequireConsent` property:

| Value | Behavior |
|---|---|
| `false` (default) | Authorization proceeds immediately after authentication |
| `true` | User is shown a consent screen listing requested scopes. Consent is persisted for 5 years and re-prompted only when new scopes are requested. |

Users can view and revoke their consent grants at `GET /consent/grants` and `DELETE /consent/grants/{clientId}`.

## Back-Channel Logout

Register a `BackChannelLogoutUri` on a client to receive OIDC Back-Channel Logout 1.0 notifications. When a user logs out, Authagonal sends a signed logout token (JWT) to each client's registered URI.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback"
    }
  ]
}
```

## Email

The built-in email sender uses [Resend](https://resend.com) and **activates automatically** when `Email:ResendApiKey` is configured, no service registration needed. To use a different provider, register your own `IEmailService` implementation before calling `AddAuthagonal()` (it takes precedence regardless of the `Email:*` keys).

| Setting | Description |
|---|---|
| `Email:ResendApiKey` | Resend API key. When set, the built-in Resend sender is used. |
| `Email:SenderEmail` | Sender email address |
| `Email:SenderName` | Sender display name (defaults to `"Authagonal"`) |

> ⚠️ **Without any email sender, self-registration is broken.** When `Email:ResendApiKey` is unset and no custom `IEmailService` is registered, a no-op service silently discards all mail, verification and password-reset emails never arrive, and because login requires a confirmed email by default, self-registered users can never sign in. `UseAuthagonal` logs a warning at startup in this state. Escape hatch for dev/test: `Auth:AutoConfirmEmailDomains` auto-confirms registrations for the listed domains.

Emails to `@example.com` addresses are silently skipped (useful for testing).

## Cluster

The clustering layer provides **leader election** (so leader-gated jobs like signing key rotation run on exactly one node) and a **cross-node event bus**, behind pluggable backends. The default is in-process: a single node is always its own leader, the right setting for single-node and local development, with zero configuration.

| Setting | Env Variable | Default | Description |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Master switch. When `false` the node runs standalone (always leader, in-process event bus). |
| `Cluster:Secret` | `Cluster__Secret` | *(none)* | Shared secret required on the internal-only `/_internal/backchannel-logout` endpoint. When set, callers must present it in the `X-Cluster-Secret` header (compared in constant time). When **unset**, the endpoint is reachable only from loopback / private (RFC 1918 / link-local / ULA) source IPs, an external request carrying a public IP is rejected. |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | Leadership lease duration. Renewed at roughly half this interval. |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | How often the event-bus backend polls for messages published by other nodes. |

**Multi-node deployments** swap in a real backend via the `configureClustering` callback on `AddAuthagonal` / `AddAuthagonalCore`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS equivalent (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// Self-hosted PostgreSQL (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` register the event bus only, keeping the in-process lease, for nodes that must receive cluster events but must never contend for leadership.

See [Scaling](scaling) for how leadership and the event bus behave across instances.

## Forwarded Headers (trusted proxy)

Authagonal keys rate limiting and account lockout on the client IP, and only emits HSTS on HTTPS requests. Behind a reverse proxy / ingress, the real client IP and scheme arrive in the `X-Forwarded-For` / `X-Forwarded-Proto` headers. These settings control **which proxy hops are trusted** to set those values, so a caller can't spoof `X-Forwarded-For` to forge the client IP.

| Setting | Env Variable | Default | Description |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Number of proxy hops to honour from the right of the `X-Forwarded-For` chain. The default of `1` trusts only the single hop your ingress appends and ignores anything further left in the chain. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (array) | *(empty)* | CIDR ranges (string array, e.g. `"10.0.0.0/8"`) permitted to set forwarded headers. **Strongest guarantee:** set this to your ingress / pod CIDR so only that network may set the client IP. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (array) | *(empty)* | Individual proxy IP addresses (string array) permitted to set forwarded headers. Use alongside or instead of `KnownNetworks`. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

> ⚠️ **TLS-terminating proxy required.** Authagonal must run behind a TLS-terminating reverse proxy. The session cookie uses `SecurePolicy = SameAsRequest` and HSTS (`Strict-Transport-Security`) is only emitted on HTTPS requests, so the proxy must forward `X-Forwarded-Proto: https` for cookies to be marked `Secure` and HSTS to be sent. Configure `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` to your trusted proxy so the scheme and client IP cannot be spoofed.

## Rate Limiting

Built-in rate limits protect the abuse-prone endpoints:

| Endpoint | Limit | Window | Keyed on |
|---|---|---|---|
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 hour (`Auth:RegistrationWindowMinutes`) | Client IP |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 hour (`Auth:PasswordResetWindowMinutes`) | Target email |
| `POST /connect/register` (when enabled) | 10 | 1 hour | Client IP |
| SCIM endpoints | 200 | 1 minute | SCIM client |

Limits are enforced **in-process per node** (behind the `IRateLimiter` seam), so with N instances the effective ceiling is N× the configured value. Treat these as a backstop and enforce the authoritative global limit at the edge (WAF / ingress / CDN). See [Scaling](scaling#rate-limiting).

## CORS

CORS is configured dynamically. Origins from all registered clients' `AllowedCorsOrigins` are automatically allowed, with a 60-minute cache.

## HashiCorp Vault Transit

Authagonal can sign JWTs using HashiCorp Vault's Transit secrets engine. Private keys never leave Vault, only the signing operation is delegated remotely. Public keys are cached locally for verification.

This is configured programmatically when hosting as a library. See [Extensibility](extensibility) for details.

## Full Example

```json
{
  "Storage": {
    "TableServiceUri": "https://myaccount.table.core.windows.net/",
    "NameIndexesEnabled": true
  },
  "Issuer": "https://auth.example.com",
  "LoginAppUrl": "/login",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "RefreshTokenReuseGraceSeconds": 0,
    "DynamicClientRegistrationEnabled": false,
    "SigningKeyLifetimeDays": 90
  },
  "SecretProvider": {
    "VaultUri": "https://my-vault.vault.azure.net/"
  },
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"]
  },
  "Cluster": {
    "Enabled": true,
    "Secret": "shared-secret-here"
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
    "ResendApiKey": "re_xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth"
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
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```

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
| `Auth:AllowInsecureHttp` | `false` | Let the OAuth endpoints (`/connect/*`) answer plain http requests. **Development only.** RFC 6749 §3.1/§3.2 require TLS at the authorization and token endpoints, so by default a non-https request to any of them is refused with `invalid_request`. The scheme is evaluated *after* forwarded-header processing, so a proxy that terminates TLS and forwards `X-Forwarded-Proto: https` passes the gate with this left off — provided that proxy is declared in [`ForwardedHeaders:KnownNetworks` / `KnownProxies`](#the-two-headers-are-not-trusted-on-the-same-terms), without which the header is ignored. Only a genuinely plaintext deployment (the shipped `docker-compose.yml`, the custom-server demo) needs it, and the server logs a warning at startup whenever it is on. Propagated to `AuthagonalProtocolOptions.AllowInsecureHttp`, so it also governs the endpoints owned by `Authagonal.Protocol` (see [Extensibility](extensibility#embedding-authagonalprotocol-alone)). |
| `Auth:RequireMinimumRuntime` | `false` | Refuse to start when the .NET shared framework is older than the security floor Authagonal requires (**9.0.18 / 10.0.10**). The floor exists because the fixes for GHSA-37gx-xxp4-5rgx and GHSA-w3x6-4m5h-cxqf — an infinite loop and an XXE / resource-exhaustion pair in `System.Security.Cryptography.Xml`, both reachable from the **anonymous** SAML ACS endpoint — ship in the runtime, not in a package this library can pin, so no dependency of yours can guarantee them. Left `false`, an old runtime is a `Critical` log and the server starts: refusing by default would turn a version bump of Authagonal into an outage on a fleet whose runtime is one patch behind. Set it `true` where not starting is preferable to serving unauthenticated XML on an unpatched runtime. |
| `Auth:MaxFailedAttempts` | `5` | Failed login attempts before account lockout |
| `Auth:LockoutDurationMinutes` | `10` | Account lockout duration after max failed attempts |
| `Auth:MaxRegistrationsPerIp` | `5` | Maximum registrations per IP address within the window |
| `Auth:RegistrationWindowMinutes` | `60` | Registration rate limiting window |
| `Auth:MaxPasswordResetsPerEmail` | `3` | Maximum password-reset emails per target address within the window (keyed on the email, not the caller IP, so one address can't be email-bombed) |
| `Auth:MaxPasswordResetsPerIp` | `15` | Maximum forgot-password requests per source IP within the window. The per-email cap bounds mail to one victim; this bounds a caller working through an address list, which is otherwise unbounded anonymous mail from your verified sending domain plus a store read per address. |
| `Auth:PasswordResetWindowMinutes` | `60` | Password-reset rate limiting window |
| `Auth:DurableRateLimiting` | `false` | Keep rate-limit counters in the configured store so every replica shares one budget, instead of each node keeping its own. Costs a store round trip per check; a single-node deployment gains nothing. Requires a provider that supplies `IRateLimitCounterStore` (Azure, SQL, AWS) — the host refuses to start otherwise rather than silently reverting to per-node limits. See [Cluster-wide limits](#cluster-wide-limits-authdurableratelimiting). |
| `Auth:AutoConfirmEmailDomains` | *(empty)* | Email domains (string array) whose self-service registrations are auto-confirmed, they skip the verification email. Empty (the default) means every registration must verify. Intended only for dev/test; never list a domain that can receive real mail. |
| `Auth:EmailVerificationExpiryHours` | `24` | Email verification link lifetime |
| `Auth:PasswordResetExpiryMinutes` | `60` | Password reset link lifetime |
| `Auth:MfaChallengeExpiryMinutes` | `5` | MFA challenge token lifetime |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | MFA setup token lifetime (for forced enrollment) |
| `Auth:Pbkdf2Iterations` | `100000` | PBKDF2 iteration count for password hashing |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | Wall-clock floor a failed login is held to before `invalid_credentials` is returned, measured from the start of the request. Closes the user-enumeration timing oracle: a missing account is verified against a dummy hash in the native PBKDF2 format, but a real account may still hold an imported bcrypt or ASP.NET Identity V3 hash at a different cost, so equal work is impossible and equal elapsed time is what's enforced. Raise it above the slowest hash the deployment holds, e.g. if you imported bcrypt above cost 11 or raised `Pbkdf2Iterations` well past the default — a single warning is logged the first time a failed login overruns it. `0` disables the pad and re-opens the oracle. |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Opt-in grace window (seconds) for concurrent refresh token reuse. `0` (default) keeps the strict posture: any reuse of a consumed refresh token revokes all tokens for that user+client. Set `> 0` to treat a reuse within the window as an idempotent retry (re-delivers the successor tokens), useful for mobile clients with connectivity flaps. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Enable the `POST /connect/register` dynamic client registration endpoint (RFC 7591). Off by default because open registration can be abused in multi-tenant deployments. See [Dynamic Client Registration](client-registration). |
| `Auth:DynamicClientRegistrationScopes` | *(empty)* | Scopes an anonymous registrant may assign to itself, on top of the always-registrable OIDC built-ins (`openid`, `profile`, `email`, `offline_access`). Empty means the built-ins and nothing else: a scope existing in the store is not permission for a self-registered client to declare it. Role-gated scopes are never registrable regardless. See [Dynamic Client Registration](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | RSA signing key lifetime before automatic rotation |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | How often signing keys are reloaded from storage |
| `Auth:KeyRotationEnabled` | `false` | Enable automatic signing key rotation |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | How often to check if the active key needs rotation |
| `Auth:KeyRotationLeadTimeDays` | `14` | Rotate when the active key expires within this many days |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Interval between cookie security stamp checks |
| `Auth:AllowedInternalTargets` | *(empty)* | Internal destinations Authagonal may fetch from on the paths where **you** supplied the URL: upstream SAML metadata, upstream OIDC discovery, provisioning callbacks. Empty means every internal address is refused. See [Outbound fetches](#outbound-fetches-ssrf-guard). |
| `Auth:AllowOutboundProxy` | `false` | Send those same operator-configured fetches through the ambient HTTP proxy, accepting that the address check cannot see through it. Never applies to a client-registered `jwks_uri` or back-channel logout URI. See [Outbound fetches](#outbound-fetches-ssrf-guard). |

## Data Protection

ASP.NET Core Data Protection keys (which encrypt the session cookie) must be shared across instances, see [Scaling](scaling#cookie-encryption-data-protection). Persistence options, in precedence order:

| Setting | Default | Description |
|---|---|---|
| `DataProtection:BlobUri` | *(none)* | Explicit Azure Blob URI for the key ring (e.g. `https://{account}.blob.core.windows.net/dataprotection/keys.xml`). Authenticates via `DefaultAzureCredential`, the preferred production path alongside `Storage:TableServiceUri`. |
| *(fallback)* | — | When `DataProtection:BlobUri` is unset, the key ring is persisted automatically: to a `dataprotection` container in the account named by `Storage:ConnectionString` (unless that is Azurite), or — on the managed-identity path — to the blob endpoint derived from `Storage:TableServiceUri` (`https://{account}.table.…` → `https://{account}.blob.…/dataprotection/keys.xml`), which needs Storage Blob Data Contributor on the same account. Only an unrecognised table endpoint (Azurite, path-style emulators) falls back to the per-machine file store, which is ephemeral and per-pod — `KeyRingStartupCheck` logs Critical when it does. |

On the AWS backend, pass an S3 client + bucket to `AddAuthagonalAwsStorage` to persist the key ring to S3, see [Installation → AWS backend](installation#aws-backend). On the SQL backend the key ring is persisted by `AddAuthagonalPostgres` / `AddAuthagonalSqlite`, see [Installation → SQL backend](installation#sql-backend).

Persisting is not encrypting. Whichever backend holds the ring, it is written as plaintext XML — master key included — unless one of these is set. That ring protects the authentication cookie, so a store read is the ability to forge a session for any user:

| Setting | Default | Description |
|---|---|---|
| `DataProtection:KeyVaultKeyId` | *(none)* | Azure Key Vault key URI used to wrap the key ring. Authenticates via `DefaultAzureCredential`. |
| `DataProtection:CertificateThumbprint` | *(none)* | Thumbprint of a certificate in the machine store used to wrap the key ring. |
| `DataProtection:AllowUnencryptedKeyRing` | `false` | Accepts a plaintext key ring deliberately. Restated at `Critical` on every start so it shows up in an audit rather than only in a config file. |

Startup enforces this from the *resolved* key-ring options, so it applies identically to the Azure, AWS, SQL and any host-registered repository. A deployment that persists the ring with no encryption and **no keys yet** is refused, so the insecure state is never created; one whose ring **already has keys** starts and logs at `Critical`, because refusing there would take a running deployment down on a version bump. Development never refuses.

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
      "Audiences": ["https://api.example.com"],
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

### Audiences and resource indicators (RFC 8707)

`Audiences` is the client's allowlist for the `resource` parameter (RFC 8707) and the `audience` parameter of a token exchange (RFC 8693). Whatever survives that check becomes the `aud` claim of the issued access token; with no `resource` on the request, `aud` falls back to `Audiences`, and with neither it is the `client_id`.

An empty `Audiences` list means **"none"** for any client created through a surface that accepts audiences — dynamic registration (the `audiences` metadata field), the admin API, or seed configuration. Such a client may not name a `resource` at all, on any path: authorize, `client_credentials` and token exchange agree.

The one exception is a client stored **before** this rule existed. Those rows carry `AudiencesDeclared: false`, and for them an empty list still reads as "unset, so any absolute URI is accepted as `resource`" — because tightening every stored client on upgrade would break flows that work today.

| Client | Empty `Audiences` means |
|---|---|
| Created through DCR or the admin API | **deny** — no `resource` may be named |
| Stored before `AudiencesDeclared` existed | **"unset"** — any absolute URI is accepted as `resource` |

**Retrofitting a legacy client** is a `PUT` to the admin client API with `audiencesDeclared: true` (and whatever `audiences` it should be pinned to). The flag only ever tightens: an update can set it and cannot clear it, so an unrelated edit will never silently return a client to the permissive reading.

The consequence for the legacy rows is worth stating plainly rather than burying:

> A pre-existing client with no configured `Audiences` may name **any** absolute URI as `resource` at the authorization endpoint or under `client_credentials`, and receive an access token whose `aud` is that value — signed by this tenant's key, carrying the requesting user's `sub` and whatever scopes the client is allowed.

A declared `audiences` list is validated where it is written: at most 20 entries of at most 512 characters, each an absolute URI with an explicit scheme and no fragment. `resource` values are held to the same shape — note that a bare path such as `/admin` is **not** accepted, even though .NET's `Uri` parser will call it an absolute `file:` URI on Linux.

Naming a resource is not access to it. But it does mean the authorization server cannot be the only thing standing between a client and an API it was never meant to call, so:

- **Resource servers MUST authorize on `scope`** (or their own model), not on `iss` + `aud` + `sub` alone. A token that names your API in `aud` proves the client asked for your API. It does not prove the client is allowed to call it, and this server cannot make it prove that.
- **Resource servers MUST validate `aud` against their own identifier**, not merely against "some value is present".
- **Set `Audiences` on every client that should be pinned to a fixed set of APIs.** With it configured, an unlisted `resource` is refused with `invalid_target` at the authorization endpoint and on `client_credentials`. This is the only place the restriction can be enforced.
- **Retrofit `audiencesDeclared: true` onto clients created before it existed**, so their empty audience list means "none" rather than "anything".
- **A self-registered client may declare `audiences`** at registration and is held to what it declares — including to an empty list. `Auth:DynamicClientRegistrationEnabled` is still off by default; see [Dynamic Client Registration](client-registration).

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
| `MetadataLocation` | Yes | URL to the IdP's SAML metadata XML. Must be https, and publicly routable unless the host is named in [`Auth:AllowedInternalTargets`](#outbound-fetches-ssrf-guard) — this document carries the certificates every assertion is validated against. Paste the XML into `MetadataXml` instead if your IdP publishes no https metadata endpoint. |
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
| `SecretProvider:RequireVaultReferences` | `false` by default. When `true`, a stored reference without a vault prefix (`kv:` for Key Vault, `sm:` for AWS Secrets Manager) is an **error** instead of being honoured as a plaintext value. Set it once a migration into the vault has finished. |

When configured, secret values that look like Key Vault references are resolved at runtime. Uses `DefaultAzureCredential` for authentication.

### Migrating into a vault, and closing the door afterwards

Both vault-backed providers return an unprefixed reference verbatim, treating it as a plaintext value written before the deployment had a vault. That is what lets a running system be migrated a secret at a time rather than all at once — but left open it is a permanent downgrade path: anything that can write one configuration column (a half-finished migration, an admin path that stores a raw value where a reference belongs, an attacker with storage access but none to the vault) replaces a vault-protected secret with a value of its own choosing, and it verifies perfectly, because for an unprefixed reference the reference *is* the value.

Set `SecretProvider:RequireVaultReferences` when the migration is done. Resolving an unprefixed reference then throws instead of quietly returning cleartext. Setting it while the resolved provider is the plaintext one is refused at startup, since that combination has no working state — every reference the plaintext provider writes is unprefixed.

The server also logs a startup warning whenever the plaintext provider is what a non-Development host ends up with.

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
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (array) | *(empty)* | CIDR ranges (string array, e.g. `"10.0.0.0/8"`) permitted to set forwarded headers. Set this to your proxy / ingress / pod CIDR. Declaring it is what allows `X-Forwarded-Proto` to be honoured at all — see below. |
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

### The two headers are not trusted on the same terms

`X-Forwarded-For` adjusts the **client IP** — the key rate limiting, lockout and the `/_internal` guard hang off. With nothing declared, Authagonal honours it from the loopback and RFC1918 ranges and logs a warning. That is a best-effort default, and it beats the framework's behaviour with an empty trust set, which is to honour the header from *any* caller alive.

`X-Forwarded-Proto` changes the **scheme**, and the scheme decides whether `/connect/*` answers at all (RFC 6749 §3.1/§3.2), whether cookies are marked `Secure`, and whether generated absolute URLs are https. It is honoured **only** from a proxy you have declared in `KnownNetworks` / `KnownProxies`. A private address is not a declaration: Authagonal ships as a library and cannot see the network it was deployed onto, so "the peer holds a private address" is a guess about topology. On a flat LAN, a shared VPC or a shared container bridge, every neighbouring workload is inside those ranges and could assert `https` over a request that arrived in cleartext.

**If your proxy has no fixed address** — a Kubernetes ingress, a rotating load balancer, a platform that will not tell you the hop's CIDR — declare every peer a proxy:

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

That is safe exactly when nothing but the proxy can reach the process, which is the assumption such a deployment is already relying on. Writing it down puts it somewhere it can be reviewed, rather than leaving the library to infer it. If other workloads *can* reach Kestrel directly, they can spoof the scheme and the client IP under this setting — pin the real CIDR instead.

> ⚠️ **TLS-terminating proxy required, and it must be declared.** Authagonal must run behind a TLS-terminating reverse proxy (or terminate TLS itself). HSTS (`Strict-Transport-Security`) is only emitted on HTTPS requests, and the OAuth endpoints refuse plaintext requests outright unless `Auth:AllowInsecureHttp` is set — so the proxy must forward `X-Forwarded-Proto: https` **and** be named in `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` for HSTS to be sent and `/connect/*` to answer at all. Declaring nothing is the common upgrade failure: the header arrives, nothing is entitled to act on it, and every `/connect/*` request answers 400 on a deployment that genuinely is on TLS. The startup log says so, and so does the refusal body.

## Outbound fetches (SSRF guard)

Authagonal makes server-initiated HTTP requests to URLs it did not choose: an upstream IdP's SAML metadata or OIDC discovery document, a client's `jwks_uri` during `private_key_jwt` authentication, a back-channel logout URI, a provisioning callback. Some of those URLs are supplied by whoever registered a client, and a URL naming `169.254.169.254` or a host inside your cluster is then a request Authagonal makes on an attacker's behalf.

Every one of those fetches is guarded twice. The **URL check** refuses non-http(s) schemes, literal internal addresses, and `localhost` / `.local` / `.internal` names, at the point the URL is accepted — an admin write, a dynamic client registration — where the error is attributable to whoever typed it. The **address check** runs at the socket: it resolves the host, refuses every returned address that is internal, and connects to an address it actually checked rather than handing the name back to the OS. That second one is what a text check cannot do, because a hostname is not text the attacker has to be honest about: `logout.attacker.test` passes every suffix and literal rule and then answers with the cloud metadata address. Because a redirect is a new connection, the address check re-runs on every hop.

Both are on by default and most deployments never notice them. Two things make them visible.

### Reaching an internal destination on purpose

Federating with an IdP that is only reachable over your private network, or provisioning an app that runs in the same cluster, is refused by exactly the same rule that stops the attack. Name those destinations:

```json
{
  "Auth": {
    "AllowedInternalTargets": ["idp.corp.internal", "*.svc.corp.internal", "10.4.0.0/16"]
  }
}
```

| Entry form | Permits |
|---|---|
| `idp.corp.internal` | That exact host, and every address it resolves to |
| `*.corp.internal` | Any host under the suffix, and every address those resolve to |
| `10.4.0.0/16`, `fd00:1234::/48` | That network, under any name |
| `10.4.1.7` | That single address, under any name |

Env-variable form is `Auth__AllowedInternalTargets__0`, `__1`, and so on. A malformed CIDR entry fails at startup rather than silently permitting nothing.

**This list only reaches the URLs you supplied.** The upstream SAML metadata fetch, upstream OIDC discovery — including the `token_endpoint`, `userinfo_endpoint` and `jwks_uri` that document names — and provisioning callbacks. It deliberately does **not** reach a client-registered `jwks_uri` or back-channel logout URI, where an internal host is never a deployment, so opening a federation target cannot also open the metadata service to an anonymous `/connect/token` request. There is no global "off".

Note that https is still required on both federation metadata URLs regardless of this list. That document carries the keys and certificates every upstream assertion is validated against, and a private network is not a secure channel.

> ⚠️ **Multi-tenant hosts: check who writes the metadata URL before you list anything.** This list is scoped to targets *you* configured, and in a single-tenant deployment the connection admin is you. If you run Authagonal for other people — a SaaS where tenant admins configure their own SAML/OIDC connections through the portal or admin API — then `MetadataLocation` is **customer**-supplied, and every entry you add here is reachable by any tenant who points a connection at it. Leave it empty on such a host (the default), and if one tenant genuinely needs an on-premises IdP, give them an egress path that terminates outside your network rather than opening one from inside it.

### If your egress requires an HTTP proxy

The address check is attached to `SocketsHttpHandler.ConnectCallback`, and with a proxy in effect .NET invokes that callback with the **proxy's** endpoint and never the target's — so the check would inspect the proxy, find it perfectly routable, and permit everything. It would fail open in precisely the networks most likely to have a proxy. So the guarded clients set `UseProxy = false`, and in a proxy-only network their fetches fail.

`Auth:AllowOutboundProxy` sends the operator-configured fetches (SAML metadata, OIDC discovery, provisioning callbacks) back through the proxy. You keep the URL check and lose the address check for them: a hostname resolving to an internal address is no longer caught. It does **not** reach the client `jwks_uri` fetch or back-channel logout delivery — those targets are registrant-chosen and reachable from anonymous requests, so there is no switch for them. A network that must proxy those needs an SSRF-filtering egress gateway in front of them.

`UseAuthagonal()` logs a warning at startup when it finds `HTTPS_PROXY`, `HTTP_PROXY` or `ALL_PROXY` set, naming which clients bypass it — otherwise the symptom is "SSO stopped working" with nothing pointing at the cause.

### What is not guarded

The BFF's outbound clients and email delivery. `AuthagonalBffOptions.Upstreams[].TargetBaseUrl` is your own configuration whose documented example is an internal address, the BFF's token client talks to the authority you configured, and the proxy already refuses any composed target that left the configured upstream authority — so a caller cannot steer those requests. `Resend` posts to a compile-time constant. All three use the ambient proxy normally.

## Rate Limiting

Built-in rate limits protect the abuse-prone endpoints:

| Endpoint | Limit | Window | Keyed on |
|---|---|---|---|
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 hour (`Auth:RegistrationWindowMinutes`) | Client IP |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 hour (`Auth:PasswordResetWindowMinutes`) | Target email |
| `POST /api/auth/forgot-password` | 15 (`Auth:MaxPasswordResetsPerIp`) | 1 hour (`Auth:PasswordResetWindowMinutes`) | Client IP |
| `POST /connect/register` (when enabled) | 10 | 1 hour | Client IP |
| SCIM endpoints | 200 | 1 minute | SCIM client |

Limits are enforced **in-process per node** by default (behind the `IRateLimiter` seam), so with N instances the effective ceiling is N× the configured value. Treat these as a backstop and enforce the authoritative global limit at the edge (WAF / ingress / CDN). See [Scaling](scaling#rate-limiting).

### Cluster-wide limits (`Auth:DurableRateLimiting`)

Set `Auth:DurableRateLimiting` to `true` to move the counters into the store the deployment already
runs, so every replica shares one budget and the ceiling stops multiplying by instance count.

| | in-process (default) | durable |
|---|---|---|
| Ceiling on N replicas | N× the configured value | the configured value |
| Cost per check | none | one store round trip |
| Survives a pod restart | no | yes |
| Backends | any | Azure Table, SQL, DynamoDB |

Worth turning on when a budget guards something guessable — the device-flow `user_code` above all, where
the attempt limit is the only thing between an attacker and a code that grants a live session, and a
budget that scales with replica count is the wrong shape. Less useful for the volume limits, where the
edge is the authoritative bound anyway.

Details that matter in production:

- **It is not free.** Every rate-limit check becomes a store round trip, including on the login, token
  and SCIM paths. A single-node deployment gains nothing (there, per-node *is* cluster-wide) and should
  leave it off.
- **Fixed windows, so bursts can straddle a boundary.** A budget of N is "N per window, and up to 2N
  across a boundary" — the shipped budgets have that headroom. This is what lets the counter be a single
  atomic increment on every backend, which is the property the correctness rests on.
- **It fails open.** If the store is unreachable the request is allowed and an error is logged: the
  limiter guards the login path and must not become a way to take it down. Keep the edge rule in place.
- **The host will not start** if you set this without a provider that supplies `IRateLimitCounterStore`
  — it refuses rather than quietly falling back to the per-node limiting you just turned off.
- **Counter rows are collected automatically**: DynamoDB by native TTL, SQL by `SqlExpiryReaper`, Azure
  Table by a leader-only sweep (Table Storage has neither TTL nor server-side arithmetic, so it is also
  the backend where an increment costs a read plus a conditional write).

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

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

## Email

The default email service uses SendGrid. To use a different provider, implement `IEmailService` and register it before `AddAuthagonal()` — see [Extensibility](extensibility).

| Setting | Description |
|---|---|
| `Email:SendGridApiKey` | SendGrid API key for sending emails |
| `Email:FromAddress` | Sender email address |
| `Email:FromName` | Sender display name |
| `Email:VerificationTemplateId` | SendGrid dynamic template ID for email verification |
| `Email:PasswordResetTemplateId` | SendGrid dynamic template ID for password reset |

Emails to `@example.com` addresses are silently skipped (useful for testing).

## Rate Limiting

Built-in per-IP rate limits:

| Endpoint Group | Limit | Window |
|---|---|---|
| Auth endpoints (login, SSO) | 20 requests | 1 minute |
| Token endpoint | 30 requests | 1 minute |

## CORS

CORS is configured dynamically. Origins from all registered clients' `AllowedCorsOrigins` are automatically allowed, with a 60-minute cache.

## Full Example

```json
{
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..."
  },
  "Issuer": "https://auth.example.com",
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
      "ProvisioningApps": ["backend"]
    }
  ]
}
```

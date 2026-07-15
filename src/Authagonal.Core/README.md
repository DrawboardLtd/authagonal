# Authagonal.Core

Core models, interfaces, and abstractions for the Authagonal authentication server. Use this package to build custom storage backends or extend the authentication pipeline.

## Models

| Model | Description |
|-------|-------------|
| `AuthUser` | User account, email, password hash, profile, lockout, security stamp |
| `OAuthClient` | OAuth/OIDC client, secrets, grant types, redirect URIs, scopes, lifetimes |
| `PersistedGrant` | Authorization codes, refresh tokens, and other temporary grants |
| `OidcProviderConfig` | External OIDC identity provider (Google, Okta, etc.) |
| `SamlProviderConfig` | SAML 2.0 identity provider |
| `SsoDomain` | Email domain-to-SSO provider routing |
| `PasswordPolicy` | Configurable password strength rules |
| `Role` | Named role for RBAC |
| `MfaCredential` | MFA credential (TOTP, WebAuthn, recovery codes) |
| `MfaChallenge` | Pending MFA challenge token |
| `ScimGroup` | SCIM-provisioned group |
| `ScimToken` | SCIM Bearer token (stored hashed) |

## Store interfaces

Implement these to plug in your own storage backend:

| Interface | Purpose |
|-----------|---------|
| `IUserStore` | User CRUD, email lookup, external login management |
| `IClientStore` | OAuth client CRUD |
| `IGrantStore` | Persisted grant (token) storage with expiry |
| `ISigningKeyStore` | RSA signing key persistence |
| `IOidcProviderStore` | OIDC provider configuration |
| `ISamlProviderStore` | SAML provider configuration |
| `ISsoDomainStore` | Domain-to-provider routing |
| `IUserProvisionStore` | Downstream app provisioning state |
| `IMfaStore` | MFA credentials and challenges |
| `IRoleStore` | Role CRUD and user-role assignments |
| `IScopeStore` | Custom OAuth scope definitions |
| `IScimTokenStore` | SCIM Bearer token storage |
| `IScimGroupStore` | SCIM group storage |
| `IRevokedTokenStore` | Revoked-token (jti) tracking for introspection |
| `IProvisioningAppStore` | Downstream provisioning app registrations |
| `ISamlReplayCache` | SAML assertion replay protection + AuthnRequest state |
| `IOidcStateStore` | Upstream OIDC federation state |
| `IChangeWriter` | Change-log capture (upserts + delete tombstones) for incremental backups |

## Extensibility hooks

| Interface | Purpose |
|-----------|---------|
| `IAuthHook` | Lifecycle callbacks, `OnUserAuthenticated`, `OnUserCreated`, `OnUserUpdated`, `OnUserDeleted`, `OnLoginFailed`, `OnTokenIssued`, `ResolveMfaPolicy`, `OnMfaVerified`, `OnMfaVerifyFailed`, `OnMfaEnrolled`, `OnEmailConfirmed`, and more. Multiple implementations run in registration order. |
| `IEmailService` | Email delivery, verification, password reset |
| `IProvisioningOrchestrator` | User provisioning into downstream apps (TCC protocol) |
| `ISecretProvider` | Reversible secret protection at rest (plaintext default; Key Vault or your own) |
| `IFieldCipher` | Field-level encryption of PII at rest (passthrough default) |
| `IIndexTokenizer` | Blind-index tokenization so encrypted fields stay searchable (passthrough default) |
| `ITenantContext` | Tenant resolution for multi-tenant deployments |
| `IKeyManager` | Signing key management, override for per-tenant key isolation |

## Packages

| Package | Description |
|---------|-------------|
| **Authagonal.Core** | Core models, interfaces, and abstractions |
| [Authagonal.Protocol](https://www.nuget.org/packages/Authagonal.Protocol) | Embeddable OIDC/OAuth 2.0 protocol surface (no UI, no user store) |
| [Authagonal.AzureProvider](https://www.nuget.org/packages/Authagonal.AzureProvider) | Azure Table Storage backend |
| [Authagonal.AwsProvider](https://www.nuget.org/packages/Authagonal.AwsProvider) | AWS backend, DynamoDB / S3 / Secrets Manager |
| [Authagonal.Backup](https://www.nuget.org/packages/Authagonal.Backup) | Backup, restore, merge, and rollup for the storage backends |
| [Authagonal.Server](https://www.nuget.org/packages/Authagonal.Server) | Full auth server, endpoints, middleware, services, login UI |

## Links

- [GitHub](https://github.com/authagonal/authagonal)
- [Documentation](https://authagonal.github.io/authagonal)

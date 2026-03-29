# Authagonal.Core

Core models, interfaces, and abstractions for the Authagonal authentication server. Use this package to build custom storage backends or extend the authentication pipeline.

## Models

| Model | Description |
|-------|-------------|
| `AuthUser` | User account — email, password hash, profile, lockout, security stamp |
| `OAuthClient` | OAuth/OIDC client — secrets, grant types, redirect URIs, scopes, lifetimes |
| `PersistedGrant` | Authorization codes, refresh tokens, and other temporary grants |
| `OidcProviderConfig` | External OIDC identity provider (Google, Okta, etc.) |
| `SamlProviderConfig` | SAML 2.0 identity provider |
| `SsoDomain` | Email domain-to-SSO provider routing |
| `PasswordPolicy` | Configurable password strength rules |

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

## Extensibility hooks

| Interface | Purpose |
|-----------|---------|
| `IAuthHook` | Lifecycle callbacks — `OnUserAuthenticated`, `OnUserCreated`, `OnLoginFailed`, `OnTokenIssued` |
| `IEmailService` | Email delivery — verification, password reset |
| `IProvisioningOrchestrator` | User provisioning into downstream apps (TCC protocol) |
| `ISecretProvider` | Secret resolution (plaintext or Key Vault) |

## Packages

| Package | Description |
|---------|-------------|
| **Authagonal.Core** | Core models, interfaces, and abstractions |
| [Authagonal.Storage](https://www.nuget.org/packages/Authagonal.Storage) | Azure Table Storage backend |
| [Authagonal.Server](https://www.nuget.org/packages/Authagonal.Server) | Full auth server — endpoints, middleware, services, login UI |

## Links

- [GitHub](https://github.com/DrawboardLtd/authagonal)
- [Documentation](https://drawboardltd.github.io/authagonal)

# Authagonal.Storage

Azure Table Storage implementation for Authagonal. Provides production-ready, serverless storage for users, clients, tokens, and SSO provider configurations.

## What's included

All `Authagonal.Core` store interfaces are implemented using Azure Table Storage:

| Store | Tables | Description |
|-------|--------|-------------|
| `TableUserStore` | Users, UserEmails, UserLogins | User accounts with email index and external login associations |
| `TableClientStore` | Clients | OAuth/OIDC client registrations |
| `TableGrantStore` | Grants, GrantsBySubject, GrantsByExpiry | Tokens with optimized indexes for lookup and cleanup |
| `TableSigningKeyStore` | SigningKeys | RSA key pairs for JWT signing |
| `TableOidcProviderStore` | OidcProviders | External OIDC identity provider configs |
| `TableSamlProviderStore` | SamlProviders | SAML 2.0 identity provider configs |
| `TableSsoDomainStore` | SsoDomains | Email domain-to-SSO provider routing |
| `TableUserProvisionStore` | UserProvisions | Downstream app provisioning state |
| `TableMfaStore` | MfaCredentials, MfaChallenges, MfaWebAuthnIndex | MFA credentials and challenges |
| `TableRoleStore` | Roles | Role definitions and user-role assignments |
| `TableScimTokenStore` | ScimTokens | SCIM Bearer tokens (hashed) |
| `TableScimGroupStore` | ScimGroups, ScimGroupExternalIds | SCIM-provisioned groups |
| `TableTombstoneWriter` | Tombstones | Soft-delete tracking for incremental backups |

## Usage

If you're using `Authagonal.Server`, storage is registered automatically. For standalone use:

```csharp
builder.Services.AddTableStorage(builder.Configuration["Storage:ConnectionString"]!);
```

Tables are auto-created on first run. Retry policies (5 retries, exponential backoff) are configured by default.

## Custom storage

To use a different backend (SQL, MongoDB, Cosmos DB, etc.), implement the interfaces in `Authagonal.Core` instead of referencing this package.

## Packages

| Package | Description |
|---------|-------------|
| [Authagonal.Core](https://www.nuget.org/packages/Authagonal.Core) | Core models, interfaces, and abstractions |
| [Authagonal.Protocol](https://www.nuget.org/packages/Authagonal.Protocol) | Embeddable OIDC/OAuth 2.0 protocol surface (no UI, no user store) |
| **Authagonal.Storage** | Azure Table Storage backend |
| [Authagonal.Server](https://www.nuget.org/packages/Authagonal.Server) | Full auth server — endpoints, middleware, services, login UI |

## Links

- [GitHub](https://github.com/authagonal/authagonal)
- [Documentation](https://authagonal.github.io/authagonal)

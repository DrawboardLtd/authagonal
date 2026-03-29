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

## Usage

If you're using `Authagonal.Server`, storage is registered automatically. For standalone use:

```csharp
builder.Services.AddTableStorage(builder.Configuration);
```

```json
{
  "Storage": {
    "ConnectionString": "UseDevelopmentStorage=true"
  }
}
```

Tables are auto-created on first run. Retry policies (5 retries, exponential backoff) are configured by default.

## Custom storage

To use a different backend (SQL, MongoDB, Cosmos DB, etc.), implement the interfaces in `Authagonal.Core` instead of referencing this package.

## Packages

| Package | Description |
|---------|-------------|
| [Authagonal.Core](https://www.nuget.org/packages/Authagonal.Core) | Core models, interfaces, and abstractions |
| **Authagonal.Storage** | Azure Table Storage backend |
| [Authagonal.Server](https://www.nuget.org/packages/Authagonal.Server) | Full auth server — endpoints, middleware, services, login UI |

## Links

- [GitHub](https://github.com/DrawboardLtd/authagonal)
- [Documentation](https://drawboardltd.github.io/authagonal)

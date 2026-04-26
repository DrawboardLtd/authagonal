# Authagonal.Protocol

Embeddable OIDC / OAuth 2.0 protocol surface extracted from Authagonal.Server.

Provides `/connect/authorize`, `/connect/token`, `/connect/userinfo`, `/connect/par` (RFC 9126 Pushed Authorization Requests), `/.well-known/openid-configuration`, and JWKS endpoints plus the token minting pipeline — nothing else. No user store, no SAML, no admin UI, no login pages.

Plug in your own identity via `IOidcSubjectResolver` and storage via `IClientStore` / `IGrantStore` / `IScopeStore` / `ISigningKeyStore`. Use this when you need to expose OIDC from an app that already has its own identity (e.g. share-link grants, service-to-service auth).

## Quick start

```
dotnet add package Authagonal.Protocol
```

```csharp
builder.Services.AddAuthagonalProtocol(opts =>
{
    opts.AuthenticationScheme = "Cookies"; // or your custom scheme
    opts.Clients.Add(new OidcClientDescriptor
    {
        ClientId = "my-rp",
        RedirectUris = { "https://rp.example.com/callback" },
        AllowedScopes = { "openid", "profile", "email" },
    });
});

builder.Services.AddScoped<IOidcSubjectResolver, MySubjectResolver>();
builder.Services.AddSingleton<IClientStore, MyClientStore>();
// ... IGrantStore, IScopeStore, ISigningKeyStore, ITenantContext

app.MapAuthagonalProtocolEndpoints();
```

## Federation passthrough

`OidcSubject.FederationClaims` carries per-session claims received from an upstream IdP through to issued tokens, gated by the same scope-driven `UserClaims` whitelist as `CustomAttributes`. Federation values win on key collision and survive refresh rotations distinct from the per-user record.

## Packages

| Package | Description |
|---------|-------------|
| [Authagonal.Core](https://www.nuget.org/packages/Authagonal.Core) | Core models, interfaces, and abstractions |
| **Authagonal.Protocol** | Embeddable OIDC/OAuth 2.0 protocol surface |
| [Authagonal.Storage](https://www.nuget.org/packages/Authagonal.Storage) | Azure Table Storage backend |
| [Authagonal.Server](https://www.nuget.org/packages/Authagonal.Server) | Full auth server — endpoints, middleware, services, login UI |

## Links

- [GitHub](https://github.com/authagonal/authagonal)
- [Documentation](https://authagonal.github.io/authagonal)

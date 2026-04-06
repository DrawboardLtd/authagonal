# Authagonal.Server

Drop-in authentication server for ASP.NET Core. Add OAuth 2.0, OpenID Connect, SAML SSO, and a built-in login UI to your app with three lines of code.

## Quick start

```
dotnet add package Authagonal.Server
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();
app.UseAuthagonal();
app.MapAuthagonalEndpoints();
app.MapFallbackToFile("index.html");
app.Run();
```

```json
{
  "Issuer": "https://auth.example.com",
  "Storage": {
    "ConnectionString": "UseDevelopmentStorage=true"
  }
}
```

That's it. You now have a fully functional auth server with:

- **OAuth 2.0 / OpenID Connect** — Authorization code + PKCE, refresh tokens, client credentials
- **SAML 2.0 SSO** — SP-initiated flows with automatic metadata parsing
- **External OIDC providers** — Google, Microsoft, Okta, etc.
- **Built-in login UI** — Customizable SPA with localization (8 languages)
- **Admin APIs** — User management, SSO provider management, token administration
- **Password policy** — Configurable strength requirements

## Configuration

Clients and SSO providers can be seeded from configuration:

```json
{
  "Clients": [
    {
      "Id": "my-app",
      "Name": "My Application",
      "GrantTypes": ["authorization_code", "refresh_token"],
      "RedirectUris": ["https://app.example.com/callback"],
      "Scopes": ["openid", "profile", "email", "offline_access"],
      "RequirePkce": true,
      "RequireSecret": false
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret"
    }
  ]
}
```

## Extensibility

Register custom implementations **before** `AddAuthagonal` — they take precedence via `TryAdd`:

```csharp
// Custom lifecycle hooks (audit logging, webhooks, etc.)
builder.Services.AddSingleton<IAuthHook, MyAuthHook>();

// Custom email delivery (SMTP, SES, Mailgun, etc.)
builder.Services.AddSingleton<IEmailService, MyEmailService>();

// Custom user provisioning into downstream apps
builder.Services.AddSingleton<IProvisioningOrchestrator, MyProvisioner>();

builder.Services.AddAuthagonal(builder.Configuration);
```

## Packages

| Package | Description |
|---------|-------------|
| **Authagonal.Server** | Full auth server — endpoints, middleware, services, login UI |
| [Authagonal.Storage](https://www.nuget.org/packages/Authagonal.Storage) | Azure Table Storage backend |
| [Authagonal.Core](https://www.nuget.org/packages/Authagonal.Core) | Core models, interfaces, and abstractions |

## Links

- [GitHub](https://github.com/DrawboardLtd/authagonal)
- [Documentation](https://drawboardltd.github.io/authagonal)
- [Live demo](https://demo.authagonal.drawboard.com)

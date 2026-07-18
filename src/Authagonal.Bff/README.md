# Authagonal.Bff

Backend-for-Frontend (BFF) for SPAs that authenticate with **Authagonal**.

Your React/Vue/Angular app should never hold access or refresh tokens: anything in JS-reachable
storage is exposed to XSS. This package is a confidential OIDC client you host on your own backend.
It runs the authorization-code + PKCE flow server-side, keeps the tokens in a server-side session,
and gives the browser nothing but an httpOnly session cookie. This is the pattern the IETF
*OAuth 2.0 for Browser-Based Apps* BCP recommends.

## Install

```
dotnet add package Authagonal.Bff
```

## Wire it up

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthagonalBff(o =>
{
    o.Authority    = "https://acme-admin.authagonal.io"; // your tenant auth host
    o.ClientId     = builder.Configuration["Bff:ClientId"]!;
    o.ClientSecret = builder.Configuration["Bff:ClientSecret"]!;
    o.Scope        = ["openid", "profile", "email", "offline_access"]; // offline_access enables refresh
    o.PostLogoutRedirectUri = "https://app.acme.com/";
});

var app = builder.Build();

app.UseForwardedHeaders();  // required if you run behind a reverse proxy / ingress
app.MapAuthagonalBff();
app.MapFallbackToFile("index.html"); // your SPA

app.Run();
```

Register a **BFF client** in the Authagonal portal (confidential + PKCE + `offline_access`) with:

- redirect URI `https://app.acme.com/bff/callback`
- post-logout redirect URI `https://app.acme.com/`

## Endpoints (mounted under `/bff` by default)

| Route | Purpose |
|---|---|
| `GET /bff/login?returnUrl=/` | Start login; redirects to Authagonal. |
| `GET /bff/callback` | OIDC redirect URI (handled for you). |
| `GET /bff/user` | `{ isAuthenticated, claims, sessionExpiresAt }`. Requires the anti-forgery header. |
| `GET\|POST /bff/logout` | Ends the session locally + at Authagonal. |

## From the browser

Every non-navigation call must carry a static anti-forgery header (defends against CSRF alongside
`SameSite=Lax`):

```js
const me = await fetch("/bff/user", { headers: { "X-Authagonal-Bff": "1" } }).then(r => r.json());
if (!me.isAuthenticated) window.location.href = "/bff/login?returnUrl=" + encodeURIComponent(location.pathname);
```

To log in / out, navigate (don't fetch): `location.href = "/bff/login"` / `"/bff/logout"`.

## Sessions & scaling

Sessions are stored via `IDistributedCache`. In-memory is the default; register a real distributed
cache (e.g. Redis) before `AddAuthagonalBff` when you run more than one instance:

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = "...");
```

## Extension points (the hosted seam)

Swap any of these to move the BFF onto other infrastructure:

- `IBffSessionStore` — where sessions live (default: `IDistributedCache`).
- `ICookieProtector` — cookie payload encryption (default: ASP.NET Data Protection).
- `ITokenClient` — talking to Authagonal's token/revocation endpoints.

See `docs/bff.md` in the authagonal-cloud repo for the full protocol contract.

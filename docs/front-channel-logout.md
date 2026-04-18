---
layout: default
title: Front-Channel Logout
---

# Front-Channel Logout

Authagonal implements **OpenID Connect Front-Channel Logout 1.0**, a browser-driven logout mechanism that complements [back-channel logout](index#features). Where back-channel logout is a server-to-server POST, front-channel logout renders the logout URL of each relying party in a hidden iframe so that each app's browser session (cookies, local storage) is cleaned up from inside the user's browser.

## When to Use Which

| Concern | Back-Channel | Front-Channel |
|---|---|---|
| Server-side sessions | ✅ | ❌ |
| Browser cookies / local storage | ❌ | ✅ |
| Works when the user's browser is offline | ✅ | ❌ |
| Survives network errors (retry) | ✅ | ❌ (single best-effort attempt) |

Most apps benefit from configuring **both**. Back-channel guarantees the server is told; front-channel clears the browser.

## Client Configuration

Add a front-channel logout URI to the `OAuthClient` record:

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| Field | Description |
|---|---|
| `FrontChannelLogoutUri` | The client's browser-visible logout endpoint |
| `FrontChannelLogoutSessionRequired` | If `true` (default), the URL is called with `iss` and `sid` query parameters so the client can correlate the logout with the specific session |

## How It Works

When the browser visits `/connect/endsession`:

1. The server finds all clients the user currently has grants with.
2. For each client with a `FrontChannelLogoutUri`, the server builds a URL — appending `iss=<issuer>` and `sid=<session_id>` if `FrontChannelLogoutSessionRequired` is `true`.
3. The server signs the user out of the authorization-server cookie, triggers back-channel logout notifications in the background, and returns an HTML page containing a hidden `<iframe>` for each client logout URL:
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
4. After a 2-second grace period, the browser is redirected to `post_logout_redirect_uri` (if supplied and validated) or shown a "signed out" confirmation.

## Client-Side Logout Handler

Each relying party should implement the URL referenced by `FrontChannelLogoutUri`. A minimal handler:

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. Verify `iss` matches the expected authorization server.
2. If `sid` is provided, confirm it matches the session cookie's session ID.
3. Clear the local session (cookies, server-side session, SPA storage).
4. Respond with `200 OK` and an empty body (or a tiny page) — the response is never visible to the user.

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Validate iss/sid, then clear local session
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## Discovery Document

Front-channel logout is advertised in `/.well-known/openid-configuration`:

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## Dynamic Client Registration

Clients registered via [Dynamic Client Registration](client-registration) may include:

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

## Limitations

- **Best effort** — iframes are loaded once. If a network error or browser extension blocks them, there is no retry. Pair with back-channel logout for reliability.
- **Third-party cookies** — some browsers block cookies in cross-site iframes by default. If your RP relies on first-party cookies, confirm the logout handler does not depend on cookies being sent.
- **Timeout** — the page waits ~2 seconds before redirecting/confirming. Heavy RP logout handlers may not complete in time.

## Related

- [Dynamic Client Registration](client-registration) — front-channel parameters in the registration request
- [OAuth Scopes](scopes) — scope-aware consent complements the logout flow

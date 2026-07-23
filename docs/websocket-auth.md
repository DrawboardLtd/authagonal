---
layout: default
title: WebSocket Auth (BFF)
---

# Authenticating WebSockets from the BFF

The [BFF](installation) exists so a browser SPA never holds a token: it gets an httpOnly
session cookie, and the BFF injects the `Authorization: Bearer` header when it proxies API calls. That works
for HTTP — but a browser `WebSocket` can't set an `Authorization` header, and you don't want to put the
access token in the URL or in JS-readable storage.

The **ws-ticket** flow solves this. The SPA asks the BFF for a short-lived, single-use ticket, opens the
socket with the *ticket* (not the token), and your API host redeems the ticket for the real access token
server-side. The token never touches the browser.

```
Browser ──GET /bff/ws-ticket──► BFF ──(store token under ticket in shared cache)──► returns { ticket }
Browser ──wss://api/…?ticket=── ► API host ──TryRedeemWsTicketAsync──► access token ──► authenticate socket
```

## 1. Enable it on the BFF

Ws-tickets are opt-in, and both the BFF and your API host must share the **same** distributed cache (Redis
in production) so the API host can read what the BFF wrote:

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);

builder.Services.AddAuthagonalBff(o =>
{
    // …your usual BFF options (authority, client id/secret, base path)…
    o.WsTicketsEnabled = true;                  // maps GET {BasePath}/ws-ticket
    o.WsTicketLifetime = TimeSpan.FromSeconds(30); // default
});
```

## 2. Mint + connect (browser)

`GET {BasePath}/ws-ticket` requires the session cookie and the BFF's anti-forgery header (same as any BFF
call). It returns an opaque ticket bound to the session's freshly-refreshed access token. Fetch it, connect,
and drop it — never persist it:

```javascript
async function openSocket() {
  const res = await fetch('/bff/ws-ticket', {
    headers: { 'X-CSRF': '1' },           // your configured AntiForgeryHeader
  });
  const { ticket } = await res.json();     // { ticket, expiresInSeconds }
  return new WebSocket(`wss://api.example.com/live?ticket=${encodeURIComponent(ticket)}`);
}
```

## 3. Redeem it (API host)

On the socket's upgrade request, pull the `ticket` and exchange it for the access token against the shared
cache. `TryRedeemWsTicketAsync` looks the token up and **deletes** the key, so a ticket works exactly once:

```csharp
using Authagonal.Bff; // BffEndpoints.TryRedeemWsTicketAsync / WsTicketKey

app.Map("/live", async (HttpContext ctx, IDistributedCache cache) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
        return Results.BadRequest();

    var ticket = ctx.Request.Query["ticket"].ToString();
    var accessToken = await BffEndpoints.TryRedeemWsTicketAsync(cache, ticket, ctx.RequestAborted);
    if (accessToken is null)
        return Results.StatusCode(StatusCodes.Status401Unauthorized); // unknown / expired / already used

    // Validate the token the same way you validate Bearer tokens on HTTP, then accept the socket.
    if (!TryValidate(accessToken, out var principal))
        return Results.StatusCode(StatusCodes.Status401Unauthorized);

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await ServeAsync(socket, principal, ctx.RequestAborted);
    return Results.Empty;
});
```

If your API host is a separate assembly and you'd rather not reference `Authagonal.Bff`, the cache key
format is public: the token is stored under `WsTicketKey(ticket)` = `agbff:wst:{ticket}`. Read it and delete
it yourself — but prefer the shipped helper.

## Security notes

- **256-bit random, 30 s TTL.** The ticket is unguessable and short-lived; even if it leaks from a URL/log
  it's useless after the window.
- **Single-use, with a caveat.** `IDistributedCache` has no atomic get-and-delete, so the helper does
  get-then-delete: two requests reading the same ticket in the tiny gap before either deletes could both
  succeed. For strict single-use, back the cache with a store that offers an atomic pop (Redis `GETDEL`).
- **The browser only ever carries the opaque ticket.** The access token stays server-side, exactly as with
  the HTTP proxy.

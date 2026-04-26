---
layout: default
title: Pushed Authorization Requests
---

# Pushed Authorization Requests (PAR)

[RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) lets a client POST its authorize-request parameters directly to the server with standard client authentication and receive a short-lived opaque `request_uri` to hand to the browser. The browser then visits `/connect/authorize?request_uri=...&client_id=...` instead of carrying every parameter on the URL.

Why use it:

- Authorize parameters never appear in browser history, server logs, or `Referer` headers.
- The server authenticates the client at push time, so the parameters are integrity-checked before any redirect happens.
- Long parameter sets (large `claims` requests, multi-resource flows) don't blow URL length limits.

## Endpoint

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

Authentication is the same as `/connect/token`: HTTP Basic with `client_id`/`client_secret`, or form-encoded credentials. Confidential clients must authenticate; public clients post without a secret.

The form body carries the same parameters that would normally go on `/connect/authorize` (`response_type`, `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method`, `nonce`, `resource`, etc.). `request_uri` itself is rejected — chaining a PAR is forbidden by §2.1 of the spec.

### Response

```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

The `request_uri` is single-use. It's removed from the store once the matching `/connect/authorize` request consumes it (or when the 90-second window expires, whichever is sooner).

### Authorization step

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

When `request_uri` is present, all other parameters are pulled from the pushed payload — anything else on the URL is ignored. The `client_id` on this request must match the client that pushed the payload.

## Requiring PAR per client

Set `RequirePushedAuthorizationRequests = true` on a client to refuse plain `/connect/authorize` requests from it. Any non-PAR authorize attempt returns `invalid_request` with the description "This client requires requests to be pushed via /connect/par".

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

This is the recommended posture for clients that handle sensitive scopes — combined with PKCE, it removes the URL bar as an attack surface.

## Lifetime and storage

The `request_uri` lifetime is server-set at 90 seconds, matching the typical reference-IdP value. Pushed payloads are stored via the same `IGrantStore` as auth codes and refresh tokens, so they inherit the host's persistence and replication strategy automatically.

## Discovery

The PAR endpoint advertises itself in `.well-known/openid-configuration` as:

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```

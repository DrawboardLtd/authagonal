---
layout: default
title: Dynamic Client Registration
---

# Dynamic Client Registration

Authagonal implements **OAuth 2.0 Dynamic Client Registration** ([RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)), allowing client applications to register themselves at runtime without administrator involvement.

## Enabling the Endpoint

Dynamic registration is **disabled by default**. Opt in via configuration:

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

Or set `Auth__DynamicClientRegistrationEnabled=true` as an environment variable.

When enabled, the discovery document advertises the endpoint:

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## Registering a Client

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### Response

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

The `client_secret` is returned **once** and cannot be retrieved later. Store it securely.

## Request Parameters

| Parameter | Required | Notes |
|---|---|---|
| `client_name` | no | Defaults to the generated `client_id` if omitted |
| `redirect_uris` | conditional | Required when `grant_types` contains `authorization_code` or `implicit` |
| `post_logout_redirect_uris` | no | Valid redirect targets after logout |
| `grant_types` | no | Defaults to `["authorization_code"]`. `refresh_token` is added automatically if `offline_access` is requested. |
| `token_endpoint_auth_method` | no | `client_secret_basic` (default), `client_secret_post`, or `none` for public clients |
| `scope` | no | Space-separated scopes — must all be built-in or previously registered (see [Scopes](scopes)) |
| `audiences` | no | JWT `aud` values added to access tokens |
| `allowed_cors_origins` | no | Origins permitted to call the token endpoint from a browser |
| `backchannel_logout_uri` | no | Enables [Back-Channel Logout](index#features) |
| `frontchannel_logout_uri` | no | Enables [Front-Channel Logout](front-channel-logout) |
| `frontchannel_logout_session_required` | no | Defaults to `true`; when `true`, the logout URL carries `iss` and `sid` parameters |

## Defaults & Invariants

- **PKCE required** — `RequirePkce` is always `true` for dynamically registered clients.
- **Public clients** — `token_endpoint_auth_method: "none"` produces a client without a secret. PKCE is still required.
- **Offline access** — requesting scope `offline_access` implicitly adds `refresh_token` to `grant_types`.

## Error Responses

| HTTP | `error` | Cause |
|---|---|---|
| `400` | `invalid_redirect_uri` | One of `redirect_uris` is not a valid absolute URI |
| `400` | `invalid_client_metadata` | `redirect_uris` missing for a grant type that requires it |
| `400` | `invalid_scope` | A requested scope is neither built-in nor registered |
| `403` | `not_supported` | Dynamic client registration is not enabled |

## Security Considerations

The registration endpoint is **unauthenticated by default**. In production, put it behind a rate limiter or an `IAuthHook` that validates an initial access token, a mutual-TLS certificate, or a software statement before allowing registration. Otherwise, anyone who can reach the endpoint can mint client credentials.

Consider disabling dynamic registration entirely and managing clients via the admin API in environments where self-service registration is not a requirement.

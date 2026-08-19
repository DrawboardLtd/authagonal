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
| `redirect_uris` | conditional | Required when `grant_types` contains `authorization_code`. Must be absolute URIs; `javascript:`/`data:`/`vbscript:`/`file:` schemes are rejected (native custom schemes for mobile deep links are fine). At most 20 entries, each at most 2048 characters. |
| `post_logout_redirect_uris` | no | Valid redirect targets after logout. Same 20-entry / 2048-character bounds as `redirect_uris`. |
| `grant_types` | no | Defaults to `["authorization_code"]`. **Only `authorization_code` and `refresh_token` are registrable**: `client_credentials`, `implicit`, device and any other grant type are rejected with `invalid_client_metadata`, so open registration can never mint a machine-to-machine client. `refresh_token` is added automatically if `offline_access` is requested. |
| `token_endpoint_auth_method` | no | `client_secret_basic` (default), `client_secret_post`, or `none` for public clients |
| `scope` | no | Space-separated scopes. Only the four OIDC built-ins (`openid`, `profile`, `email`, `offline_access`) plus whatever `Auth:DynamicClientRegistrationScopes` names are registrable — existence in the scope store is **not** enough (see [Scopes](scopes)). Role-gated scopes and the administrative scope (`AdminApi:Scope`, default `authagonal-admin`) can never be registered. |
| `audiences` | no | JWT `aud` values added to access tokens |
| `allowed_cors_origins` | no | Origins permitted to call the token endpoint from a browser |
| `backchannel_logout_uri` | no | Enables [Back-Channel Logout](index#features) |
| `frontchannel_logout_uri` | no | Enables [Front-Channel Logout](front-channel-logout) |
| `frontchannel_logout_session_required` | no | Defaults to `true`; when `true`, the logout URL carries `iss` and `sid` parameters |

## Defaults & Invariants

- **PKCE required**: `RequirePkce` is always `true` for dynamically registered clients.
- **Public clients**: `token_endpoint_auth_method: "none"` produces a client without a secret. PKCE is still required.
- **Offline access**: requesting scope `offline_access` implicitly adds `refresh_token` to `grant_types`.

## Error Responses

| HTTP | `error` | Cause |
|---|---|---|
| `400` | `invalid_redirect_uri` | One of `redirect_uris` is not a valid absolute URI, or uses a script/data/file pseudo-scheme |
| `400` | `invalid_client_metadata` | A non-registrable grant type was requested, or `redirect_uris` is missing for a grant type that requires it |
| `400` | `invalid_scope` | A requested scope is neither built-in nor registered |
| `400` | `invalid_client_metadata` | More than 20 `redirect_uris` / `post_logout_redirect_uris` |
| `403` | `invalid_scope` | A requested scope is not registrable: not in `Auth:DynamicClientRegistrationScopes`, or role-gated |
| `403` | `invalid_scope` | The administrative scope was requested, it can never be granted through registration |
| `403` | `not_supported` | Dynamic client registration is not enabled |
| `429` | `rate_limited` | Too many registrations from this IP (10 per hour) |

## Security Considerations

The registration endpoint is **unauthenticated**, but constrained by design:

- **Rate limited**: 10 registrations per IP per rolling hour (`429 rate_limited`), so the client store can't be flooded.
- **Grant types restricted**: only `authorization_code` + `refresh_token`; a registered client always requires a user-mediated flow and can never act as a machine-to-machine client.
- **Scopes allowlisted, not inherited**: a registrant may declare the four OIDC built-ins and nothing else unless an operator lists a scope in `Auth:DynamicClientRegistrationScopes`. Existence in the scope store is not permission — a scope exists because some client needs it, not because every anonymous registrant may claim it.
- **Admin scope reserved**: the `authagonal-admin` scope (or whatever `AdminApi:Scope` is set to) is refused, so registration can never produce a client that reaches the [admin API](admin-api).
- **Logout URIs validated**: `backchannel_logout_uri` and `frontchannel_logout_uri` are dereferenced by the server, so they must be external http(s) endpoints — loopback, RFC1918, link-local (including the cloud metadata address) and `.internal`/`.local` hosts are refused.
- **Bounded records**: at most 20 redirect URIs of at most 2048 characters each, so one registration cannot be used to inflate the client store.
- **PKCE always required** on registered clients.

What it does **not** constrain, unless the registrant opts in, is the audience. RFC 7591 has no field for one, so a stock registration omits `audiences` (an Authagonal extension) entirely — the client was never asked, its list is "unset", and it may name any absolute URI as its `resource` at the authorization endpoint and receive a token carrying that value as `aud`. That is deliberate (the MCP authorization spec requires clients to name the MCP server as the resource, and an MCP client is a DCR client), and it makes the resource server responsible for authorizing on `scope` rather than on `iss` + `aud` + `sub`. **Sending** `audiences`, even as an empty list, is an answer and pins the client to it: a non-empty list is the allowlist for `resource`, and an explicit `[]` means the client may not name a resource at all. Token exchange is the exception: there an unset `Audiences` denies outright, so a registered client cannot aim an exchanged token anywhere. See [Audiences and resource indicators](configuration#audiences-and-resource-indicators-rfc-8707).

For stronger gating (initial access tokens, mTLS, software statements), front the endpoint with your own middleware or an `IAuthHook`. Consider disabling dynamic registration entirely and managing clients via the admin API in environments where self-service registration is not a requirement.

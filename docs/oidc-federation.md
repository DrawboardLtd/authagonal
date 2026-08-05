---
layout: default
title: OIDC Federation
---

# OIDC Federation

Authagonal can federate authentication to external OIDC identity providers (Google, Apple, Azure AD, etc.). This allows "Login with Google"-style flows while Authagonal remains the central auth server.

## How It Works

There are two entry paths into federation:

**Domain-based (interactive login):**

1. User enters their email on the login page
2. SPA calls `/api/auth/sso-check`, if the email domain is linked to an OIDC provider, SSO is required
3. User clicks "Continue with SSO" → redirected to the external IdP
4. After authenticating, the IdP redirects back to `/oidc/callback`
5. Authagonal validates the id_token, creates/links the user, and sets a session cookie

**RP-hinted (`idp_hint`):**

The downstream relying party can route directly to a specific upstream IdP without going through the email/SSO-domain step. Append `idp_hint={connectionId}` to `/connect/authorize`:

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

When the request is unauthenticated, Authagonal redirects to `/oidc/{connectionId}/login` with the original `/authorize` URL preserved as `returnUrl`. After federation completes, the user lands back at `/authorize` with a session cookie and the flow proceeds normally.

## Setup

### 1. Create an OIDC Provider

**Option A, Configuration (recommended for static setups):**

Add to `appsettings.json`:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Providers are seeded on startup. The seedable fields are exactly those shown, minus `RedirectUrl`: `ConnectionId`, `ConnectionName`, `MetadataLocation`, `ClientId`, `ClientSecret`, `AllowedDomains`. `RedirectUrl` is accepted for compatibility and ignored — the redirect URI is derived per request as `{Issuer}/oidc/callback`, since it has to be on the origin the browser is on, and that is the URI to register with the IdP. The `ClientSecret` is protected via `ISecretProvider` (Key Vault when configured, plaintext otherwise). SSO domain mappings are registered automatically from `AllowedDomains`.

> **An IdP on your own private network.** `MetadataLocation` must be https and, by default, must resolve to a publicly routable address — Authagonal refuses internal targets on every URL it fetches, at the URL and again at the socket. To federate with an on-premises IdP, name it in [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard). That covers the whole exchange, including the `token_endpoint`, `userinfo_endpoint` and `jwks_uri` the discovery document names. https is still required: this document supplies the keys every upstream `id_token` is validated against, and a private network is not a secure channel.

The connection model carries additional optional behavior, `PassthroughParams` (settable via the admin API create), plus `SessionExpClaim` and `DisableJitProvisioning` (store-level fields, set via `IOidcProviderStore` from hosting code), see [Scope and claim flow-through](#scope-and-claim-flow-through) and [Session lifetime cap](#session-lifetime-cap) below.

**Option B, Admin API (for runtime management):**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"]
  }'
```

### 2. SSO Domain Routing

When `AllowedDomains` is specified (in config or via the create API), SSO domain mappings are registered automatically. Without domain routing, users can still be directed to the OIDC login via `/oidc/{connectionId}/login`.

## Endpoints

| Endpoint | Description |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | Initiates OIDC login. Generates PKCE + state + nonce, derives the upstream scope and passthrough params from `returnUrl`, redirects to the IdP's authorization endpoint. |
| `GET /oidc/callback` | Handles the IdP callback. Exchanges the code for tokens, validates the id_token, captures every non-protocol claim onto the cookie as `federated:*`, creates/signs in the user. |

## Scope and claim flow-through

The scope set requested by the downstream RP at `/connect/authorize` is forwarded to the upstream IdP, **filtered to the standard OIDC set**: `openid`, `profile`, `email`, `address`, `phone`, with `openid` always included. Anything else the RP requested (custom API scopes, `offline_access`, …) is dropped before the upstream call: a strict IdP like Google returns `invalid_scope` on unknown values, and the upstream only needs to identify the user, the RP's own scopes are honored on Authagonal-issued tokens, not upstream ones. Whatever claims the upstream IdP scope-gates onto the id_token come back to Authagonal, get stashed on the cookie ticket as `federated:<name>` claims, and ride through into `OidcSubject.FederationClaims` at the next `/connect/authorize` traversal. From there `ProtocolTokenService` re-emits them on Authagonal-issued tokens, gated by the same `Scope.UserClaims` whitelist that gates `CustomAttributes`. On key collision the value in Authagonal's own user store wins: these claims arrive verbatim from the upstream IdP, so letting them overwrite would let a customer-controlled IdP restate any scope-released claim about their own user and beat this server's record of it. An upstream claim with no stored counterpart still flows through.

Net effect: no per-connection allowlist of claims to preserve. Every non-protocol claim the upstream puts on the id_token is captured; which of them reach downstream tokens is controlled by the downstream scope's `UserClaims`, declare the claim there and the value flows through.

`FederationClaims` survives refresh rotations distinct from `CustomAttributes`, so per-session federation context (e.g. a share-link token captured at the original authorize) stays intact while per-user attributes still re-read fresh from the user store.

## Passthrough query parameters

`OidcProviderConfig.PassthroughParams` is a per-connection whitelist of query keys that flow through from the original `/authorize` request onto the upstream IdP's authorize URL. The standard set (`scope`, `state`, `nonce`, PKCE) is always forwarded; this is for additional, RP-specified values like a one-shot credential the upstream needs to authenticate (e.g. `link_token` for share-link IdPs).

When a key is whitelisted, Authagonal pulls its value from the original `/authorize` query (carried via `returnUrl`) and appends it to the upstream URL. Anything not on the whitelist is dropped silently.

## Session lifetime cap

`OidcProviderConfig.SessionExpClaim` is the optional name of an id_token claim (Unix seconds) whose value caps the local session lifetime. When present, the upstream value rides through as `session_max_exp` on the cookie ticket and into the issued auth code; access / id / refresh tokens are clamped so no token, including those minted from rotations, outlives the upstream session. Useful when the upstream IdP enforces shorter session bounds than Authagonal would by default.

## Security Features

- **PKCE**: code_challenge with S256 on every authorization request
- **Nonce validation**: nonce stored with the state, must be present in the id_token and match
- **State validation**: single-use (consumed atomically via `IOidcStateStore`, persisted with expiry) **and browser-bound**: a `SameSite=Lax` cookie scoped to `/oidc` is set at login and must match the `state` on the callback, so an attacker can't complete a federation flow they started and deliver the callback URL to a victim (login CSRF)
- **id_token signature validation**: keys fetched from the IdP's JWKS endpoint; issuer, audience and lifetime validated
- **Userinfo fallback**: if the id_token doesn't contain an email, the userinfo endpoint is tried. The userinfo `sub` must match the id_token `sub` (OIDC Core 5.3.2), otherwise the response is ignored
- **Stable identity linking**: a returning user is resolved by provider + `sub`, never by email alone. Attaching a federated identity to a **pre-existing** local account by email requires the connection's `AllowedDomains` to cover that email's domain, the admin's explicit vouch that the IdP owns it. An upstream-asserted `email_verified` is *not* sufficient to seize an existing account
- **Domain enforcement**: when `AllowedDomains` is set, the connection may only assert identities within those domains (`access_denied` otherwise)
- **JIT opt-out**: `DisableJitProvisioning` rejects unknown users instead of auto-creating them
- **Open-redirect guard**: `returnUrl` must be a same-site relative path; protocol-relative (`//`) and backslash forms are rejected
- **Local MFA still applies**: federation proves the first factor only. A user who is MFA-enrolled (or whose client policy requires MFA) is routed through the local MFA challenge/setup pages after the callback instead of being signed straight in; only then does the session carry the MFA marker

## Azure AD Specifics

Azure AD sometimes returns emails as a JSON array in the `emails` claim (especially for B2C). Authagonal handles this by checking both the `email` claim and the `emails` array.

## Supported Providers

Any OIDC-compliant provider that supports:
- Authorization Code flow
- PKCE (S256)
- Discovery document (`.well-known/openid-configuration`)

Tested with:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C

## Related Guides

- [Self-Service SSO](self-service-sso) — JIT provisioning postures (invite-only vs. self-service), the connection trust tier, and pre-federation interstitials.
- [Federated Sessions](federated-sessions) — propagate upstream revocation to the local session with `RevalidateOnRefresh`.
- [Upgrading a User](user-upgrade) — let a federated / guest account claim a first-party password.

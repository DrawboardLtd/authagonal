---
layout: default
title: Federated Sessions
---

# Keeping Federated Sessions in Sync With the Upstream

When a user signs in through an [external IdP](oidc-federation), Authagonal issues its
*own* session and tokens. By default that local session then lives its own life: if the customer disables
the user in their directory, or the guest's share link is revoked upstream, the local Authagonal session
keeps working until its cookie expires.

For offboarding and revocation to propagate quickly, turn on **`RevalidateOnRefresh`**. Then, on every local
token refresh, Authagonal redeems the upstream refresh token against the IdP — and if the upstream says the
credential is gone, the local refresh is rejected and the session dies within one access-token lifetime.

## Enable it

Per connection, and the upstream must actually issue a refresh token:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "MetadataLocation": "…", "ClientId": "…", "ClientSecret": "…",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["acme.com"],
      "RevalidateOnRefresh": true
    }
  ]
}
```

That's the whole setup. Authagonal automatically requests `offline_access` on the federation hop so the
upstream issues a refresh token, stashes it, and redeems it server-to-server on each local refresh. The
upstream refresh token is **never** emitted to a client — it lives encrypted in a durable per-session store
and is used only for revalidation.

## What happens on refresh

1. The RP refreshes an Authagonal token as usual (`grant_type=refresh_token` at `/connect/token`).
2. Authagonal redeems the upstream refresh token at the IdP's token endpoint:
   - **Success** → the local refresh proceeds; if the upstream rotated its token, the new one is stored and
     shared by every RP grant in the session.
   - **`invalid_grant`** → the federated credential is gone (user disabled, session revoked, token expired).
     The local refresh is **rejected** and the session ends.
   - **Any other 4xx** (e.g. `invalid_client` from a rotated/misconfigured secret) or a 5xx/transport blip
     → treated as **transient**: the session survives so an operator fault doesn't mass-log-out every
     federated user. Fix the config; nothing is lost.

Because there is **one** upstream token per browser session (keyed by user + connection + session), a
second RP that the user opens reads and rotates the *same* token — so one app's refresh can't leave another
app holding a dead copy.

## Nothing to implement

There is no interface to write here. The durable store
(`IUpstreamRefreshTokenStore`) is registered automatically by the Azure and AWS storage providers, and the
redemption is internal. You only flip `RevalidateOnRefresh` on the connections whose upstream owns a
revocable credential.

> **Reach:** this feature is active wherever the store is registered (the Azure Table and DynamoDB
> providers). Enable it on connections to a **trusted** upstream — in particular one that one-time-rotates
> its refresh tokens (Entra, Auth0) — and combine it with `IsExternalConnection` for third-party IdPs (see
> [Self-service SSO](self-service-sso)).

## Complementary: a hard session cap

`RevalidateOnRefresh` keeps a session honest against upstream revocation. If instead (or additionally) you
want the local session to never *outlive* the upstream's asserted session, set `SessionExpClaim` to the name
of an id_token claim carrying an expiry (Unix seconds). Authagonal clamps the local session and all tokens
minted from it — including refresh rotations — to that bound. See
[OIDC Federation → Session lifetime cap](oidc-federation).

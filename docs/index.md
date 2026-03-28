---
layout: default
title: Home
---

# Authagonal

OAuth 2.0 / OpenID Connect / SAML 2.0 authentication server backed by Azure Table Storage.

Authagonal replaces Duende IdentityServer + Sustainsys.Saml2 with a single, self-contained deployment. The server and login UI ship as one Docker image — the SPA is served from the same origin as the API, so cookie auth, redirects, and CSP all work without cross-origin complexity.

## Key Features

- **OIDC Provider** — authorization_code + PKCE, client_credentials, refresh_token grants with one-time rotation
- **SAML 2.0 SP** — homebrew implementation with full Azure AD support (signed response, assertion, or both)
- **Dynamic OIDC Federation** — connect to Google, Apple, Azure AD, or any OIDC-compliant IdP
- **TCC Provisioning** — Try-Confirm-Cancel provisioning into downstream apps at authorize time
- **Brandable Login UI** — runtime-configurable via a JSON file — logo, colors, custom CSS — no rebuild needed
- **Azure Table Storage** — low-cost, serverless-friendly storage backend
- **Admin APIs** — user CRUD, SAML/OIDC provider management, SSO domain routing, token impersonation

## Architecture

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

## Pages

- [Installation](installation) — Docker, docker-compose, building from source
- [Quick Start](quickstart) — get running in 5 minutes
- [Configuration](configuration) — server settings, clients, storage, provisioning apps
- [Branding](branding) — customize the login UI
- [Provisioning](provisioning) — TCC provisioning into downstream apps
- [SAML](saml) — SAML SP setup and Azure AD integration
- [OIDC Federation](oidc-federation) — connecting external OIDC identity providers
- [Admin API](admin-api) — user, SSO, and token management endpoints
- [Auth API](auth-api) — login, logout, password reset, session endpoints
- [Migration](migration) — migrating from Duende IdentityServer

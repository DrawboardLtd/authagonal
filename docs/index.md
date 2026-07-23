---
layout: default
title: Home
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

OAuth 2.0 / OpenID Connect / SAML 2.0 authentication server for .NET, backed by pluggable cloud storage, Azure Table Storage or AWS (DynamoDB / S3 / Secrets Manager).

A single, self-contained deployment. The server and login UI ship as one Docker image, the SPA is served from the same origin as the API, so cookie auth, redirects, and CSP all work without cross-origin complexity.

> **Prefer a managed service?** [Authagonal Cloud](https://authagonal.io) runs all of this for you, multi-tenant, every feature on every plan, no per-connection SSO fees. → [authagonal.io](https://authagonal.io)

## Key Features

- **OIDC Provider**: authorization_code + PKCE, client_credentials, refresh_token, device_code grants with one-time rotation
- **SAML 2.0 SP**: homebrew implementation with full Azure AD support (signed response, assertion, or both), a per-connection SP keypair for signed AuthnRequests + `EncryptedAssertion` decryption, and Single Logout (SP- and IdP-initiated)
- **Dynamic OIDC Federation**: connect to Google, Apple, Azure AD, or any OIDC-compliant IdP
- **Multi-Factor Authentication**: TOTP, WebAuthn/passkeys, recovery codes; per-client policy (`Disabled` / `Enabled` / `Required`) with `IAuthHook` per-user override, enforced for federated logins too
- **SCIM 2.0 Provisioning**: inbound user/group provisioning from Entra ID, Okta, OneLogin; cursor-paged listing and blind-index-backed `eq` filters
- **OAuth Consent Screen**: per-client consent with scope-aware re-prompt and grant management
- **Device Authorization Grant**: RFC 8628 flow for input-constrained devices (smart TVs, CLIs, IoT)
- **Token Introspection**: RFC 7662 for resource servers to verify token validity
- **Back-Channel Logout**: OIDC Back-Channel Logout 1.0 notifications to relying parties
- **GDPR Self-Service**: data export and scheduled account deletion from the hosted account page
- **TCC Provisioning**: Try-Confirm-Cancel provisioning into downstream apps at authorize time
- **Brandable Login UI**: runtime-configurable via a JSON file, logo, colors, CSS custom properties, no rebuild needed; localized into 10 languages
- **Auth Hooks**: `IAuthHook` extensibility for audit logging, custom validation, webhooks
- **PII Encryption Seams**: `IFieldCipher` / `IIndexTokenizer` extension points for field-level encryption at rest with keyed blind-index (HMAC) search; recovery codes encrypted via `ISecretProvider`
- **HashiCorp Vault Transit**: remote JWT signing without local private key access
- **Composable Library**: `AddAuthagonal()` / `UseAuthagonal()` to host in your own project with custom service overrides
- **Native AOT Ready**: IL trimming and source-generated JSON serialization for fast startup
- **Pluggable cloud storage**: Azure Table Storage or AWS (DynamoDB / S3 / Secrets Manager); low-cost, serverless-friendly backends
- **Backup & Restore**: incremental backups (change-log-driven with a full-scan backstop), integrity verification, tombstone-based delete tracking
- **Admin APIs**: user CRUD, SAML/OIDC provider management, SSO domain routing, token impersonation

## Common Integrations

Task-oriented guides for the flows teams build most often:

- **[Upgrading a User](user-upgrade)** — turn a guest / SSO / invite account into a credentialed one via the passwordless account claim, and run your guest → standard-member promotion on confirm.
- **[Self-Service SSO](self-service-sso)** — JIT provisioning for enterprise connections: invite-only vs. self-service onboarding, keeping external IdPs from becoming foot-guns, and pre-federation interstitials.
- **[Federated Sessions](federated-sessions)** — revoke the local session when the upstream IdP does (`RevalidateOnRefresh`).
- **[WebSocket Auth](websocket-auth)** — authenticate browser WebSockets through the BFF without exposing a token.

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

Get started with the [Installation](installation) guide or jump straight to the [Quick Start](quickstart). To host Authagonal in your own project, see [Extensibility](extensibility). For data management, see [Backup & Restore](backup-restore). For the full change history, see the [Changelog](https://github.com/authagonal/authagonal/blob/master/CHANGELOG.md).

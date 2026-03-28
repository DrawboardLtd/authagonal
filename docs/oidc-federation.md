---
layout: default
title: OIDC Federation
---

# OIDC Federation

Authagonal can federate authentication to external OIDC identity providers (Google, Apple, Azure AD, etc.). This allows "Login with Google"-style flows while Authagonal remains the central auth server.

## How It Works

1. User enters their email on the login page
2. SPA calls `/api/auth/sso-check` — if the email domain is linked to an OIDC provider, SSO is required
3. User clicks "Continue with SSO" → redirected to the external IdP
4. After authenticating, the IdP redirects back to `/oidc/callback`
5. Authagonal validates the id_token, creates/links the user, and sets a session cookie

## Setup

### 1. Create an OIDC Provider

```bash
curl -X POST https://auth.example.com/api/v1/sso/oidc \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionId": "google",
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret"
  }'
```

The `clientSecret` can be a plaintext value or a Key Vault reference (when `SecretProvider:VaultUri` is configured).

### 2. Add SSO Domains (Optional)

To require Google SSO for specific email domains:

```bash
curl -X POST https://auth.example.com/api/v1/sso/domains \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "example.com",
    "providerType": "oidc",
    "connectionId": "google"
  }'
```

Without domain routing, users can still be directed to the OIDC login via `/oidc/{connectionId}/login`.

## Endpoints

| Endpoint | Description |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | Initiates OIDC login. Generates PKCE + state + nonce, redirects to the IdP's authorization endpoint. |
| `GET /oidc/callback` | Handles the IdP callback. Exchanges the code for tokens, validates the id_token, creates/signs in the user. |

## Security Features

- **PKCE** — code_challenge with S256 on every authorization request
- **Nonce validation** — nonce stored in state, verified in the id_token
- **State validation** — single-use, stored in Azure Table Storage with expiry
- **id_token signature validation** — keys fetched from the IdP's JWKS endpoint
- **Userinfo fallback** — if the id_token doesn't contain an email, the userinfo endpoint is tried

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

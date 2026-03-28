---
layout: default
title: Quick Start
---

# Quick Start

Get Authagonal running locally in 5 minutes.

## 1. Start the Server

```bash
docker compose up
```

This starts Authagonal on `http://localhost:8080` with Azurite for storage.

## 2. Verify It's Running

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. Register a Client

Add a client to your `appsettings.json` (or pass via environment variables):

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

Clients are seeded on startup — safe to run on every deployment.

## 4. Initiate a Login

Redirect your users to:

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

The user sees the login page, authenticates, and is redirected back with an authorization code.

## 5. Exchange the Code

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

Response:

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800
}
```

## Next Steps

- [Configuration](configuration) — full reference for all settings
- [Branding](branding) — customize the login UI
- [SAML](saml) — add SAML SSO providers
- [Provisioning](provisioning) — provision users into downstream apps

[← Back to home](.)

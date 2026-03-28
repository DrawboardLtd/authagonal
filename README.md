# Authagonal

OAuth 2.0 / OpenID Connect / SAML 2.0 authentication server backed by Azure Table Storage.

Architecture: API-only ASP.NET Core server + React login SPA, packaged as a single Docker image.

**[Documentation](https://drawboardltd.github.io/authagonal/)**

## Quick Start

```bash
docker compose up
```

This starts the auth server on `http://localhost:8080` with an Azurite storage emulator.

## Projects

| Project | Description |
|---|---|
| `src/Authagonal.Core` | Domain models, interfaces, shared logic |
| `src/Authagonal.Storage` | Azure Table Storage implementations |
| `src/Authagonal.Server` | ASP.NET Core host — OIDC, SAML, auth API, admin API |
| `login-app` | React/TypeScript login SPA (Vite) |
| `tools/Authagonal.Migration` | SQL Server → Table Storage migration tool |
| `tests/Authagonal.Tests` | Unit tests |

## Features

- **OIDC Provider** — authorization_code + PKCE, client_credentials, refresh_token grants
- **SAML 2.0 SP** — homebrew implementation, full Azure AD support
- **Dynamic OIDC Federation** — Google, Apple, Azure AD
- **TCC Provisioning** — Try-Confirm-Cancel provisioning into downstream apps at authorize time
- **Session Invalidation** — SecurityStamp rotation on org change, password reset
- **Admin APIs** — user CRUD, SAML/OIDC provider management, token impersonation
- **Brandable Login UI** — runtime-configurable branding via `branding.json`

## Docker

```bash
# Build server image
docker build -t authagonal .

# Build migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## Development

```bash
# Server (requires .NET 10 SDK)
dotnet run --project src/Authagonal.Server

# Login app
cd login-app && npm install && npm run dev
```

## Configuration

See the [full documentation](https://drawboardltd.github.io/authagonal/) for configuration reference.

## License

Proprietary — Drawboard Ltd.

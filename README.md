# Authagonal

Replacement for Duende IdentityServer + Sustainsys.Saml2, backed by Azure Table Storage.

Architecture: API-only .NET server + React login SPA.

## Projects

| Project | Description |
|---|---|
| `src/Authagonal.Core` | Domain models, interfaces, token service, password hashing, SAML/OIDC logic |
| `src/Authagonal.Storage` | Azure Table Storage implementations |
| `src/Authagonal.Server` | ASP.NET Core host — OIDC endpoints, auth API, admin API, SAML endpoints |
| `login-app` | React/TypeScript login SPA (Vite) |
| `tools/Authagonal.Migration` | SQL Server → Table Storage migration tool |
| `tests/Authagonal.Tests` | Integration and unit tests |

## Auth Flow

```
/connect/authorize → 302 to login app → POST /api/auth/login → cookie set → redirect back → 302 with auth code
```

## Features

- **OIDC Provider**: authorization_code + PKCE, client_credentials, refresh_token grants
- **SAML SP**: Homebrew implementation, full Azure AD support (signed response/assertion/both)
- **Dynamic OIDC Federation**: Google, Apple, Azure AD
- **User Provisioning**: Webhook-based approve/reject for all user creation paths
- **Session Invalidation**: SecurityStamp rotation on org change, password reset
- **Admin APIs**: User CRUD, SAML/OIDC provider management, token impersonation

## Prerequisites

- .NET 9 SDK
- Node.js 20+
- Azure Storage account (or Azurite for local dev)

## Getting Started

```bash
# Server
dotnet build
dotnet run --project src/Authagonal.Server

# Login app
cd login-app
npm install
npm run dev
```

## Migration

```bash
dotnet run --project tools/Authagonal.Migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] [--MigrateRefreshTokens true]
```

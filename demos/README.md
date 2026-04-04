# Authagonal Demos

**Live demo: [demo.authagonal.drawboard.com](https://demo.authagonal.drawboard.com)** (auth server: [sso.demo.authagonal.drawboard.com](https://sso.demo.authagonal.drawboard.com))

Two demo projects showing how to use Authagonal:

## 1. Custom Server (`custom-server/`)

An ASP.NET host that uses Authagonal as a library with custom extensions:

- **`AuditAuthHook`** — custom `IAuthHook` that logs every authentication event
- **`ConsoleEmailService`** — custom `IEmailService` that prints emails to the console
- **Custom branding** — green color scheme, custom app name via `branding.json`
- **Custom endpoint** — adds `/custom/health` alongside the standard Authagonal endpoints

This demonstrates the "host Authagonal in your own project" model — reference the NuGet packages, register your overrides before `AddAuthagonal()`, and the framework uses your implementations.

### Run locally

```bash
# Start Azurite (Azure Table Storage emulator)
docker run -p 10002:10002 mcr.microsoft.com/azure-storage/azurite azurite-table --tableHost 0.0.0.0

# Build the login SPA and start the server
cd demos/custom-server
dotnet run
```

## 2. Sample App (`sample-app/`)

A client application that authenticates users via Authagonal:

- **SampleApp.Api** — ASP.NET Web API that validates JWTs issued by Authagonal
- **frontend** — React SPA that implements OIDC authorization code + PKCE (no libraries)

This demonstrates the "use Authagonal as your IdP" model — your app redirects to Authagonal for login, receives tokens, and calls your API with bearer tokens.

### Run locally

```bash
# Terminal 1: Authagonal (use the custom-server or stock Docker image)
cd demos/custom-server && dotnet run

# Terminal 2: Sample API
cd demos/sample-app/SampleApp.Api && dotnet run

# Terminal 3: Sample frontend
cd demos/sample-app/frontend && npm install && npm run dev
```

Then open http://localhost:3000 and click "Sign in with Authagonal".

## Architecture

```
Browser (http://localhost:3000)
   │
   ├── React SPA ───────────────────────────────────────────┐
   │   └── Sign in → redirect to Authagonal                 │
   │                                                        │
   ├── GET /connect/authorize ──► Authagonal (localhost:8080)│
   │   └── Login UI (cookie auth)                           │
   │   └── 302 → callback?code=xxx                          │
   │                                                        │
   ├── POST /connect/token ────► Authagonal (localhost:8080) │
   │   └── { access_token, id_token, refresh_token }        │
   │                                                        │
   └── GET /api/protected ─────► Sample API (localhost:5001) │
       └── Authorization: Bearer <access_token>             │
       └── API validates JWT signature via Authagonal's JWKS│
```

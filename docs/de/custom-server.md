---
layout: default
title: Benutzerdefinierter Server
locale: de
---

# Benutzerdefinierter Server -- Schnellstart

Diese Anleitung zeigt, wie Sie Authagonal als Bibliothek in Ihrem eigenen ASP.NET Core-Projekt hosten und anschliessend die Login-Oberflaeche mit Ihren eigenen React-Komponenten anpassen.

## Teil 1: Server-Einrichtung

### Projekt erstellen

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.Storage
```

Ihre `.csproj` sollte enthalten:

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.Storage" Version="*" />
</ItemGroup>
```

### Program.cs konfigurieren

Die Minimaleinrichtung besteht aus drei Aufrufen: `AddAuthagonal`, `UseAuthagonal` und `MapAuthagonalEndpoints`.

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Register custom services BEFORE AddAuthagonal — yours take precedence
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, ConsoleEmailService>();

// 2. Register Authagonal
builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();

// 3. Middleware + endpoints
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// 4. Serve the login SPA from wwwroot
app.MapFallbackToFile("index.html");

app.Run();
```

### appsettings.json konfigurieren

```json
{
  "Issuer": "https://auth.example.com",
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=..."
  },
  "Clients": [
    {
      "Id": "my-app",
      "Name": "My Application",
      "GrantTypes": ["authorization_code", "refresh_token"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "Scopes": ["openid", "profile", "email", "offline_access"],
      "CorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireSecret": false,
      "AllowOfflineAccess": true
    }
  ]
}
```

| Schluessel | Beschreibung |
|---|---|
| `Issuer` | Die oeffentliche URL Ihres Auth-Servers. Wird in Tokens und OIDC Discovery verwendet. |
| `Storage:ConnectionString` | Azure Table Storage Verbindungszeichenfolge. |
| `Clients` | Array von OAuth-Clients, die beim Start geseedet werden. |

### Erweiterungspunkte

Registrieren Sie Ihre Implementierungen **vor** dem Aufruf von `AddAuthagonal()` -- Authagonal verwendet `TryAdd`, sodass Ihre Registrierungen Vorrang haben.

| Schnittstelle | Zweck | Standard |
|---|---|---|
| `IEmailService` | Versand von Verifizierungs- und Passwortzuruecksetzungs-E-Mails | No-op (verwirft stillschweigend) |
| `IAuthHook` | Login-, Registrierungs- und Token-Ereignisse abfangen oder auditieren | Leeroperationen |
| `IProvisioningOrchestrator` | Benutzer bei der Autorisierung in nachgelagerte Apps bereitstellen | TCC-Bereitstellung |
| `ISecretProvider` | Client-Geheimnisse aufloesen | Klartext (oder Key Vault mit `SecretProvider:VaultUri`) |

#### Beispiel: Audit-Hook

```csharp
using Authagonal.Core.Services;

public class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId = null, CancellationToken ct = default)
    {
        logger.LogInformation("Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct = default)
    {
        logger.LogInformation("New user: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default)
    {
        logger.LogWarning("Failed login: {Email} — {Reason}", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct = default)
    {
        logger.LogInformation("Token issued: {ClientId} ({GrantType})", clientId, grantType);
        return Task.CompletedTask;
    }
}
```

#### Beispiel: E-Mail-Dienst

```csharp
using Authagonal.Core.Services;

public class ConsoleEmailService(ILogger<ConsoleEmailService> logger) : IEmailService
{
    public Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        logger.LogInformation("Verify email: {Url}", callbackUrl);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        logger.LogInformation("Reset password: {Url}", callbackUrl);
        return Task.CompletedTask;
    }
}
```

### Benutzerdefinierte Endpunkte hinzufuegen

Sie koennen neben den Authagonal-Endpunkten eigene Endpunkte hinzufuegen:

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### Admin-API deaktivieren

Fuer oeffentlich zugaengliche Deployments deaktivieren Sie die Admin-Endpunkte:

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### Ausfuehren

```bash
dotnet run
```

Der Server startet auf der konfigurierten URL und stellt das OIDC Discovery-Dokument unter `/.well-known/openid-configuration`, die Login-Oberflaeche unter `/login` sowie alle Auth-/Admin-APIs bereit.

---

## Teil 2: Benutzerdefinierte Login-Oberflaeche

Die Standard-Login-SPA funktioniert sofort, aber Sie koennen sie durch Ihre eigene React-App ersetzen, die Komponenten und API-Clients aus dem `@drawboard/authagonal-login` npm-Paket importiert.

### Frontend-Projekt aufsetzen

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router-dom @drawboard/authagonal-login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### Was das npm-Paket exportiert

```typescript
// Components — use as-is or as reference
import {
  AuthLayout,
  LoginPage,
  ForgotPasswordPage,
  ResetPasswordPage,
  MfaChallengePage,
  MfaSetupPage,
} from '@drawboard/authagonal-login';

// UI primitives
import {
  Button, Input, Label, Card, Alert, Separator, cn,
} from '@drawboard/authagonal-login';

// API clients — call from your custom pages
import {
  login, logout, ssoCheck, forgotPassword, resetPassword,
  getSession, getProviders, getPasswordPolicy,
  mfaVerify, mfaStatus, mfaTotpSetup, mfaTotpConfirm,
  mfaWebAuthnSetup, mfaWebAuthnConfirm, mfaRecoveryGenerate,
  mfaDeleteCredential,
  ApiRequestError,
} from '@drawboard/authagonal-login';

// Branding
import {
  loadBranding, useBranding, BrandingContext, resolveLocalized,
} from '@drawboard/authagonal-login';

// i18n — always import from this package, not react-i18next directly
import { useTranslation, i18n } from '@drawboard/authagonal-login';

// Styles
import '@drawboard/authagonal-login/styles.css';

// Types
import type {
  BrandingConfig, LocalizedString, LoginResponse,
  SessionResponse, ExternalProvider, PasswordPolicyResponse,
  MfaStatusResponse, MfaTotpSetupResponse,
} from '@drawboard/authagonal-login';
```

### Einstiegspunkt (main.tsx)

Branding vom Server laden und Ihre App in den Branding-Kontext einbetten:

```tsx
import { createRoot } from 'react-dom/client';
import { loadBranding, BrandingContext } from '@drawboard/authagonal-login';
import '@drawboard/authagonal-login/styles.css';
import App from './App';

loadBranding().then((config) => {
  document.title = `Sign In — ${config.appName}`;
  createRoot(document.getElementById('root')!).render(
    <BrandingContext.Provider value={config}>
      <App />
    </BrandingContext.Provider>
  );
});
```

### Routing (App.tsx)

Benutzerdefinierte Seiten mit den Basispaket-Seiten kombinieren:

```tsx
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { ForgotPasswordPage, ResetPasswordPage } from '@drawboard/authagonal-login';
import MyLoginPage from './MyLoginPage';
import MyLayout from './MyLayout';

export default function App() {
  return (
    <BrowserRouter>
      <MyLayout>
        <Routes>
          <Route path="/login" element={<MyLoginPage />} />
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/reset-password" element={<ResetPasswordPage />} />
          <Route path="*" element={<Navigate to="/login" replace />} />
        </Routes>
      </MyLayout>
    </BrowserRouter>
  );
}
```

### Benutzerdefinierte Login-Seite

Erstellen Sie Ihr eigenes Login-Formular mit den API-Clients aus dem npm-Paket:

```tsx
import { useState } from 'react';
import { login, ssoCheck, ApiRequestError, useBranding } from '@drawboard/authagonal-login';

export default function MyLoginPage() {
  const branding = useBranding();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    try {
      await login(email, password);
      // Login sets a cookie — redirect to the return URL
      const params = new URLSearchParams(window.location.search);
      window.location.href = params.get('returnUrl') || '/';
    } catch (err) {
      if (err instanceof ApiRequestError) {
        setError(err.message || 'Login failed');
      }
    }
  };

  return (
    <form onSubmit={handleSubmit}>
      <h1>Sign in to {branding.appName}</h1>
      {error && <p className="error">{error}</p>}
      <input
        type="email"
        value={email}
        onChange={(e) => setEmail(e.target.value)}
        placeholder="Email"
        required
      />
      <input
        type="password"
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        placeholder="Password"
        required
      />
      <button type="submit">Sign in</button>
    </form>
  );
}
```

### Benutzerdefiniertes Layout

Umschliessen Sie das Basis-`AuthLayout`, um Ihr eigenes Branding hinzuzufuegen:

```tsx
import { AuthLayout } from '@drawboard/authagonal-login';

export default function MyLayout({ children }: { children: React.ReactNode }) {
  return (
    <>
      <AuthLayout>{children}</AuthLayout>
      <footer>
        &copy; {new Date().getFullYear()} My Company —
        <a href="/terms">Terms</a> | <a href="/privacy">Privacy</a>
      </footer>
    </>
  );
}
```

### Branding (wwwroot/branding.json)

Konfigurieren Sie das Erscheinungsbild der Login-Oberflaeche ohne Neuaufbau:

```json
{
  "appName": "My App",
  "logoUrl": "/logo.svg",
  "primaryColor": "#059669",
  "supportEmail": "support@example.com",
  "showForgotPassword": true,
  "customCssUrl": "/custom.css"
}
```

### Vite-Konfiguration

API-Aufrufe waehrend der Entwicklung an das Backend weiterleiten:

```typescript
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  resolve: {
    dedupe: ['react', 'react-dom'],
  },
  server: {
    proxy: {
      '/api': { target: 'http://localhost:5000', changeOrigin: true },
      '/connect': { target: 'http://localhost:5000', changeOrigin: true },
      '/saml': { target: 'http://localhost:5000', changeOrigin: true },
      '/oidc': { target: 'http://localhost:5000', changeOrigin: true },
    },
  },
});
```

### Erstellen und bereitstellen

Fuegen Sie ein Build-Target zu Ihrer `.csproj` hinzu, um die SPA automatisch zu erstellen und nach `wwwroot` zu kopieren:

```xml
<Target Name="BuildLoginApp" BeforeTargets="Build" Condition="!Exists('wwwroot/index.html')">
  <Exec Command="npm ci" WorkingDirectory="login-app" />
  <Exec Command="npm run build" WorkingDirectory="login-app" />
  <ItemGroup>
    <LoginAppFiles Include="login-app/dist/**/*" />
  </ItemGroup>
  <Copy SourceFiles="@(LoginAppFiles)" DestinationFolder="wwwroot/%(RecursiveDir)" />
</Target>
```

Jetzt erstellt `dotnet build` sowohl den .NET-Server als auch die React-SPA, und `dotnet run` bedient alles aus einem einzigen Prozess.

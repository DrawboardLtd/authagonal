---
layout: default
title: Benutzerdefinierter Server
locale: de
---

# Benutzerdefinierter Server -- Schnellstart

Diese Anleitung zeigt, wie Sie Authagonal als Bibliothek in Ihrem eigenen ASP.NET Core-Projekt hosten und anschließend die Login-Oberfläche mit Ihren eigenen React-Komponenten anpassen.

## Teil 1: Server-Einrichtung

### Projekt erstellen

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.AzureProvider
```

Ihre `.csproj` sollte enthalten:

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.AzureProvider" Version="*" />
</ItemGroup>
```

`Authagonal.AzureProvider` stellt die Azure Table Storage-Speicher bereit, die `AddAuthagonal` aus der `Storage:*`-Konfiguration verdrahtet. Um stattdessen auf AWS zu hosten, referenzieren Sie `Authagonal.AwsProvider` und rufen Sie `AddAuthagonalAwsStorage(...)` vor `AddAuthagonal` auf -- siehe [Installation → AWS-Backend](installation#aws-backend).

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

| Schlüssel | Beschreibung |
|---|---|
| `Issuer` | Die öffentliche URL Ihres Auth-Servers. Wird in Tokens und OIDC Discovery verwendet. |
| `Storage:ConnectionString` | Azure Table Storage Verbindungszeichenfolge. |
| `Clients` | Array von OAuth-Clients, die beim Start geseedet werden. |

### Erweiterungspunkte

Registrieren Sie Ihre Implementierungen **vor** dem Aufruf von `AddAuthagonal()` -- Authagonal verwendet `TryAdd`, sodass Ihre Registrierungen Vorrang haben.

| Schnittstelle | Zweck | Standard |
|---|---|---|
| `IEmailService` | Versand von Verifizierungs- und Passwortzurücksetzungs-E-Mails | Integrierter Resend-Absender, wenn `Email:ResendApiKey` gesetzt ist; andernfalls No-op (verwirft stillschweigend) |
| `IAuthHook` | Login-, Registrierungs- und Token-Ereignisse abfangen oder auditieren | Leeroperationen |
| `IProvisioningOrchestrator` | Benutzer bei der Autorisierung in nachgelagerte Apps bereitstellen | TCC-Bereitstellung |
| `ISecretProvider` | Client-Geheimnisse auflösen | Klartext (oder Key Vault mit `SecretProvider:VaultUri`) |

#### Beispiel: Audit-Hook

```csharp
using Authagonal.Core.Models;
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

    public Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default)
        => Task.FromResult(clientPolicy);

    public Task OnMfaVerifiedAsync(string userId, string email,
        string mfaMethod, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task OnUserUpdatedAsync(string userId, string email,
        string updatedVia, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task OnUserDeletedAsync(string userId, string email,
        string deletedVia, CancellationToken ct = default)
        => Task.CompletedTask;
}
```

Die Schnittstelle hat weitere optionale Mitglieder mit No-op-Standardimplementierungen (`OnMfaVerifyFailedAsync`, `OnEmailConfirmedAsync`, `OnMfaEnrolledAsync`, `OnMfaCredentialRemovedAsync`, `OnRecoveryCodesRegeneratedAsync`, `OnPasswordChangedAsync`), überschreiben Sie diese nur, wenn Sie diese Ereignisse benötigen.

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

> **E-Mail ist die häufigste Integrationsfalle.** Wenn Sie keinen `IEmailService` registrieren und `Email:ResendApiKey` nicht setzen, werden Verifizierungs- und Passwortzurücksetzungs-E-Mails stillschweigend verworfen, und da das Login-Gate für bestätigte E-Mails standardmäßig aktiviert ist, können sich selbst registrierte Benutzer niemals anmelden (`UseAuthagonal` warnt beim Start). Der integrierte Resend-Absender aktiviert sich automatisch, wenn `Email:ResendApiKey` + `Email:SenderEmail` konfiguriert sind; für Entwicklung/Tests überspringt `Auth:AutoConfirmEmailDomains` die Verifizierung für aufgelistete Domains. Siehe [Konfiguration → E-Mail](configuration#email).

### Benutzerdefinierte Endpunkte hinzufügen

Sie können neben den Authagonal-Endpunkten eigene Endpunkte hinzufügen:

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### Admin-API deaktivieren

Für öffentlich zugängliche Deployments deaktivieren Sie die Admin-Endpunkte:

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### Ausführen

```bash
dotnet run
```

Der Server startet auf der konfigurierten URL und stellt das OIDC Discovery-Dokument unter `/.well-known/openid-configuration`, die Login-Oberfläche unter `/login` sowie alle Auth-/Admin-APIs bereit.

---

## Teil 2: Benutzerdefinierte Login-Oberfläche

Die Standard-Login-SPA funktioniert sofort, aber Sie können sie durch Ihre eigene React-App ersetzen, die Komponenten und API-Clients aus dem `@authagonal/login` npm-Paket importiert.

### Frontend-Projekt aufsetzen

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router @authagonal/login
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
  RegisterPage,
  ConsentPage,
  GrantsPage,
  DevicePage,
  App,              // Standalone SPA with full routing
} from '@authagonal/login';

// UI primitives
import {
  Button, Input, Label, Card, Alert, Separator, cn,
} from '@authagonal/login';

// API clients — call from your custom pages
import {
  login, register, logout, ssoCheck, forgotPassword, resetPassword,
  getSession, getProviders, getPasswordPolicy,
  mfaVerify, mfaStatus, mfaTotpSetup, mfaTotpConfirm,
  mfaWebAuthnSetup, mfaWebAuthnConfirm, mfaRecoveryGenerate,
  mfaDeleteCredential,
  ApiRequestError,
} from '@authagonal/login';

// Branding
import {
  loadBranding, useBranding, BrandingContext, resolveLocalized,
} from '@authagonal/login';

// i18n — always import from this package, not react-i18next directly
import { useTranslation, i18n } from '@authagonal/login';

// Styles
import '@authagonal/login/styles.css';

// Types
import type {
  BrandingConfig, LocalizedString, LoginResponse,
  SessionResponse, ExternalProvider, PasswordPolicyResponse,
  MfaStatusResponse, MfaTotpSetupResponse,
} from '@authagonal/login';
```

### Einstiegspunkt (main.tsx)

Branding vom Server laden und Ihre App in den Branding-Kontext einbetten:

```tsx
import { createRoot } from 'react-dom/client';
import { loadBranding, BrandingContext } from '@authagonal/login';
import '@authagonal/login/styles.css';
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
import { BrowserRouter, Routes, Route, Navigate } from 'react-router';
import {
  ForgotPasswordPage, ResetPasswordPage, ConsentPage, DevicePage, GrantsPage,
} from '@authagonal/login';
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
          <Route path="/consent" element={<ConsentPage />} />
          <Route path="/device" element={<DevicePage />} />
          <Route path="/grants" element={<GrantsPage />} />
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
import { login, ssoCheck, ApiRequestError, useBranding } from '@authagonal/login';

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

Umschließen Sie das Basis-`AuthLayout`, um Ihr eigenes Branding hinzuzufügen:

```tsx
import { AuthLayout } from '@authagonal/login';

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

Konfigurieren Sie das Erscheinungsbild der Login-Oberfläche ohne Neuaufbau:

```json
{
  "appName": "My App",
  "logoUrl": "/logo.svg",
  "primaryColor": "#059669",
  "supportEmail": "support@example.com",
  "showForgotPassword": true,
  "showRegistration": false,
  "darkMode": "auto",
  "customCssUrl": "/custom.css"
}
```

Das vollständige Schema, einschließlich lokalisiertem Willkommenstext, der Sprachauswahlliste und modusabhängiger Dunkel-/Hell-Farb- und Logo-Hintergrund-Überschreibungen, finden Sie auf der [Branding](branding)-Seite.

### Vite-Konfiguration

API-Aufrufe während der Entwicklung an das Backend weiterleiten:

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

Fügen Sie ein Build-Target zu Ihrer `.csproj` hinzu, um die SPA automatisch zu erstellen und nach `wwwroot` zu kopieren:

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

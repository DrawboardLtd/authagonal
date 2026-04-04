---
layout: default
title: Serveur personnalise
locale: fr
---

# Demarrage rapide -- Serveur personnalise

Ce guide explique comment heberger Authagonal en tant que bibliotheque dans votre propre projet ASP.NET Core, puis personnaliser l'interface de connexion avec vos propres composants React.

## Partie 1 : Configuration du serveur

### Creer le projet

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.Storage
```

Votre fichier `.csproj` doit contenir :

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.Storage" Version="*" />
</ItemGroup>
```

### Configurer Program.cs

La configuration minimale comprend trois appels : `AddAuthagonal`, `UseAuthagonal` et `MapAuthagonalEndpoints`.

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

### Configurer appsettings.json

```json
{
  "Issuer": "https://auth.example.com",
  "Oidc": {
    "Issuer": "https://auth.example.com"
  },
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

| Cle | Description |
|---|---|
| `Issuer` | L'URL publique de votre serveur d'authentification. Utilisee dans les tokens et la decouverte OIDC. |
| `Oidc:Issuer` | Generalement identique a `Issuer`. Separe si vos URLs internes et externes different. |
| `Storage:ConnectionString` | Chaine de connexion Azure Table Storage. |
| `Clients` | Tableau de clients OAuth injectes au demarrage. |

### Points d'extension

Enregistrez vos implementations **avant** d'appeler `AddAuthagonal()` -- Authagonal utilise `TryAdd`, donc vos enregistrements ont la priorite.

| Interface | Fonction | Defaut |
|---|---|---|
| `IEmailService` | Envoi d'e-mails de verification et de reinitialisation de mot de passe | SendGrid (necessite une configuration) |
| `IAuthHook` | Intercepter ou auditer les evenements de connexion, d'inscription et de token | Aucune operation |
| `IProvisioningOrchestrator` | Provisionner les utilisateurs dans les applications en aval lors de l'autorisation | Provisionnement TCC |
| `ISecretProvider` | Resoudre les secrets client | Texte brut (ou Key Vault avec `SecretProvider:VaultUri`) |

#### Exemple : hook d'audit

```csharp
using Authagonal.Core.Services;

public class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email, string method)
    {
        logger.LogInformation("Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email)
    {
        logger.LogInformation("New user: {Email}", email);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason)
    {
        logger.LogWarning("Failed login: {Email} — {Reason}", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string clientId, string? userId, IEnumerable<string> scopes)
    {
        logger.LogInformation("Token issued to {ClientId}", clientId);
        return Task.CompletedTask;
    }
}
```

#### Exemple : service d'e-mail

```csharp
using Authagonal.Core.Services;

public class ConsoleEmailService(ILogger<ConsoleEmailService> logger) : IEmailService
{
    public Task SendVerificationEmailAsync(string email, string callbackUrl)
    {
        logger.LogInformation("Verify email: {Url}", callbackUrl);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetEmailAsync(string email, string callbackUrl)
    {
        logger.LogInformation("Reset password: {Url}", callbackUrl);
        return Task.CompletedTask;
    }
}
```

### Ajouter des endpoints personnalises

Vous pouvez ajouter vos propres endpoints a cote de ceux d'Authagonal :

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### Desactiver l'API d'administration

Pour les deployments publics, desactivez les endpoints d'administration :

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### Executer

```bash
dotnet run
```

Le serveur demarre sur l'URL configuree, en servant le document de decouverte OIDC a `/.well-known/openid-configuration`, l'interface de connexion a `/login`, et toutes les APIs d'authentification et d'administration.

---

## Partie 2 : Interface de connexion personnalisee

La SPA de connexion par defaut fonctionne directement, mais vous pouvez la remplacer par votre propre application React qui importe des composants et des clients API depuis le package npm `@drawboard/authagonal-login`.

### Mettre en place le frontend

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router-dom @drawboard/authagonal-login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### Ce que le package npm exporte

```typescript
// Components — use as-is or as reference
import {
  AuthLayout,
  LoginPage,
  ForgotPasswordPage,
  ResetPasswordPage,
} from '@drawboard/authagonal-login';

// API clients — call from your custom pages
import {
  login,
  logout,
  ssoCheck,
  forgotPassword,
  resetPassword,
  getSession,
  getPasswordPolicy,
} from '@drawboard/authagonal-login';

// Branding
import {
  loadBranding,
  useBranding,
  BrandingContext,
} from '@drawboard/authagonal-login';

// Styles
import '@drawboard/authagonal-login/styles.css';

// Types
import type {
  BrandingConfig,
  ApiError,
  LoginResponse,
  SessionResponse,
  PasswordPolicyResponse,
} from '@drawboard/authagonal-login';
```

### Point d'entree (main.tsx)

Chargez la configuration de marque depuis le serveur et enveloppez votre application dans le contexte de marque :

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

### Routage (App.tsx)

Combinez des pages personnalisees avec les pages du package de base :

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

### Page de connexion personnalisee

Construisez votre propre formulaire de connexion en utilisant les clients API du package npm :

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
        setError(err.errorDescription || 'Login failed');
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

### Layout personnalise

Enveloppez le `AuthLayout` de base pour ajouter votre propre marque :

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

### Marque (wwwroot/branding.json)

Configurez l'apparence de l'interface de connexion sans reconstruire :

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

### Configuration Vite

Redirigez les appels API vers le backend pendant le developpement :

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

### Construire et servir

Ajoutez une cible de build a votre `.csproj` pour construire automatiquement la SPA et la copier dans `wwwroot` :

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

Maintenant `dotnet build` construit a la fois le serveur .NET et la SPA React, et `dotnet run` sert le tout depuis un seul processus.

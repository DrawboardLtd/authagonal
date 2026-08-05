---
layout: default
title: Servidor personalizado
locale: es
---

# Inicio rápido: Servidor personalizado

Esta guía explica cómo alojar Authagonal como biblioteca en su propio proyecto ASP.NET Core y luego personalizar la interfaz de inicio de sesión con sus propios componentes React.

## Parte 1: Configuración del servidor

### Crear el proyecto

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.AzureProvider
```

Su archivo `.csproj` debe contener:

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.AzureProvider" Version="*" />
</ItemGroup>
```

`Authagonal.AzureProvider` proporciona los stores de Azure Table Storage que `AddAuthagonal` conecta a partir de la configuración `Storage:*`. Para alojar en AWS en su lugar, referencie `Authagonal.AwsProvider` y llame a `AddAuthagonalAwsStorage(...)` antes de `AddAuthagonal`, ver [Instalación → Backend de AWS](installation#aws-backend).

### Configurar Program.cs

La configuración mínima requiere tres llamadas: `AddAuthagonal`, `UseAuthagonal` y `MapAuthagonalEndpoints`.

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

### Configurar appsettings.json

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

| Clave | Descripción |
|---|---|
| `Issuer` | La URL pública de su servidor de autenticación. Se usa en tokens y en el descubrimiento OIDC. |
| `Storage:ConnectionString` | Cadena de conexión de Azure Table Storage. |
| `Clients` | Array de clientes OAuth inyectados al inicio. |

### Puntos de extensión

Registre sus implementaciones **antes** de llamar a `AddAuthagonal()`: Authagonal usa `TryAdd`, por lo que sus registros tienen prioridad.

| Interfaz | Propósito | Predeterminado |
|---|---|---|
| `IEmailService` | Enviar correos de verificación y restablecimiento de contraseña | Emisor Resend integrado cuando `Email:ResendApiKey` está establecido; de lo contrario no-op (descarta silenciosamente) |
| `IAuthHook` | Interceptar o auditar eventos de inicio de sesión, registro y token | Sin operación |
| `IProvisioningOrchestrator` | Aprovisionar usuarios en aplicaciones posteriores durante la autorización | Aprovisionamiento TCC |
| `ISecretProvider` | Resolver secretos de cliente | Texto plano (o Key Vault con `SecretProvider:VaultUri`) |

#### Ejemplo: hook de auditoría

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

La interfaz tiene más miembros opcionales con implementaciones predeterminadas no-op (`OnMfaVerifyFailedAsync`, `OnEmailConfirmedAsync`, `OnMfaEnrolledAsync`, `OnMfaCredentialRemovedAsync`, `OnRecoveryCodesRegeneratedAsync`, `OnPasswordChangedAsync`); sobreescríbalas solo si necesita esos eventos.

#### Ejemplo: servicio de correo electrónico

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

> **El correo electrónico es la trampa de integración más común.** Si no registra ningún `IEmailService` y no establece `Email:ResendApiKey`, los correos de verificación y de restablecimiento de contraseña se descartan silenciosamente, y como la puerta de inicio de sesión de correo confirmado está activada de forma predeterminada, los usuarios que se registran por sí mismos nunca pueden iniciar sesión (`UseAuthagonal` avisa al inicio). El emisor Resend integrado se activa automáticamente cuando `Email:ResendApiKey` + `Email:SenderEmail` están configurados; para dev/test, `Auth:AutoConfirmEmailDomains` omite la verificación para los dominios listados. Ver [Configuración → Correo](configuration#email).

### Agregar endpoints personalizados

Puede agregar sus propios endpoints junto a los de Authagonal:

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### Desactivar la API de administración

Para despliegues públicos, desactive los endpoints de administración:

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### Ejecutar

```bash
dotnet run
```

El servidor se inicia en la URL configurada, sirviendo el documento de descubrimiento OIDC en `/.well-known/openid-configuration`, la interfaz de inicio de sesión en `/login` y todas las APIs de autenticación y administración.

---

## Parte 2: Interfaz de inicio de sesión personalizada

La SPA de inicio de sesión predeterminada funciona de inmediato, pero puede reemplazarla con su propia aplicación React que importa componentes y clientes API del paquete npm `@authagonal/login`.

### Preparar el frontend

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router @authagonal/login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### Lo que exporta el paquete npm

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

### Punto de entrada (main.tsx)

Cargue la configuración de marca desde el servidor y envuelva su aplicación en el contexto de marca:

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

### Enrutamiento (App.tsx)

Combine páginas personalizadas con las páginas del paquete base:

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

### Página de inicio de sesión personalizada

Construya su propio formulario de inicio de sesión usando los clientes API del paquete npm:

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

### Layout personalizado

Envuelva el `AuthLayout` base para agregar su propia marca:

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

### Marca (wwwroot/branding.json)

Configure la apariencia de la interfaz de inicio de sesión sin reconstruir:

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

El esquema completo, incluidos el texto de bienvenida localizado, la lista del selector de idioma y las sustituciones de color y de fondo de logotipo por modo claro/oscuro, está en la página de [Marca](branding).

### Configuración de Vite

Redirija las llamadas API al backend durante el desarrollo:

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

### Construir y servir

Agregue un objetivo de compilación a su `.csproj` para construir automáticamente la SPA y copiarla a `wwwroot`:

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

Ahora `dotnet build` compila tanto el servidor .NET como la SPA React, y `dotnet run` sirve todo desde un único proceso.

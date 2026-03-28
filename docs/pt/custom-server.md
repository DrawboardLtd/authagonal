---
layout: default
title: Servidor Personalizado
locale: pt
---

# Início Rápido -- Servidor Personalizado

Este guia mostra como hospedar o Authagonal como uma biblioteca no seu próprio projeto ASP.NET Core e, em seguida, personalizar a interface de login com seus próprios componentes React.

## Parte 1: Configuração do Servidor

### Criar o projeto

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.Storage
```

Seu arquivo `.csproj` deve conter:

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.Storage" Version="*" />
</ItemGroup>
```

### Configurar Program.cs

A configuração mínima consiste em três chamadas: `AddAuthagonal`, `UseAuthagonal` e `MapAuthagonalEndpoints`.

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

| Chave | Descrição |
|---|---|
| `Issuer` | A URL pública do seu servidor de autenticação. Usada em tokens e na descoberta OIDC. |
| `Oidc:Issuer` | Geralmente igual a `Issuer`. Separado se suas URLs internas e externas forem diferentes. |
| `Storage:ConnectionString` | String de conexão do Azure Table Storage. |
| `Clients` | Array de clientes OAuth injetados na inicialização. |

### Pontos de extensão

Registre suas implementações **antes** de chamar `AddAuthagonal()` -- o Authagonal usa `TryAdd`, então seus registros têm prioridade.

| Interface | Finalidade | Padrão |
|---|---|---|
| `IEmailService` | Enviar e-mails de verificação e redefinição de senha | SendGrid (requer configuração) |
| `IAuthHook` | Interceptar ou auditar eventos de login, registro e token | Sem operação |
| `IProvisioningOrchestrator` | Provisionar usuários em aplicações downstream no momento da autorização | Provisionamento TCC |
| `ISecretProvider` | Resolver segredos de cliente | Texto simples (ou Key Vault com `SecretProvider:VaultUri`) |

#### Exemplo: hook de auditoria

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

#### Exemplo: serviço de e-mail

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

### Adicionar endpoints personalizados

Você pode adicionar seus próprios endpoints ao lado dos do Authagonal:

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### Desativar a API de administração

Para implantações públicas, desative os endpoints de administração:

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### Executar

```bash
dotnet run
```

O servidor inicia na URL configurada, servindo o documento de descoberta OIDC em `/.well-known/openid-configuration`, a interface de login em `/login` e todas as APIs de autenticação e administração.

---

## Parte 2: Interface de Login Personalizada

A SPA de login padrão funciona imediatamente, mas você pode substituí-la pela sua própria aplicação React que importa componentes e clientes API do pacote npm `@drawboard/authagonal-login`.

### Preparar o frontend

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router-dom @drawboard/authagonal-login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### O que o pacote npm exporta

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

### Ponto de entrada (main.tsx)

Carregue a configuração de marca do servidor e envolva sua aplicação no contexto de marca:

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

### Roteamento (App.tsx)

Combine páginas personalizadas com as páginas do pacote base:

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

### Página de login personalizada

Construa seu próprio formulário de login usando os clientes API do pacote npm:

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

### Layout personalizado

Envolva o `AuthLayout` base para adicionar sua própria marca:

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

### Marca (wwwroot/branding.json)

Configure a aparência da interface de login sem precisar reconstruir:

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

### Configuração do Vite

Redirecione as chamadas de API para o backend durante o desenvolvimento:

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

### Compilar e servir

Adicione um alvo de build ao seu `.csproj` para compilar automaticamente a SPA e copiá-la para `wwwroot`:

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

Agora `dotnet build` compila tanto o servidor .NET quanto a SPA React, e `dotnet run` serve tudo a partir de um único processo.

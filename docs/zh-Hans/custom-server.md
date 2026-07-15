---
layout: default
title: 自定义服务器
locale: zh-Hans
---

# 自定义服务器快速入门

本指南演示如何将 Authagonal 作为库托管在您自己的 ASP.NET Core 项目中，然后使用您自己的 React 组件自定义登录界面。

## 第 1 部分：服务器设置

### 创建项目

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.AzureProvider
```

您的 `.csproj` 应包含：

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.AzureProvider" Version="*" />
</ItemGroup>
```

`Authagonal.AzureProvider` 提供 Azure Table Storage 存储，`AddAuthagonal` 会从 `Storage:*` 配置将其接入。若要改为托管在 AWS 上，请引用 `Authagonal.AwsProvider`，并在 `AddAuthagonal` 之前调用 `AddAuthagonalAwsStorage(...)` — 参见 [安装 → AWS 后端](installation#aws-backend)。

### 配置 Program.cs

最少需要三个调用：`AddAuthagonal`、`UseAuthagonal` 和 `MapAuthagonalEndpoints`。

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

### 配置 appsettings.json

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

| 键 | 描述 |
|---|---|
| `Issuer` | 您的认证服务器的公共 URL。用于令牌和 OIDC 发现。 |
| `Storage:ConnectionString` | Azure Table Storage 连接字符串。 |
| `Clients` | 启动时播种的 OAuth 客户端数组。 |

### 扩展点

在调用 `AddAuthagonal()` **之前**注册您的实现 — Authagonal 使用 `TryAdd`，因此您的注册优先。

| 接口 | 用途 | 默认值 |
|---|---|---|
| `IEmailService` | 发送验证和密码重置邮件 | 设置了 `Email:ResendApiKey` 时使用内置的 Resend 发送器；否则为空操作（静默丢弃） |
| `IAuthHook` | 拦截或审计登录、注册和令牌事件 | 空操作 |
| `IProvisioningOrchestrator` | 在授权时将用户配置到下游应用 | TCC 配置 |
| `ISecretProvider` | 解析客户端密钥 | 明文（或使用 `SecretProvider:VaultUri` 的 Key Vault） |

#### 示例：审计钩子

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

该接口还有更多可选成员，它们带有空操作的默认实现（`OnMfaVerifyFailedAsync`、`OnEmailConfirmedAsync`、`OnMfaEnrolledAsync`、`OnMfaCredentialRemovedAsync`、`OnRecoveryCodesRegeneratedAsync`、`OnPasswordChangedAsync`）— 仅在您需要这些事件时才覆盖它们。

#### 示例：邮件服务

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

> **邮件是最常见的集成陷阱。** 如果您未注册 `IEmailService` 且未设置 `Email:ResendApiKey`，验证邮件和密码重置邮件会被静默丢弃 — 而且由于确认邮箱的登录门控默认开启，自助注册的用户将永远无法登录（`UseAuthagonal` 会在启动时发出警告）。当配置了 `Email:ResendApiKey` + `Email:SenderEmail` 时，内置的 Resend 发送器会自动激活；在开发 / 测试环境中，`Auth:AutoConfirmEmailDomains` 会为列出的域名跳过验证。参见 [配置 → 邮件](configuration#email)。

### 添加自定义端点

您可以在 Authagonal 的端点旁边添加自己的端点：

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### 禁用管理 API

对于面向公众的部署，禁用管理端点：

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### 运行

```bash
dotnet run
```

服务器在配置的 URL 上启动，在 `/.well-known/openid-configuration` 提供 OIDC 发现文档，在 `/login` 提供登录界面，以及所有认证/管理 API。

---

## 第 2 部分：自定义登录界面

默认登录 SPA 开箱即用，但您可以用自己的 React 应用替换它，该应用从 `@authagonal/login` npm 包导入组件和 API 客户端。

### 搭建前端项目

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router-dom @authagonal/login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### npm 包导出内容

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

### 入口点 (main.tsx)

从服务器加载品牌配置，并将您的应用包裹在品牌上下文中：

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

### 路由 (App.tsx)

将自定义页面与基础包页面混合使用：

```tsx
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { ForgotPasswordPage, ResetPasswordPage } from '@authagonal/login';
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

### 自定义登录页面

使用 npm 包中的 API 客户端构建您自己的登录表单：

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

### 自定义布局

包裹基础 `AuthLayout` 以添加您自己的品牌：

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

### 品牌配置 (wwwroot/branding.json)

无需重新构建即可配置登录界面外观：

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

完整的架构 — 包括本地化的欢迎文本、语言选择器列表，以及按模式的深色/浅色颜色和徽标背景覆盖 — 在 [品牌定制](branding) 页面上。

### Vite 配置

在开发期间将 API 调用代理到后端：

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

### 构建和提供服务

在您的 `.csproj` 中添加构建目标，以自动构建 SPA 并将其复制到 `wwwroot`：

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

现在 `dotnet build` 会同时构建 .NET 服务器和 React SPA，`dotnet run` 从单个进程提供所有服务。

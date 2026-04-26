---
layout: default
title: Máy chủ tùy chỉnh
locale: vi
---

# Khởi đầu nhanh -- Máy chủ tùy chỉnh

Hướng dẫn này trình bày cách lưu trữ Authagonal như một thư viện trong dự án ASP.NET Core của bạn, sau đó tùy chỉnh giao diện đăng nhập bằng các component React của riêng bạn.

## Phần 1: Thiết lập máy chủ

### Tạo dự án

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.Storage
```

File `.csproj` của bạn cần chứa:

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.Storage" Version="*" />
</ItemGroup>
```

### Cấu hình Program.cs

Cấu hình tối thiểu gồm ba lệnh gọi: `AddAuthagonal`, `UseAuthagonal` và `MapAuthagonalEndpoints`.

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

### Cấu hình appsettings.json

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

| Khóa | Mô tả |
|---|---|
| `Issuer` | URL công khai của máy chủ xác thực. Được sử dụng trong token và khám phá OIDC. |
| `Storage:ConnectionString` | Chuỗi kết nối Azure Table Storage. |
| `Clients` | Mảng các client OAuth được khởi tạo khi khởi động. |

### Các điểm mở rộng

Đăng ký các implementation của bạn **trước** khi gọi `AddAuthagonal()` -- Authagonal sử dụng `TryAdd`, nên đăng ký của bạn được ưu tiên.

| Interface | Mục đích | Mặc định |
|---|---|---|
| `IEmailService` | Gửi email xác minh và đặt lại mật khẩu | Không thực hiện gì (bỏ qua im lặng) |
| `IAuthHook` | Chặn hoặc kiểm tra các sự kiện đăng nhập, đăng ký và token | Không thực hiện gì |
| `IProvisioningOrchestrator` | Cung cấp người dùng cho các ứng dụng hạ nguồn tại thời điểm ủy quyền | Cung cấp TCC |
| `ISecretProvider` | Giải quyết secret của client | Văn bản thuần (hoặc Key Vault với `SecretProvider:VaultUri`) |

#### Ví dụ: hook kiểm toán

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

#### Ví dụ: dịch vụ email

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

### Thêm endpoint tùy chỉnh

Bạn có thể thêm endpoint của riêng mình bên cạnh các endpoint của Authagonal:

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### Tắt API quản trị

Đối với các triển khai công khai, tắt các endpoint quản trị:

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### Chạy

```bash
dotnet run
```

Máy chủ khởi động tại URL đã cấu hình, phục vụ tài liệu khám phá OIDC tại `/.well-known/openid-configuration`, giao diện đăng nhập tại `/login` và tất cả các API xác thực/quản trị.

---

## Phần 2: Giao diện đăng nhập tùy chỉnh

SPA đăng nhập mặc định hoạt động ngay lập tức, nhưng bạn có thể thay thế bằng ứng dụng React của riêng mình, nhập các component và API client từ gói npm `@authagonal/login`.

### Thiết lập frontend

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router-dom @authagonal/login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### Gói npm xuất những gì

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
  login, logout, ssoCheck, forgotPassword, resetPassword,
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

### Điểm vào (main.tsx)

Tải cấu hình thương hiệu từ máy chủ và bọc ứng dụng trong ngữ cảnh thương hiệu:

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

### Định tuyến (App.tsx)

Kết hợp các trang tùy chỉnh với các trang của gói cơ sở:

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

### Trang đăng nhập tùy chỉnh

Xây dựng form đăng nhập của riêng bạn bằng các API client từ gói npm:

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

### Layout tùy chỉnh

Bọc `AuthLayout` cơ sở để thêm thương hiệu của riêng bạn:

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

### Thương hiệu (wwwroot/branding.json)

Cấu hình giao diện đăng nhập mà không cần build lại:

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

### Cấu hình Vite

Chuyển tiếp các lệnh gọi API đến backend trong quá trình phát triển:

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

### Build và phục vụ

Thêm mục tiêu build vào file `.csproj` để tự động build SPA và sao chép vào `wwwroot`:

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

Bây giờ `dotnet build` sẽ build cả máy chủ .NET và SPA React, và `dotnet run` phục vụ mọi thứ từ một tiến trình duy nhất.

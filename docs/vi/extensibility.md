---
layout: default
title: Khả năng mở rộng
locale: vi
---

# Khả năng mở rộng

Authagonal có thể được tích hợp dưới dạng thư viện trong dự án ASP.NET Core của bạn, với toàn quyền kiểm soát các triển khai dịch vụ.

## Phương thức mở rộng

Ba phương thức tích hợp Authagonal vào bất kỳ ứng dụng ASP.NET Core nào:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

## Ghi đè dịch vụ

Đăng ký các triển khai tùy chỉnh **trước** khi gọi `AddAuthagonal()`. Authagonal sử dụng `TryAdd` nội bộ, nên các đăng ký của bạn được ưu tiên:

```csharp
// Custom implementations — registered first, won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup — skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

### Các điểm mở rộng

| Giao diện | Mặc định | Mục đích |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (không làm gì) | Hook vòng đời cho các sự kiện xác thực — ghi nhật ký kiểm tra, xác thực tùy chỉnh, webhooks |
| `IEmailService` | `EmailService` (SendGrid) | Gửi email cho xác minh và đặt lại mật khẩu |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` | Cấp phát người dùng vào các ứng dụng phía sau |
| `ISecretProvider` | `PlaintextSecretProvider` | Giải quyết bí mật (Key Vault, AWS Secrets Manager, v.v.) |

## IAuthHook

Giao diện `IAuthHook` cung cấp các hook vào vòng đời xác thực. Mỗi phương thức có thể ném ngoại lệ để hủy bỏ thao tác.

```csharp
public interface IAuthHook
{
    Task OnUserAuthenticatedAsync(string userId, string email, string method,
        string? clientId = null, CancellationToken ct = default);
    Task OnUserCreatedAsync(string userId, string email, string createdVia,
        CancellationToken ct = default);
    Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default);
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType,
        CancellationToken ct = default);
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default);
}
```

### Tham số

| Phương thức | Giá trị `method` / `createdVia` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnLoginFailedAsync` | `"invalid_password"`, `"locked_out"`, v.v. |
| `OnTokenIssuedAsync` | Các loại cấp quyền: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Trả về chính sách MFA hiệu lực cho người dùng. Mặc định: trả về `clientPolicy` không thay đổi. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |

### Ví dụ: Ghi nhật ký kiểm tra

```csharp
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] User created: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning("[AUDIT] Login failed: {Email} — {Reason}", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }
}
```

### Ví dụ: Hạn chế tên miền

```csharp
public sealed class DomainRestrictionHook : IAuthHook
{
    private static readonly HashSet<string> BlockedDomains = ["competitor.com"];

    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        var domain = email.Split('@').Last();
        if (BlockedDomains.Contains(domain))
            throw new InvalidOperationException($"Domain {domain} is not allowed");

        return Task.CompletedTask;
    }

    // ... other methods return Task.CompletedTask
}
```

## Endpoint tùy chỉnh

Thêm các endpoint riêng của bạn bên cạnh các endpoint của Authagonal:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Dịch vụ email tùy chỉnh

Thay thế SendGrid bằng bất kỳ nhà cung cấp email nào:

```csharp
public sealed class SmtpEmailService(SmtpClient smtp) : IEmailService
{
    public async Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Verify your email", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Reset your password", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }
}
```

## Xem thêm

- [demos/custom-server/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/custom-server) — ví dụ hoạt động hoàn chỉnh
- [demos/sample-app/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/sample-app) — ví dụ ứng dụng client

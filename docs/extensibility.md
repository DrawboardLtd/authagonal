---
layout: default
title: Extensibility
---

# Extensibility

Authagonal can be hosted as a library in your own ASP.NET Core project, with full control over service implementations.

## Extension Methods

Three methods compose Authagonal into any ASP.NET Core app:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

## Overriding Services

Register your custom implementations **before** calling `AddAuthagonal()`. Authagonal uses `TryAdd` internally, so your registrations take precedence:

```csharp
// Custom implementations — registered first, won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup — skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

### Extensibility Points

| Interface | Default | Purpose |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (no-op) | Lifecycle hooks for auth events — audit logging, custom validation, webhooks |
| `IEmailService` | `EmailService` (SendGrid) | Email delivery for verification and password reset |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` | User provisioning into downstream apps |
| `ISecretProvider` | `PlaintextSecretProvider` | Secret resolution (Key Vault, AWS Secrets Manager, etc.) |

## IAuthHook

The `IAuthHook` interface provides hooks into the authentication lifecycle. Each method can throw an exception to abort the operation.

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

### Parameters

| Method | `method` / `createdVia` values |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnLoginFailedAsync` | `"invalid_password"`, `"locked_out"`, etc. |
| `OnTokenIssuedAsync` | Grant types: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Returns the effective MFA policy for a user. Default: return `clientPolicy` unchanged. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |

### Example: Audit Logger

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

### Example: Domain Restriction

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

## Custom Endpoints

Add your own endpoints alongside Authagonal's:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Custom Email Service

Replace SendGrid with any email provider:

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

## See Also

- [demos/custom-server/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/custom-server) — complete working example
- [demos/sample-app/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/sample-app) — client app example

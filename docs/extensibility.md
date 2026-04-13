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

### Multi-Tenant Hosting

For multi-tenant deployments, use `AddAuthagonalCore()` instead. It registers endpoints, middleware, and core services but skips storage, `KeyManager`, and background services — you provide those per-tenant:

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` and store interfaces (`IClientStore`, `IScimTokenStore`, etc.) are resolved from `HttpContext.RequestServices` at request time, so scoped registrations work correctly for per-tenant isolation.

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
| `IEmailService` | `NullEmailService` (no-op) | Email delivery for verification and password reset |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` | User provisioning into downstream apps |
| `ISecretProvider` | `PlaintextSecretProvider` | Secret resolution (Key Vault, AWS Secrets Manager, etc.) |
| `ITenantContext` | `DefaultTenantContext` (reads from `IConfiguration`) | Tenant resolution for multi-tenant deployments |
| `IKeyManager` | `KeyManager` (singleton) | Signing key management — override for per-tenant key isolation |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` | Resolves available provisioning apps — override for dynamic or per-tenant app resolution |

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

## HashiCorp Vault Transit Integration

Authagonal can delegate JWT signing to HashiCorp Vault's Transit secrets engine. Private keys never leave Vault — only the signing operation is remote. Public keys are cached locally for verification.

```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Vault Transit HTTP client
builder.Services.AddHttpClient("Vault", client =>
{
    client.BaseAddress = new Uri("https://vault.example.com");
    client.DefaultRequestHeaders.Add("X-Vault-Token", "hvs.xxx");
});

// Register Vault Transit services
builder.Services.AddSingleton<VaultTransitClient>();
builder.Services.AddSingleton<VaultTransitCryptoProvider>();

builder.Services.AddAuthagonal(builder.Configuration);
```

The `VaultTransitClient` provides these operations:

| Method | Description |
|---|---|
| `SignAsync(keyName, data)` | Sign data using a Vault Transit key |
| `VerifyAsync(keyName, data, signature)` | Verify a signature (local, using cached public key) |
| `CreateKeyAsync(keyName, type)` | Create a new Transit key (default: RSA-2048) |
| `RotateKeyAsync(keyName)` | Rotate a key to a new version |
| `ReadKeyAsync(keyName)` | Read key metadata and public keys |
| `KeyExistsAsync(keyName)` | Check if a key exists |

The `VaultTransitCryptoProvider` integrates with .NET's `JsonWebTokenHandler` so that JWT signing transparently uses Vault. The `VaultTransitSecurityKey` and `VaultTransitSignatureProvider` handle the low-level integration.

## Custom Email Service

Replace Resend with any email provider:

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

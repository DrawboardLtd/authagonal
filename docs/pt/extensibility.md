---
layout: default
title: Extensibilidade
locale: pt
---

# Extensibilidade

O Authagonal pode ser hospedado como uma biblioteca no seu próprio projeto ASP.NET Core, com controlo total sobre as implementações de serviços.

## Métodos de Extensão

Três métodos compõem o Authagonal em qualquer aplicação ASP.NET Core:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

## Substituição de Serviços

Registe as suas implementações personalizadas **antes** de chamar `AddAuthagonal()`. O Authagonal usa `TryAdd` internamente, portanto os seus registos têm precedência:

```csharp
// Custom implementations — registered first, won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup — skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

### Pontos de Extensibilidade

| Interface | Padrão | Finalidade |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (no-op) | Hooks de ciclo de vida para eventos de autenticação — registo de auditoria, validação personalizada, webhooks |
| `IEmailService` | `NullEmailService` (no-op) | Entrega de e-mail para verificação e redefinição de senha |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` | Provisionamento de utilizadores em aplicações downstream |
| `ISecretProvider` | `PlaintextSecretProvider` | Resolução de segredos (Key Vault, AWS Secrets Manager, etc.) |
| `ITenantContext` | `DefaultTenantContext` (lê de `IConfiguration`) | Resolução de tenant para implantações multi-tenant |
| `IKeyManager` | `KeyManager` (singleton) | Gestão de chaves de assinatura — substitua para isolamento de chaves por tenant |

## IAuthHook

A interface `IAuthHook` fornece hooks no ciclo de vida da autenticação. Cada método pode lançar uma exceção para abortar a operação.

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

### Parâmetros

| Método | Valores de `method` / `createdVia` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnLoginFailedAsync` | `"invalid_password"`, `"locked_out"`, etc. |
| `OnTokenIssuedAsync` | Tipos de grant: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Retorna a política de MFA efetiva para um utilizador. Padrão: retorna `clientPolicy` sem alteração. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |

### Exemplo: Registo de Auditoria

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

### Exemplo: Restrição de Domínio

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

## Endpoints Personalizados

Adicione os seus próprios endpoints ao lado dos do Authagonal:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Serviço de E-mail Personalizado

Substitua o SendGrid por qualquer provedor de e-mail:

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

## Veja Também

- [demos/custom-server/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/custom-server) — exemplo completo funcional
- [demos/sample-app/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/sample-app) — exemplo de aplicação cliente

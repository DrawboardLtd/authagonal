---
layout: default
title: Extensibilidad
locale: es
---

# Extensibilidad

Authagonal puede alojarse como una biblioteca en su propio proyecto ASP.NET Core, con control total sobre las implementaciones de servicios.

## Metodos de extension

Tres metodos componen Authagonal en cualquier aplicacion ASP.NET Core:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

## Sustitucion de servicios

Registre sus implementaciones personalizadas **antes** de llamar a `AddAuthagonal()`. Authagonal usa `TryAdd` internamente, por lo que sus registros tienen prioridad:

```csharp
// Custom implementations — registered first, won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup — skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

### Puntos de extensibilidad

| Interfaz | Predeterminado | Proposito |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (sin efecto) | Hooks de ciclo de vida para eventos de autenticacion -- registro de auditoria, validacion personalizada, webhooks |
| `IEmailService` | `EmailService` (SendGrid) | Entrega de correos electronicos para verificacion y restablecimiento de contrasena |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` | Aprovisionamiento de usuarios en aplicaciones posteriores |
| `ISecretProvider` | `PlaintextSecretProvider` | Resolucion de secretos (Key Vault, AWS Secrets Manager, etc.) |

## IAuthHook

La interfaz `IAuthHook` proporciona hooks en el ciclo de vida de la autenticacion. Cada metodo puede lanzar una excepcion para cancelar la operacion.

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
}
```

### Parametros

| Metodo | Valores de `method` / `createdVia` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnLoginFailedAsync` | `"invalid_password"`, `"locked_out"`, etc. |
| `OnTokenIssuedAsync` | Tipos de concesion: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |

### Ejemplo: Registro de auditoria

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

### Ejemplo: Restriccion de dominio

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

## Endpoints personalizados

Agregue sus propios endpoints junto a los de Authagonal:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Servicio de correo electronico personalizado

Reemplace SendGrid con cualquier proveedor de correo:

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

## Ver tambien

- [demos/custom-server/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/custom-server) -- ejemplo completo funcional
- [demos/sample-app/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/sample-app) -- ejemplo de aplicacion cliente

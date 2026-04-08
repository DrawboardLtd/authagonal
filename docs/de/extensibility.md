---
layout: default
title: Erweiterbarkeit
locale: de
---

# Erweiterbarkeit

Authagonal kann als Bibliothek in Ihrem eigenen ASP.NET Core-Projekt gehostet werden, mit voller Kontrolle ueber Service-Implementierungen.

## Erweiterungsmethoden

Drei Methoden integrieren Authagonal in jede ASP.NET Core-App:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### Multi-Mandanten-Hosting

Fuer Multi-Mandanten-Deployments verwenden Sie stattdessen `AddAuthagonalCore()`. Es registriert Endpunkte, Middleware und Kerndienste, ueberspringt aber Storage, `KeyManager` und Hintergrunddienste -- diese stellen Sie pro Mandant bereit:

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` und Store-Schnittstellen (`IClientStore`, `IScimTokenStore` usw.) werden zur Anforderungszeit aus `HttpContext.RequestServices` aufgeloest, sodass Scoped-Registrierungen fuer die mandantenspezifische Isolierung korrekt funktionieren.

## Services ueberschreiben

Registrieren Sie Ihre benutzerdefinierten Implementierungen **vor** dem Aufruf von `AddAuthagonal()`. Authagonal verwendet intern `TryAdd`, sodass Ihre Registrierungen Vorrang haben:

```csharp
// Custom implementations — registered first, won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup — skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

### Erweiterungspunkte

| Schnittstelle | Standardimplementierung | Zweck |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (Leeroperationen) | Lebenszyklus-Hooks fuer Auth-Ereignisse -- Audit-Protokollierung, benutzerdefinierte Validierung, Webhooks |
| `IEmailService` | `NullEmailService` (No-op) | E-Mail-Versand fuer Verifizierung und Passwortzuruecksetzung |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` | Benutzerbereitstellung in nachgelagerte Apps |
| `ISecretProvider` | `PlaintextSecretProvider` | Geheimnis-Aufloesung (Key Vault, AWS Secrets Manager usw.) |
| `ITenantContext` | `DefaultTenantContext` (liest aus `IConfiguration`) | Mandantenaufloesung fuer Multi-Mandanten-Deployments |
| `IKeyManager` | `KeyManager` (Singleton) | Signaturschluessel-Verwaltung -- ueberschreiben fuer mandantenspezifische Schluesselisolierung |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` | Aufloesen verfuegbarer Bereitstellungs-Apps -- ueberschreiben fuer dynamische oder mandantenspezifische App-Aufloesung |

## IAuthHook

Die `IAuthHook`-Schnittstelle bietet Hooks in den Authentifizierungs-Lebenszyklus. Jede Methode kann eine Ausnahme werfen, um den Vorgang abzubrechen.

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

### Parameter

| Methode | `method` / `createdVia` Werte |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnLoginFailedAsync` | `"invalid_password"`, `"locked_out"` usw. |
| `OnTokenIssuedAsync` | Gewaehrungstypen: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Gibt die effektive MFA-Richtlinie fuer einen Benutzer zurueck. Standard: `clientPolicy` unveraendert zurueckgeben. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |

### Beispiel: Audit-Logger

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

### Beispiel: Domain-Einschraenkung

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

## Benutzerdefinierte Endpunkte

Fuegen Sie Ihre eigenen Endpunkte neben denen von Authagonal hinzu:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Benutzerdefinierter E-Mail-Dienst

Ersetzen Sie SendGrid durch einen beliebigen E-Mail-Anbieter:

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

## Siehe auch

- [demos/custom-server/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/custom-server) -- vollstaendiges funktionierendes Beispiel
- [demos/sample-app/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/sample-app) -- Client-App-Beispiel

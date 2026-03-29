---
layout: default
title: Extensibilite
locale: fr
---

# Extensibilite

Authagonal peut etre heberge en tant que bibliotheque dans votre propre projet ASP.NET Core, avec un controle total sur les implementations des services.

## Methodes d'extension

Trois methodes composent Authagonal dans toute application ASP.NET Core :

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

## Substitution des services

Enregistrez vos implementations personnalisees **avant** d'appeler `AddAuthagonal()`. Authagonal utilise `TryAdd` en interne, donc vos enregistrements ont la priorite :

```csharp
// Custom implementations — registered first, won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup — skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

### Points d'extensibilite

| Interface | Defaut | Objectif |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (sans effet) | Hooks de cycle de vie pour les evenements d'authentification -- journalisation d'audit, validation personnalisee, webhooks |
| `IEmailService` | `EmailService` (SendGrid) | Livraison d'emails pour la verification et la reinitialisation du mot de passe |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` | Provisionnement des utilisateurs dans les applications en aval |
| `ISecretProvider` | `PlaintextSecretProvider` | Resolution des secrets (Key Vault, AWS Secrets Manager, etc.) |

## IAuthHook

L'interface `IAuthHook` fournit des hooks dans le cycle de vie de l'authentification. Chaque methode peut lever une exception pour annuler l'operation.

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

### Parametres

| Methode | Valeurs de `method` / `createdVia` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnLoginFailedAsync` | `"invalid_password"`, `"locked_out"`, etc. |
| `OnTokenIssuedAsync` | Types d'octroi : `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Renvoie la politique MFA effective pour un utilisateur. Par defaut : renvoie `clientPolicy` sans modification. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |

### Exemple : Journalisation d'audit

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

### Exemple : Restriction de domaine

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

## Points d'acces personnalises

Ajoutez vos propres points d'acces a cote de ceux d'Authagonal :

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Service d'email personnalise

Remplacez SendGrid par n'importe quel fournisseur d'email :

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

## Voir aussi

- [demos/custom-server/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/custom-server) -- exemple complet fonctionnel
- [demos/sample-app/](https://github.com/DrawboardLtd/authagonal/tree/master/demos/sample-app) -- exemple d'application cliente

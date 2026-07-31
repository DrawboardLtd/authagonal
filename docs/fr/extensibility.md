---
layout: default
title: Extensibilité
locale: fr
---

# Extensibilité

Authagonal peut être hébergé en tant que bibliothèque dans votre propre projet ASP.NET Core, avec un contrôle total sur les implémentations des services.

## Méthodes d'extension

Trois méthodes intègrent Authagonal dans n'importe quelle application ASP.NET Core :

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### Hébergement multi-tenant

Pour les déploiements multi-tenant, utilisez plutôt `AddAuthagonalCore()`. Cette méthode enregistre les endpoints, le middleware et les services principaux, mais ignore le stockage et les services d'arrière-plan : vous les fournissez par tenant. La gestion des clés de signature utilise par défaut le singleton `ProtocolKeyManager` d'`Authagonal.Protocol`, et un hôte qui enregistre son propre `IKeyManager` avant `AddAuthagonalCore()` le conserve :

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` et les interfaces de stockage (`IClientStore`, `IScimTokenStore`, etc.) sont résolus depuis `HttpContext.RequestServices` au moment de la requête, donc les enregistrements scoped fonctionnent correctement pour l'isolation par tenant.

## Substitution des services

Enregistrez vos implémentations personnalisées **avant** d'appeler `AddAuthagonal()`. Authagonal utilise `TryAdd` en interne, donc vos enregistrements ont la priorité :

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` est particulier : c'est un pipeline à enregistrements multiples. Enregistrez autant de hooks que vous le souhaitez (n'importe quelle durée de vie, y compris `AddScoped`) et tous s'exécutent dans l'ordre d'enregistrement. Le `NullAuthHook` sans effet n'est ajouté que si aucun hook n'a été enregistré au moment où `AddAuthagonal()` / `AddAuthagonalCore()` s'exécute, donc enregistrez toujours vos hooks en premier.

### Points d'extensibilité

| Interface | Défaut | Objectif |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (sans effet, ajouté uniquement quand aucun hook n'est enregistré) | Hooks de cycle de vie pour les événements d'authentification : journalisation d'audit, validation personnalisée, webhooks. Plusieurs hooks peuvent être enregistrés ; ils s'exécutent tous dans l'ordre |
| `IEmailService` | `NullEmailService` (sans effet), ou l'expéditeur Resend intégré quand `Email:ResendApiKey` est configuré | Envoi d'emails pour la vérification, la réinitialisation du mot de passe et les avis de compte existant |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` (scoped) | Provisionnement des utilisateurs dans les applications en aval |
| `ISecretProvider` | `PlaintextSecretProvider`, ou le `KeyVaultSecretProvider` intégré quand `SecretProvider:VaultUri` est configuré | Stockage réversible des secrets (Key Vault, AWS Secrets Manager, Vault Transit, etc.) |
| `ITenantContext` | `DefaultTenantContext` (lit depuis `IConfiguration`) | Résolution du tenant pour les déploiements multi-tenant |
| `IKeyManager` | `ProtocolKeyManager` (singleton, issu d'`Authagonal.Protocol`) | Gestion des clés de signature ; à remplacer pour l'isolation des clés par tenant |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` (scoped) | Résout les applications de provisionnement disponibles ; à remplacer pour une résolution dynamique ou par tenant |
| `IAuditLogger` | `NullAuditLogger` (sans effet) | Piste d'audit pour les changements de configuration et les événements pertinents pour la sécurité |

Trois autres points d'extension se situent au **niveau du store** plutôt que dans la DI : `IFieldCipher`, `IIndexTokenizer` et `IChangeWriter` (tous dans `Authagonal.Core.Services`). Les fournisseurs de stockage les acceptent comme paramètres de constructeur optionnels ; voir leurs sections ci-dessous.

## IAuthHook

L'interface `IAuthHook` fournit des hooks dans le cycle de vie de l'authentification. Les méthodes sur le chemin critique (authentification, création d'utilisateur, émission de Token) peuvent lever une exception pour interrompre l'opération ; les méthodes plus récentes sont des notifications a posteriori. Plusieurs implémentations d'`IAuthHook` peuvent être enregistrées et toutes s'exécutent dans l'ordre d'enregistrement.

```csharp
public interface IAuthHook
{
    // Core lifecycle: implement these
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
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia,
        CancellationToken ct = default);
    Task OnUserDeletedAsync(string userId, string email, string deletedVia,
        CancellationToken ct = default);

    // Additive notifications: default no-op implementations, so existing
    // hooks keep compiling as the interface grows
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnEmailConfirmedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod,
        bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnPasswordChangedAsync(string userId, string email, string changedVia,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

### Paramètres

| Méthode | Notes et valeurs de `method` / `via` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"passkey"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`, `"self"` (les hôtes peuvent passer la leur, par exemple une origine SCIM) |
| `OnUserDeletedAsync` | `"admin"` ; notification uniquement, l'enregistrement peut ne plus être lisible |
| `OnLoginFailedAsync` | `"user_not_found"`, `"invalid_password"`, etc. |
| `OnTokenIssuedAsync` | Types de Grant : `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Appelée après la vérification du mot de passe ; renvoie la politique MFA effective pour l'utilisateur. Par défaut : renvoie `clientPolicy` sans modification. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |
| `OnMfaVerifyFailedAsync` | Mêmes méthodes qu'`OnMfaVerifiedAsync`. Ne se déclenche qu'après des identifiants de premier facteur valides, donc des rafales constituent un signal fort de tentative de contournement du MFA (distinct d'`OnLoginFailedAsync`, l'étape du mot de passe) |
| `OnEmailConfirmedAsync` | L'utilisateur a confirmé son email via le lien de vérification ; déjà persisté |
| `OnMfaEnrolledAsync` | `"totp"`, `"webauthn"` ; l'identifiant est déjà actif |
| `OnMfaCredentialRemovedAsync` | `"totp"`, `"webauthn"`, `"recoverycode"` ; `mfaDisabled` est vrai quand la suppression n'a laissé aucun facteur principal |
| `OnRecoveryCodesRegeneratedAsync` | L'ensemble précédent de codes de récupération est invalidé |
| `OnPasswordChangedAsync` | par exemple `"reset"` ; le changement est persisté et les sessions existantes invalidées |

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
        logger.LogWarning("[AUDIT] Login failed: {Email} ({Reason})", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }

    // ... remaining required methods return Task.CompletedTask
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

## ISecretProvider

`ISecretProvider` (dans `Authagonal.Core.Services`) est le point d'extension de chiffrement réversible pour les secrets stockés tels que les secrets client SSO, les mots de passe SMTP et les seeds TOTP. `ProtectAsync` transforme un texte en clair en une référence que le store persiste ; `ResolveAsync` retransforme la référence en texte en clair. Le `PlaintextSecretProvider` par défaut stocke les valeurs telles quelles (la référence EST la valeur).

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

Définir `SecretProvider:VaultUri` câble automatiquement le `KeyVaultSecretProvider` intégré (Azure Key Vault via `DefaultAzureCredential`). Pour tout le reste, enregistrez votre propre implémentation avant `AddAuthagonal()`.

## Chiffrement des champs PII : IFieldCipher

`IFieldCipher` chiffre au repos les valeurs individuelles des champs PII de l'utilisateur (téléphone, société, attributs personnalisés, email et noms sur la ligne de profil). C'est un point d'extension au niveau du store : les fournisseurs de stockage le prennent comme paramètre de constructeur optionnel (par exemple `TableUserStore`), et en son absence le `NullFieldCipher` passthrough s'applique, donc le chiffrement est strictement opt-in et les hôtes non configurés continuent de stocker en clair.

```csharp
public interface IFieldCipher
{
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);

    // Batch variants have default loop implementations; override for backends
    // with a one-round-trip batch primitive (e.g. Vault Transit)
    Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored,
        CancellationToken ct = default);
}
```

Deux points du contrat comptent. `ProtectAsync` doit renvoyer un token de texte chiffré auto-descriptif (par exemple le `vault:v{n}:...` de Vault Transit), et `ResolveAsync` doit laisser passer sans modification une valeur qu'il ne reconnaît pas comme son propre texte chiffré. La règle du passthrough est ce qui permet de déployer le chiffrement paresseusement sur les lignes existantes : la lecture d'une ligne non migrée renvoie le texte en clair hérité, et la prochaine écriture le rechiffre.

## Recherche par blind-index : IIndexTokenizer

`IIndexTokenizer` garde les champs chiffrés interrogeables. Il transforme une valeur en clair normalisée en un token de blind-index déterministe et compatible avec les clés de table, généralement un HMAC à clé dont la clé réside en dehors de la base de données. Le déterminisme signifie qu'une recherche d'égalité fonctionne toujours ("email = x" devient "token = HMAC(x)"), tandis qu'un dump de la base de données ne peut ni recalculer ni inverser un token. La recherche par préfixe se superpose en tokenisant séparément chaque préfixe d'une valeur, puisqu'un HMAC à clé détruit l'ordre et les balayages de plage.

> **Ce qu'un dump révèle malgré tout.** « Ni recalculer ni inverser » est vrai d'un token isolé, pas
> de l'index dans son ensemble. Trois résidus subsistent, et il vaut mieux les connaître avant de s'y
> fier :
>
>   *(Corrigé.)* ~~**Structure.** L'index de préfixes écrit une ligne par préfixe, si bien que le
>   nombre de lignes d'un enregistrement égale la longueur du champ indexé.~~ Chaque valeur indexée
>   écrit désormais un nombre fixe de lignes, complété par des leurres qu'aucune requête ne peut
>   produire et qu'un dump ne peut distinguer de vrais préfixes.
> - **Égalité et fréquence.** Les tokens sont déterministes par construction, ce qui est précisément
>   ce qui fait fonctionner la recherche : un dump montre donc quels enregistrements partagent une
>   valeur et à quelle fréquence chacune apparaît. L'index de domaines regroupe votre population par
>   employeur, ce qui identifie souvent des personnes sans récupérer d'adresse.
> - **Texte clair choisi.** Quiconque peut à la fois lire le stockage *et* faire indexer des valeurs
>   (créer un compte, être provisionné via SCIM) peut soumettre un candidat et chercher son token.
>   Cela récupère toute valeur devinable -- domaines courants, prénoms courants -- où que réside la
>   clé, car l'oracle est le chemin d'écriture et non le chiffrement.
>
> La tokenisation défend le cas pour lequel elle a été construite : quelqu'un qui détient un dump et
> rien d'autre, et qui cherche à lire des adresses. Les deux résidus restants sont exactement ce
> qu'un oracle d'inscription livre de toute façon. S'ils sont inacceptables, laissez les tables
> d'index de préfixe et de domaine non configurées -- la recherche par correspondance exacte n'en
> porte aucun -- plutôt que de supposer que le HMAC les couvre.

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

Comme `IFieldCipher`, c'est un paramètre de constructeur de store optionnel avec un défaut passthrough (`NullIndexTokenizer`), donc les lignes d'index restent indexées sur le texte en clair jusqu'à ce que vous choisissiez de l'activer. Les tokens renvoyés doivent être utilisables comme valeurs PartitionKey/RowKey d'Azure Table (aucun des caractères `/ \ # ?` ni caractères de contrôle).

## Capture du change-log : IChangeWriter

`IChangeWriter` (renommé depuis `ITombstoneWriter` en 0.6.0) enregistre la clé de chaque ligne modifiée dans une table de change-log dédiée, afin que les sauvegardes incrémentielles puissent trouver ce qui a changé sans balayer la colonne `Timestamp` non indexée des tables actives. Les suppressions sont capturées pour chaque table (un balayage des lignes actives ne peut pas voir une ligne qui a disparu) ; les upserts sont capturés pour les tables que la sauvegarde lit depuis le log au lieu de les balayer. Implémentations intégrées : `TableChangeWriter` (Azure Table Storage), `DynamoChangeWriter` (DynamoDB) et `SqlChangeWriter` (PostgreSQL / SQLite).

```csharp
public interface IChangeWriter
{
    // Deletes
    Task WriteAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    // Upserts
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteUpsertBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
```

Contrat d'ordonnancement pour les implémenteurs et les appelants : écrivez le tombstone de suppression AVANT de supprimer la ligne de données. Un plantage dans l'ordre inverse perd la suppression de toutes les futures sauvegardes, puisque les suppressions sont la seule classe de mutation qu'un nouveau balayage ne peut pas réparer de lui-même. Le plantage inverse est sûr : une écriture ultérieure sur la clé réapplique un timestamp plus récent, et la fusion/restauration conserve les lignes écrites après le tombstone.

## Points d'accès personnalisés

Ajoutez vos propres endpoints à côté de ceux d'Authagonal :

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Intégration de HashiCorp Vault Transit

Authagonal peut déléguer la signature des JWT au moteur de secrets Transit de HashiCorp Vault. Les clés privées ne quittent jamais Vault ; seule l'opération de signature est distante. Les clés publiques sont mises en cache localement pour la vérification.

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

Le `VaultTransitClient` fournit ces opérations :

| Méthode | Description |
|---|---|
| `SignAsync(keyName, data)` | Signe des données à l'aide d'une clé Vault Transit |
| `VerifyAsync(keyName, data, signature)` | Vérifie une signature marshalée en JWS via l'endpoint verify de Transit |
| `EncryptAsync` / `DecryptAsync` (+ `EncryptBatchAsync` / `DecryptBatchAsync`) | Chiffrement symétrique sous une clé `aes256-gcm96` ; renvoie des tokens `vault:v{n}:...` à stocker tels quels |
| `HmacAsync` / `HmacBatchAsync` | HMAC à clé sous une clé `hmac` (tokens de blind-index) |
| `CreateKeyAsync(keyName, type)` | Crée une nouvelle clé Transit (par défaut : `ecdsa-p256`) |
| `EnsureKeyTypeAsync(keyName, type)` | Garantit de façon idempotente qu'une clé existe avec le type souhaité (recrée en cas de non-correspondance de type ; les clés Transit ne peuvent pas être retypées sur place) |
| `RotateKeyAsync(keyName)` | Fait tourner une clé vers une nouvelle version |
| `DeleteKeyAsync(keyName)` | Supprime une clé (active d'abord `deletion_allowed`) |
| `ReadKeyAsync(keyName)` | Lit les métadonnées, les versions et les clés publiques d'une clé |
| `KeyExistsAsync(keyName)` | Vérifie si une clé existe |

Le `VaultTransitCryptoProvider` s'intègre au `JsonWebTokenHandler` de .NET afin que la signature des JWT utilise Vault de façon transparente. Le `VaultTransitSecurityKey` et le `VaultTransitSignatureProvider` gèrent l'intégration de bas niveau.

## Email

L'expéditeur Resend intégré s'active automatiquement quand `Email:ResendApiKey` est configuré (définissez aussi `Email:SenderEmail`). Sans aucun `IEmailService`, le courrier est jeté via `NullEmailService`, et comme la barrière de connexion sur email confirmé est activée par défaut, les utilisateurs auto-inscrits ne pourraient jamais se connecter ; `UseAuthagonal()` émet un avertissement de démarrage bruyant dans cet état.

Pour utiliser un autre fournisseur, enregistrez votre propre `IEmailService` avant `AddAuthagonal()` :

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

`IEmailService` déclare aussi `SendAccountExistsEmailAsync` (envoyé quand quelqu'un tente d'inscrire un email déjà inscrit, ce qui garde la réponse d'inscription neutre contre l'énumération de comptes). Elle a une implémentation par défaut sans effet, donc les implémentations existantes continuent de compiler.

## Voir aussi

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) : exemple complet fonctionnel
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app) : exemple d'application cliente

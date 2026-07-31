---
layout: default
title: Erweiterbarkeit
locale: de
---

# Erweiterbarkeit

Authagonal kann als Bibliothek in Ihrem eigenen ASP.NET Core-Projekt gehostet werden, mit voller Kontrolle über Service-Implementierungen.

## Erweiterungsmethoden

Drei Methoden binden Authagonal in jede ASP.NET Core-App ein:

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

Verwenden Sie für Multi-Mandanten-Deployments stattdessen `AddAuthagonalCore()`. Es registriert Endpunkte, Middleware und Kerndienste, überspringt jedoch Storage und Hintergrunddienste; diese stellen Sie pro Mandant bereit. Die Signaturschlüssel-Verwaltung verwendet standardmäßig den Singleton `ProtocolKeyManager` von `Authagonal.Protocol`, und ein Host, der vor `AddAuthagonalCore()` einen eigenen `IKeyManager` registriert, behält diesen bei:

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` und Store-Schnittstellen (`IClientStore`, `IScimTokenStore` usw.) werden zur Anforderungszeit aus `HttpContext.RequestServices` aufgelöst, sodass Scoped-Registrierungen für die mandantenspezifische Isolierung korrekt funktionieren.

## Services überschreiben

Registrieren Sie Ihre benutzerdefinierten Implementierungen **vor** dem Aufruf von `AddAuthagonal()`. Authagonal verwendet intern `TryAdd`, sodass Ihre Registrierungen Vorrang haben:

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` ist ein Sonderfall: Es handelt sich um eine Mehrfachregistrierungs-Pipeline. Registrieren Sie so viele Hooks, wie Sie möchten (jede Lebensdauer, einschließlich `AddScoped`), und alle laufen in Registrierungsreihenfolge. Der No-op `NullAuthHook` wird nur hinzugefügt, wenn zum Zeitpunkt der Ausführung von `AddAuthagonal()` / `AddAuthagonalCore()` noch kein Hook registriert wurde; registrieren Sie Ihre Hooks daher immer zuerst.

### Erweiterungspunkte

| Schnittstelle | Standard | Zweck |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (No-op, wird nur hinzugefügt, wenn kein Hook registriert ist) | Lebenszyklus-Hooks für Auth-Ereignisse: Audit-Protokollierung, benutzerdefinierte Validierung, Webhooks. Es können mehrere Hooks registriert werden; alle laufen in Registrierungsreihenfolge |
| `IEmailService` | `NullEmailService` (No-op), oder der integrierte Resend-Sender, wenn `Email:ResendApiKey` konfiguriert ist | E-Mail-Zustellung für Verifizierung, Passwortzurücksetzung und Hinweise auf bereits bestehende Konten |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` (Scoped) | Benutzerbereitstellung in nachgelagerte Apps |
| `ISecretProvider` | `PlaintextSecretProvider`, oder der integrierte `KeyVaultSecretProvider`, wenn `SecretProvider:VaultUri` konfiguriert ist | Reversible Geheimnisspeicherung (Key Vault, AWS Secrets Manager, Vault Transit usw.) |
| `ITenantContext` | `DefaultTenantContext` (liest aus `IConfiguration`) | Mandantenauflösung für Multi-Mandanten-Deployments |
| `IKeyManager` | `ProtocolKeyManager` (Singleton, aus `Authagonal.Protocol`) | Signaturschlüssel-Verwaltung; überschreiben für mandantenspezifische Schlüsselisolierung |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` (Scoped) | Löst verfügbare Bereitstellungs-Apps auf; überschreiben für dynamische oder mandantenspezifische App-Auflösung |
| `IAuditLogger` | `NullAuditLogger` (No-op) | Audit-Protokoll für Konfigurationsänderungen und sicherheitsrelevante Ereignisse |

Drei weitere Erweiterungspunkte liegen auf **Store-Ebene** statt in der DI: `IFieldCipher`, `IIndexTokenizer` und `IChangeWriter` (alle in `Authagonal.Core.Services`). Die Storage-Provider nehmen sie als optionale Konstruktorparameter entgegen; siehe die entsprechenden Abschnitte unten.

## IAuthHook

Die Schnittstelle `IAuthHook` bietet Hooks in den Authentifizierungs-Lebenszyklus. Methoden auf dem kritischen Pfad (Authentifizierung, Benutzererstellung, Token-Ausstellung) können eine Ausnahme werfen, um den Vorgang abzubrechen; die neueren Methoden sind nachträgliche Benachrichtigungen. Es können mehrere `IAuthHook`-Implementierungen registriert werden, und alle laufen in Registrierungsreihenfolge.

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

### Parameter

| Methode | Hinweise und `method`- / `via`-Werte |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"passkey"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`, `"self"` (Hosts können eigene Werte übergeben, z. B. einen SCIM-Ursprung) |
| `OnUserDeletedAsync` | `"admin"`; nur Benachrichtigung, der Datensatz ist möglicherweise nicht mehr lesbar |
| `OnLoginFailedAsync` | `"user_not_found"`, `"invalid_password"` usw. |
| `OnTokenIssuedAsync` | Gewährungstypen: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Wird nach der Passwortprüfung aufgerufen; gibt die effektive MFA-Richtlinie für den Benutzer zurück. Standard: `clientPolicy` unverändert zurückgeben. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |
| `OnMfaVerifyFailedAsync` | Dieselben Methoden wie bei `OnMfaVerifiedAsync`. Wird nur nach gültigen Anmeldedaten des ersten Faktors ausgelöst, sodass Häufungen ein starkes Signal für einen MFA-Umgehungsversuch sind (im Unterschied zu `OnLoginFailedAsync`, der Passwortstufe) |
| `OnEmailConfirmedAsync` | Der Benutzer hat seine E-Mail-Adresse über den Bestätigungslink bestätigt; bereits gespeichert |
| `OnMfaEnrolledAsync` | `"totp"`, `"webauthn"`; die Anmeldeinformation ist bereits aktiv |
| `OnMfaCredentialRemovedAsync` | `"totp"`, `"webauthn"`, `"recoverycode"`; `mfaDisabled` ist `true`, wenn nach der Entfernung kein primärer Faktor mehr vorhanden ist |
| `OnRecoveryCodesRegeneratedAsync` | Der vorherige Wiederherstellungscode-Satz wird ungültig |
| `OnPasswordChangedAsync` | z. B. `"reset"`; die Änderung wird gespeichert und bestehende Sitzungen werden ungültig gemacht |

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

### Beispiel: Domain-Beschränkung

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

`ISecretProvider` (in `Authagonal.Core.Services`) ist der Erweiterungspunkt für reversible Verschlüsselung gespeicherter Geheimnisse wie SSO-Client-Secrets, SMTP-Passwörter und TOTP-Seeds. `ProtectAsync` wandelt einen Klartext in eine Referenz um, die der Store dauerhaft speichert; `ResolveAsync` wandelt die Referenz zurück in den Klartext. Der Standard `PlaintextSecretProvider` speichert Werte unverändert (die Referenz IST der Wert).

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

Durch das Setzen von `SecretProvider:VaultUri` wird automatisch der integrierte `KeyVaultSecretProvider` verdrahtet (Azure Key Vault über `DefaultAzureCredential`). Für alles andere registrieren Sie Ihre eigene Implementierung vor `AddAuthagonal()`.

## PII-Feldverschlüsselung: IFieldCipher

`IFieldCipher` verschlüsselt einzelne PII-Feldwerte eines Benutzers (Telefonnummer, Firma, benutzerdefinierte Attribute, E-Mail-Adresse und Namen in der Profilzeile) im Ruhezustand. Es handelt sich um einen Erweiterungspunkt auf Store-Ebene: Die Storage-Provider nehmen ihn als optionalen Konstruktorparameter entgegen (z. B. `TableUserStore`), und wenn er fehlt, greift der Passthrough `NullFieldCipher`, sodass die Verschlüsselung strikt Opt-in ist und unkonfigurierte Hosts weiterhin Klartext speichern.

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

Zwei Vertragspunkte sind entscheidend. `ProtectAsync` muss ein selbstbeschreibendes Chiffretext-Token zurückgeben (z. B. das `vault:v{n}:...` von Vault Transit), und `ResolveAsync` muss einen Wert, den es nicht als eigenen Chiffretext erkennt, unverändert durchreichen. Diese Passthrough-Regel ermöglicht es, die Verschlüsselung schrittweise über bestehende Zeilen auszurollen: Das Lesen einer noch nicht migrierten Zeile liefert den alten Klartext, und der nächste Schreibvorgang verschlüsselt sie erneut.

## Blind-Index-Suche: IIndexTokenizer

`IIndexTokenizer` hält verschlüsselte Felder durchsuchbar. Er wandelt einen normalisierten Klartextwert in ein deterministisches, tabellenschlüsselsicheres Blind-Index-Token um, typischerweise einen keyed HMAC, dessen Schlüssel außerhalb der Datenbank liegt. Determinismus bedeutet, dass eine Gleichheitsabfrage weiterhin funktioniert ("email = x" wird zu "token = HMAC(x)"), während ein Datenbank-Dump ein Token weder neu berechnen noch umkehren kann. Die Präfixsuche wird darübergelegt, indem jedes Präfix eines Werts separat tokenisiert wird, da ein keyed HMAC Reihenfolge und Bereichsabfragen zerstört.

> **Was ein Dump dennoch verrät.** "Weder neu berechnen noch umkehren" gilt für ein einzelnes Token,
> nicht für den Index als Ganzes. Drei Reste bleiben bestehen, und man sollte sie kennen, bevor man
> sich darauf verlässt:
>
>   *(Behoben.)* ~~**Struktur.** Der Präfixindex schreibt eine Zeile pro Präfix, sodass die
>   Zeilenanzahl eines Datensatzes der Länge des indizierten Felds entspricht.~~ Jeder indizierte
>   Wert schreibt jetzt eine feste Anzahl Zeilen, aufgefüllt mit Attrappen, die keine Abfrage
>   erzeugen kann und die ein Dump nicht von echten Präfixen unterscheiden kann.
> - **Gleichheit und Häufigkeit.** Token sind konstruktionsbedingt deterministisch -- genau das lässt
>   die Suche funktionieren --, ein Dump zeigt also, welche Datensätze denselben Wert teilen und wie
>   häufig jeder Wert ist. Der Domain-Index gruppiert Ihre Population nach Arbeitgeber, was Personen
>   oft identifiziert, ohne eine Adresse wiederherzustellen.
> - **Gewählter Klartext.** Wer den Speicher lesen *und* zugleich Werte indizieren lassen kann (ein
>   Konto registrieren, per SCIM bereitgestellt werden), kann einen Kandidaten einreichen und nach
>   dessen Token suchen. Das rekonstruiert jeden erratbaren Wert -- verbreitete Domains, verbreitete
>   Vornamen --, gleichgültig wo der Schlüssel liegt, denn das Orakel ist der Schreibpfad, nicht die
>   Chiffre.
>
> Die Tokenisierung schützt gegen den Fall, für den sie gebaut wurde: jemand hat einen Dump und sonst
> nichts und will Adressen lesen. Die beiden verbleibenden Reste sind genau das, was ein
> Registrierungs-Orakel ohnehin preisgibt. Sind sie nicht hinnehmbar, lassen Sie die Tabellen für
> Präfix- und Domain-Index unkonfiguriert -- die Suche auf exakte Übereinstimmung trägt beides nicht
> -- statt anzunehmen, der HMAC decke sie ab.

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

Wie `IFieldCipher` ist er ein optionaler Store-Konstruktorparameter mit einem Passthrough-Standard (`NullIndexTokenizer`), sodass Index-Zeilen weiterhin auf Klartext geschlüsselt bleiben, bis Sie sich für die Verschlüsselung entscheiden. Zurückgegebene Tokens müssen als Azure Table PartitionKey-/RowKey-Werte sicher sein (keine `/ \ # ?` oder Steuerzeichen).

## Änderungsprotokoll-Erfassung: IChangeWriter

`IChangeWriter` (in 0.6.0 umbenannt von `ITombstoneWriter`) zeichnet den Schlüssel jeder geänderten Zeile in einer eigenen Änderungsprotokoll-Tabelle auf, sodass inkrementelle Sicherungen erkennen können, was sich geändert hat, ohne die nicht indizierte `Timestamp`-Spalte der Live-Tabellen zu durchsuchen. Löschungen werden für jede Tabelle erfasst (ein Scan der Live-Zeilen kann eine bereits gelöschte Zeile nicht sehen); Upserts werden für die Tabellen erfasst, bei denen die Sicherung aus dem Protokoll statt per Scan liest. Integrierte Implementierungen: `TableChangeWriter` (Azure Table Storage), `DynamoChangeWriter` (DynamoDB) und `SqlChangeWriter` (PostgreSQL / SQLite).

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

Reihenfolgevertrag für Implementierer und Aufrufer: Schreiben Sie den Lösch-Tombstone, BEVOR Sie die Datenzeile löschen. Ein Absturz in der umgekehrten Reihenfolge verliert die Löschung aus jeder zukünftigen Sicherung, da Löschungen die einzige Mutationsklasse sind, die ein erneuter Scan nicht selbst heilen kann. Der umgekehrte Absturz ist unbedenklich: Ein späterer Schreibvorgang auf den Schlüssel stempelt einen neueren Zeitstempel, und Merge/Wiederherstellung behalten Zeilen, die nach dem Tombstone geschrieben wurden.

## Benutzerdefinierte Endpunkte

Fügen Sie Ihre eigenen Endpunkte neben denen von Authagonal hinzu:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## HashiCorp Vault Transit-Integration

Authagonal kann die JWT-Signierung an die Transit Secrets Engine von HashiCorp Vault delegieren. Private Schlüssel verlassen Vault niemals; nur der Signiervorgang erfolgt remote. Öffentliche Schlüssel werden lokal für die Verifizierung zwischengespeichert.

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

Der `VaultTransitClient` bietet folgende Operationen:

| Methode | Beschreibung |
|---|---|
| `SignAsync(keyName, data)` | Signiert Daten mit einem Vault Transit-Schlüssel |
| `VerifyAsync(keyName, data, signature)` | Verifiziert eine JWS-serialisierte Signatur über den Transit-Verify-Endpunkt |
| `EncryptAsync` / `DecryptAsync` (+ `EncryptBatchAsync` / `DecryptBatchAsync`) | Symmetrische Verschlüsselung unter einem `aes256-gcm96`-Schlüssel; gibt `vault:v{n}:...`-Tokens zurück, die unverändert gespeichert werden |
| `HmacAsync` / `HmacBatchAsync` | Keyed HMAC unter einem `hmac`-Schlüssel (Blind-Index-Tokens) |
| `CreateKeyAsync(keyName, type)` | Erstellt einen neuen Transit-Schlüssel (Standard: `ecdsa-p256`) |
| `EnsureKeyTypeAsync(keyName, type)` | Stellt idempotent sicher, dass ein Schlüssel mit dem gewünschten Typ existiert (erstellt bei Typabweichung neu; Transit-Schlüssel können nicht nachträglich umtypisiert werden) |
| `RotateKeyAsync(keyName)` | Rotiert einen Schlüssel in eine neue Version |
| `DeleteKeyAsync(keyName)` | Löscht einen Schlüssel (aktiviert zuerst `deletion_allowed`) |
| `ReadKeyAsync(keyName)` | Liest Schlüsselmetadaten, Versionen und öffentliche Schlüssel |
| `KeyExistsAsync(keyName)` | Prüft, ob ein Schlüssel existiert |

Der `VaultTransitCryptoProvider` integriert sich in .NETs `JsonWebTokenHandler`, sodass die JWT-Signierung transparent Vault verwendet. Die `VaultTransitSecurityKey` und der `VaultTransitSignatureProvider` übernehmen die Low-Level-Integration.

## E-Mail

Der integrierte Resend-Sender aktiviert sich automatisch, wenn `Email:ResendApiKey` konfiguriert ist (setzen Sie auch `Email:SenderEmail`). Ohne einen `IEmailService` wird Mail über den `NullEmailService` verworfen, und da die Anmeldesperre für unbestätigte E-Mails standardmäßig aktiviert ist, könnten sich selbst registrierte Benutzer nie anmelden; `UseAuthagonal()` protokolliert in diesem Zustand eine deutliche Startwarnung.

Um einen anderen Anbieter zu verwenden, registrieren Sie Ihren eigenen `IEmailService` vor `AddAuthagonal()`:

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

`IEmailService` deklariert außerdem `SendAccountExistsEmailAsync` (wird gesendet, wenn jemand versucht, eine bereits registrierte E-Mail-Adresse erneut zu registrieren, wodurch die Registrierungsantwort neutral gegenüber Account-Enumeration bleibt). Sie hat eine standardmäßige No-op-Implementierung, sodass bestehende Implementierungen weiterhin kompilieren.

## Siehe auch

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server): vollständiges funktionierendes Beispiel
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app): Beispiel für eine Client-App
</content>

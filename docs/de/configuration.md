---
layout: default
title: Konfiguration
locale: de
---

# Konfiguration

Authagonal wird über `appsettings.json` oder Umgebungsvariablen konfiguriert. Umgebungsvariablen verwenden `__` als Abschnittstrennzeichen (z.B. `Storage__ConnectionString`).

## Erforderliche Einstellungen

Der Speicher kann auf zwei Arten konfiguriert werden — geben Sie **entweder** `Storage:ConnectionString` **oder** `Storage:TableServiceUri` an (der Pfad über Managed Identity, in der Produktion bevorzugt).

| Einstellung | Umgebungsvariable | Beschreibung |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Azure Table Storage Verbindungszeichenfolge mit einem Kontoschlüssel. Geeignet für Entwicklung / Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Table-Storage-Endpunkt über Managed Identity, z.B. `https://{account}.table.core.windows.net/`. Alternative zu `Storage:ConnectionString` und **in der Produktion bevorzugt** — authentifiziert sich über `DefaultAzureCredential`, sodass kein Zugriffsschlüssel jemals in einem Geheimnis landet. Der Host muss der Workload-Identität die Rolle **Storage Table Data Contributor** zuweisen. |
| `Issuer` | `Issuer` | Die öffentliche Basis-URL dieses Servers (z.B. `https://auth.example.com`) |

## Speicher

| Einstellung | Umgebungsvariable | Standard | Beschreibung |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(keine)* | Verbindungszeichenfolge mit Kontoschlüssel (siehe Erforderliche Einstellungen). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(keine)* | Table-Storage-URI über Managed Identity (siehe Erforderliche Einstellungen). Hat Vorrang vor `Storage:ConnectionString`, wenn beide gesetzt sind. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Ob die Praefixsuch-Indextabellen `UserFirstNames` / `UserLastNames` gepflegt werden, die die Admin-Namenspraefixsuche stuetzen. Auf `false` setzen bei Hosts, die keine Admin-Namenssuche anbieten, um diese Schreibvorgaenge zu vermeiden. **Skalierungshinweis:** Diese Indizes verwenden eine einzige heisse Partition und begrenzen den Durchsatz bei Skalierung auf etwa 2.000 Operationen/Sek. — deaktivieren Sie sie, wenn Sie keine Namenssuche benötigen. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | Basis-URL, zu der der `/connect/authorize`-Endpunkt für die Login-SPA umleitet (Anmelde-, Step-up- und Zustimmungsbildschirme). Setzen Sie diesen Wert, wenn die Login-Oberflaeche von einem anderen Ursprung als der Server ausgeliefert wird; Standard ist der relative Pfad `/login`, der von der gebuendelten SPA bereitgestellt wird. |

## Authentifizierung

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Cookie-Sitzungsdauer (gleitend) |
| `Auth:MaxFailedAttempts` | `5` | Fehlgeschlagene Anmeldeversuche vor Kontosperre |
| `Auth:LockoutDurationMinutes` | `10` | Kontosperrdauer nach maximalen Fehlversuchen |
| `Auth:MaxRegistrationsPerIp` | `5` | Maximale Registrierungen pro IP-Adresse innerhalb des Zeitfensters |
| `Auth:RegistrationWindowMinutes` | `60` | Zeitfenster für Registrierungsratenbegrenzung |
| `Auth:EmailVerificationExpiryHours` | `24` | Gültigkeitsdauer des E-Mail-Verifizierungslinks |
| `Auth:PasswordResetExpiryMinutes` | `60` | Gültigkeitsdauer des Passwortzurücksetzungslinks |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Gültigkeitsdauer des MFA-Abfrage-Tokens |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Gültigkeitsdauer des MFA-Setup-Tokens (für erzwungene Registrierung) |
| `Auth:Pbkdf2Iterations` | `100000` | PBKDF2-Iterationsanzahl für Passwort-Hashing |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Optionales Toleranzfenster (in Sekunden) für die gleichzeitige Wiederverwendung von Refresh-Tokens. `0` (Standard) behaelt die strenge Haltung bei: Jede Wiederverwendung eines bereits eingeloesten Refresh-Tokens widerruft alle Token für diesen Benutzer+Client. Auf einen Wert `> 0` setzen, um eine Wiederverwendung innerhalb des Fensters als idempotenten Wiederholungsversuch zu behandeln (die Nachfolger-Token werden erneut ausgeliefert) — nützlich für mobile Clients mit instabiler Verbindung. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Aktiviert den Endpunkt `POST /connect/register` für die dynamische Client-Registrierung (RFC 7591). Standardmäßig deaktiviert, da offene Registrierung in Multi-Mandanten-Deployments missbraucht werden kann. Siehe [Dynamische Client-Registrierung](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | RSA-Signaturschlüssel-Lebensdauer vor automatischer Rotation |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Wie oft Signaturschlüssel aus dem Speicher neu geladen werden |
| `Auth:KeyRotationEnabled` | `false` | Automatische Signaturschlüsselrotation aktivieren |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Wie oft geprueft wird, ob der aktive Schlüssel rotiert werden muss |
| `Auth:KeyRotationLeadTimeDays` | `14` | Rotieren, wenn der aktive Schlüssel innerhalb dieser Anzahl von Tagen ablaeuft |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervall zwischen Cookie-Sicherheitsstempel-Pruefungen |
| `DataProtection:BlobUri` | *(keine)* | Azure Blob-URI zur Persistierung von Data Protection-Schlüsseln über Instanzen hinweg |

## Cache und Timeouts

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Wie lange CORS-erlaubte Urspruenge gecacht werden |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Cache-Dauer des OIDC-Discovery-Dokuments |
| `Cache:SamlMetadataCacheMinutes` | `60` | Cache-Dauer der SAML-IdP-Metadaten |
| `Cache:OidcStateLifetimeMinutes` | `10` | Lebensdauer des OIDC-Autorisierungs-State-Parameters |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Lebensdauer der SAML-AuthnRequest-ID (Replay-Schutz) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Timeout für Table Storage Gesundheitspruefung |

## Hintergrunddienste

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Anfangsverzoegerung vor der ersten Bereinigung abgelaufener Token |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Intervall für die Bereinigung abgelaufener Token |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Anfangsverzoegerung vor der ersten Grant-Abstimmung |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Intervall für die Grant-Abstimmung |

## Clients

Clients werden im `Clients`-Array definiert und beim Start initialisiert. Jeder Client kann enthalten:

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientName": "My Application",
      "ClientSecretHashes": ["sha256-hash-here"],
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email", "custom-scope"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "AlwaysIncludeUserClaimsInIdToken": false,
      "AccessTokenLifetimeSeconds": 1800,
      "IdentityTokenLifetimeSeconds": 300,
      "AuthorizationCodeLifetimeSeconds": 300,
      "AbsoluteRefreshTokenLifetimeSeconds": 2592000,
      "SlidingRefreshTokenLifetimeSeconds": 1296000,
      "RefreshTokenUsage": "OneTime",
      "MfaPolicy": "Enabled",
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["my-backend"]
    }
  ]
}
```

### Gewährungstypen

| Gewährungstyp | Anwendungsfall |
|---|---|
| `authorization_code` | Interaktive Benutzeranmeldung (Webanwendungen, SPAs, Mobilgeräte) |
| `client_credentials` | Dienst-zu-Dienst-Kommunikation |
| `refresh_token` | Token-Erneuerung (erfordert `AllowOfflineAccess: true`) |
| `urn:ietf:params:oauth:grant-type:device_code` | Device-Authorization-Grant (RFC 8628) für Geräte mit eingeschraenkter Eingabe |

### Refresh-Token-Verwendung

| Wert | Verhalten |
|---|---|
| `OneTime` (Standard) | Bei jeder Aktualisierung wird ein neues Refresh-Token ausgestellt und das alte ungültig gemacht. Standardmäßig (`Auth:RefreshTokenReuseGraceSeconds = 0`) widerruft jede Wiederverwendung eines eingeloesten Tokens sofort alle Token für diesen Benutzer+Client — es gibt standardmäßig **kein** Toleranzfenster. Setzen Sie `Auth:RefreshTokenReuseGraceSeconds` auf einen positiven Wert, um ein Toleranzfenster für Wiederholungsversuche zu aktivieren. |
| `ReUse` | Dasselbe Refresh-Token wird bis zum Ablauf wiederverwendet. |

### Bereitstellungs-Apps

Das `ProvisioningApps`-Array verweist auf App-IDs, die im Konfigurationsabschnitt `ProvisioningApps` definiert sind. Wenn ein Benutzer sich über diesen Client autorisiert, wird er über TCC in diese Apps bereitgestellt. Details finden Sie unter [Bereitstellung](provisioning).

## Bereitstellungs-Apps

Definieren Sie nachgelagerte Anwendungen, in die Benutzer bereitgestellt werden sollen:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-api-key"
    },
    "analytics": {
      "CallbackUrl": "https://analytics.example.com/provisioning",
      "ApiKey": "another-key"
    }
  }
}
```

Die vollständige TCC-Protokollspezifikation finden Sie unter [Bereitstellung](provisioning).

## MFA-Richtlinie

Multi-Faktor-Authentifizierung wird pro Client über die Eigenschaft `MfaPolicy` durchgesetzt:

| Wert | Verhalten |
|---|---|
| `Disabled` (Standard) | Keine MFA-Abfrage, auch wenn der Benutzer MFA registriert hat |
| `Enabled` | Benutzer mit registrierter MFA werden abgefragt; keine erzwungene Registrierung |
| `Required` | Registrierte Benutzer werden abgefragt; Benutzer ohne MFA werden zur Registrierung gezwungen |

```json
{
  "Clients": [
    {
      "ClientId": "secure-app",
      "MfaPolicy": "Required"
    }
  ]
}
```

Wenn `MfaPolicy` auf `Required` gesetzt ist und der Benutzer keine MFA registriert hat, gibt die Anmeldung `{ mfaSetupRequired: true, setupToken: "..." }` zurück. Das Setup-Token authentifiziert den Benutzer bei den MFA-Setup-Endpunkten (über den `X-MFA-Setup-Token`-Header), damit er sich registrieren kann, bevor eine Cookie-Sitzung erstellt wird.

Foederierte Anmeldungen (SAML/OIDC) überspringen MFA -- der externe Identitaetsanbieter übernimmt dies.

### IAuthHook-Überschreibung

Die Methode `IAuthHook.ResolveMfaPolicyAsync` kann die Client-Richtlinie pro Benutzer überschreiben:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // MFA für Admin-Benutzer erzwingen, unabhängig von der Client-Einstellung
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

## Passwortrichtlinie

Passen Sie die Anforderungen an die Passwortkomplexitaet an:

```json
{
  "PasswordPolicy": {
    "MinLength": 10,
    "MinUniqueChars": 3,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": false
  }
}
```

| Eigenschaft | Standard | Beschreibung |
|---|---|---|
| `MinLength` | `8` | Minimale Passwortlaenge |
| `MinUniqueChars` | `2` | Mindestanzahl unterschiedlicher Zeichen |
| `RequireUppercase` | `true` | Mindestens ein Grossbuchstabe erforderlich |
| `RequireLowercase` | `true` | Mindestens ein Kleinbuchstabe erforderlich |
| `RequireDigit` | `true` | Mindestens eine Ziffer erforderlich |
| `RequireSpecialChar` | `true` | Mindestens ein nicht-alphanumerisches Zeichen erforderlich |

Die Richtlinie wird bei Passwortzurücksetzung und Admin-Benutzerregistrierung durchgesetzt. Die Login-Oberflaeche ruft die aktive Richtlinie von `GET /api/auth/password-policy` ab, um Anforderungen dynamisch anzuzeigen.

## SAML-Anbieter

Definieren Sie SAML-Identitaetsanbieter in der Konfiguration. Diese werden beim Start initialisiert:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com", "example.org"]
    }
  ]
}
```

| Eigenschaft | Erforderlich | Beschreibung |
|---|---|---|
| `ConnectionId` | Ja | Stabiler Bezeichner (verwendet in URLs wie `/saml/{connectionId}/login`) |
| `ConnectionName` | Nein | Anzeigename (Standard: ConnectionId) |
| `EntityId` | Ja | SAML Service Provider Entity ID |
| `MetadataLocation` | Ja | URL zur SAML-Metadaten-XML des IdP |
| `AllowedDomains` | Nein | E-Mail-Domaenen, die über SSO zu diesem Anbieter geleitet werden |

## OIDC-Anbieter

Definieren Sie OIDC-Identitaetsanbieter in der Konfiguration. Diese werden beim Start initialisiert:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

| Eigenschaft | Erforderlich | Beschreibung |
|---|---|---|
| `ConnectionId` | Ja | Stabiler Bezeichner (verwendet in URLs wie `/oidc/{connectionId}/login`) |
| `ConnectionName` | Nein | Anzeigename (Standard: ConnectionId) |
| `MetadataLocation` | Ja | URL zum OpenID Connect Discovery-Dokument des IdP |
| `ClientId` | Ja | Beim IdP registrierte OAuth2-Client-ID |
| `ClientSecret` | Ja | OAuth2-Client-Geheimnis (beim Start über `ISecretProvider` geschuetzt) |
| `RedirectUrl` | Ja | Beim IdP registrierte OAuth2-Weiterleitungs-URI |
| `AllowedDomains` | Nein | E-Mail-Domaenen, die über SSO zu diesem Anbieter geleitet werden |

> **Hinweis:** Anbieter können auch zur Laufzeit über die [Admin-API](admin-api) verwaltet werden. Konfigurationsinitialisierte Anbieter werden bei jedem Start per Upsert aktualisiert, sodass Konfigurationsänderungen nach dem Neustart wirksam werden.

## Geheimnis-Anbieter

Geheimnisse von vorgelagerten OIDC-Clients sowie TOTP-/MFA-Seeds können statt im Klartext in Azure Key Vault gespeichert werden:

| Einstellung | Beschreibung |
|---|---|
| `SecretProvider:VaultUri` | Key Vault URI (z.B. `https://my-vault.vault.azure.net/`). Wenn nicht gesetzt, wird der **Klartext**-Anbieter verwendet und Geheimnisse werden unverändert in Table Storage gespeichert. |

Bei Konfiguration werden Geheimniswerte, die wie Key Vault-Referenzen aussehen, zur Laufzeit aufgeloest. Verwendet `DefaultAzureCredential` zur Authentifizierung.

> ⚠️ **Produktion: `SecretProvider:VaultUri` setzen.** Der Standard-Geheimnis-Anbieter speichert im **Klartext**. Wenn `SecretProvider:VaultUri` nicht gesetzt ist, werden Geheimnisse von vorgelagerten OIDC-Clients sowie TOTP-/MFA-Seeds im Klartext in Azure Table Storage geschrieben — und erscheinen daher auch im Klartext in jeder [Sicherung](backup-restore). Konfigurieren Sie für jedes Produktions-Deployment `SecretProvider:VaultUri`, damit diese Geheimnisse in Key Vault gespeichert werden.

## Admin-API

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Standardmäßig aktiviert.** Auf `false` setzen, um alle Admin-Endpunkte zu deaktivieren (sie werden dann nicht registriert). |
| `AdminApi:Scope` | `authagonal-admin` | JWT-Scope, der für den Zugriff auf Admin-Endpunkte erforderlich ist. Ändern Sie dies, um Ihren vorhandenen Scope-Namen abzubilden (z.B. `projects-identity-admin` für IdentityServer-Migrationen). |

> ⚠️ **Die Admin-API ist standardmäßig aktiviert und hochprivilegiert.** Der Admin-Scope gewährt vollständige Verwaltung und Benutzer-Imitation — jeder, der ein Token mit `AdminApi:Scope` besitzt, kann Token für beliebige Benutzer ausstellen, Clients verwalten und die gesamte Konfiguration lesen/schreiben. Beschraenken Sie die Admin-Endpunkte (die `/api/v1/*`-Admin-Routen) auf Netzwerkebene und kontrollieren Sie streng, wem der Admin-Scope ausgestellt werden kann. Als zusätzliche Schutzmassnahme ist der Scope *reserviert*: Er kann niemals einem OAuth-Client gewährt werden (siehe [Admin-API](admin-api)) und kann nicht über den Imitations-Endpunkt ausgestellt werden. Setzen Sie `AdminApi:Enabled = false`, wenn die Admin-API nicht verwendet wird.

## Zustimmung

Die Zustimmung pro Client kann mit der Eigenschaft `RequireConsent` aktiviert werden:

| Wert | Verhalten |
|---|---|
| `false` (Standard) | Die Autorisierung wird unmittelbar nach der Authentifizierung fortgesetzt |
| `true` | Dem Benutzer wird ein Zustimmungsbildschirm mit den angeforderten Scopes angezeigt. Die Zustimmung wird 5 Jahre lang gespeichert und nur bei der Anforderung neuer Scopes erneut abgefragt. |

Benutzer können ihre Zustimmungserteilungen unter `GET /consent/grants` einsehen und unter `DELETE /consent/grants/{clientId}` widerrufen.

## Back-Channel-Logout

Registrieren Sie eine `BackChannelLogoutUri` an einem Client, um Benachrichtigungen gemäß OIDC Back-Channel Logout 1.0 zu empfangen. Wenn sich ein Benutzer abmeldet, sendet Authagonal ein signiertes Logout-Token (JWT) an die registrierte URI jedes Clients.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback"
    }
  ]
}
```

## E-Mail

Standardmäßig verwendet Authagonal einen No-Op-E-Mail-Dienst, der alle E-Mails stillschweigend verwirft. Um den E-Mail-Versand zu aktivieren, registrieren Sie eine `IEmailService`-Implementierung vor dem Aufruf von `AddAuthagonal()`.

Der integrierte `EmailService` verwendet [Resend](https://resend.com). Um ihn zu verwenden, registrieren Sie ihn explizit:

```csharp
services.AddSingleton<IEmailService, EmailService>();
services.AddAuthagonal(configuration);
```

| Einstellung | Beschreibung |
|---|---|
| `Email:ResendApiKey` | Resend-API-Schlüssel zum Versenden von E-Mails |
| `Email:SenderEmail` | Absender-E-Mail-Adresse |
| `Email:SenderName` | Absender-Anzeigename (Standard: `"Authagonal"`) |

E-Mails an `@example.com`-Adressen werden stillschweigend übersprungen (nützlich zum Testen).

## Cluster

Authagonal-Instanzen bilden automatisch einen Cluster, um den Ratenbegrenzungsstatus zu teilen. Clustering ist standardmäßig ohne Konfiguration aktiviert.

| Einstellung | Umgebungsvariable | Standard | Beschreibung |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Hauptschalter für Clustering. Auf `false` setzen für lokale Ratenbegrenzung. |
| `Cluster:MulticastGroup` | `Cluster__MulticastGroup` | `239.42.42.42` | UDP-Multicast-Gruppe für Peer-Erkennung |
| `Cluster:MulticastPort` | `Cluster__MulticastPort` | `19847` | UDP-Multicast-Port für Peer-Erkennung |
| `Cluster:InternalUrl` | `Cluster__InternalUrl` | *(keine)* | Load-Balanced-Fallback-URL für Gossip, wenn Multicast nicht verfügbar ist |
| `Cluster:Secret` | `Cluster__Secret` | *(keine)* | Gemeinsames Geheimnis, das auf den rein internen Endpunkten (`/_internal/cluster/gossip` und `/_internal/backchannel-logout`) erforderlich ist. Wenn gesetzt, müssen Aufrufer es im Header `X-Cluster-Secret` praesentieren (Vergleich in konstanter Zeit). Wenn **nicht gesetzt**, sind diese Endpunkte nur von Loopback- / privaten (RFC 1918 / Link-Local / ULA) Quell-IPs erreichbar — eine externe Anfrage mit einer öffentlichen IP wird abgelehnt. Empfohlen, sobald `InternalUrl` Gossip über einen Load Balancer leitet. |
| `Cluster:GossipIntervalSeconds` | `Cluster__GossipIntervalSeconds` | `5` | Wie oft Instanzen den Ratenbegrenzungsstatus austauschen |
| `Cluster:DiscoveryIntervalSeconds` | `Cluster__DiscoveryIntervalSeconds` | `10` | Wie oft Instanzen sich per Multicast ankuendigen |
| `Cluster:PeerStaleAfterSeconds` | `Cluster__PeerStaleAfterSeconds` | `30` | Peers verwerfen, von denen nach dieser Anzahl Sekunden nichts gehört wurde |

**Zero-Config (Standard):** Instanzen finden sich gegenseitig über UDP-Multicast. Funktioniert in Kubernetes, Docker Compose oder jedem gemeinsamen Netzwerk.

**Multicast deaktiviert (z.B. einige Cloud-VPCs):**

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

**Clustering vollständig deaktiviert:**

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

Weitere Details zur verteilten Ratenbegrenzung finden Sie unter [Skalierung](scaling).

## Weitergeleitete Header (vertrauenswuerdiger Proxy)

Authagonal verschlüsselt Ratenbegrenzung und Kontosperre anhand der Client-IP und sendet HSTS nur bei HTTPS-Anfragen. Hinter einem Reverse-Proxy / Ingress treffen die echte Client-IP und das Schema in den Headern `X-Forwarded-For` / `X-Forwarded-Proto` ein. Diese Einstellungen steuern, **welchen Proxy-Hops vertraut wird**, diese Werte zu setzen, damit ein Aufrufer nicht `X-Forwarded-For` faelschen kann, um die Client-IP vorzutaeuschen.

| Einstellung | Umgebungsvariable | Standard | Beschreibung |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Anzahl der Proxy-Hops, die von rechts in der `X-Forwarded-For`-Kette berücksichtigt werden. Der Standardwert `1` vertraut nur dem einzelnen Hop, den Ihr Ingress anhaengt, und ignoriert alles weiter links in der Kette. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (Array) | *(leer)* | CIDR-Bereiche (String-Array, z.B. `"10.0.0.0/8"`), die weitergeleitete Header setzen duerfen. **Staerkste Garantie:** Setzen Sie dies auf Ihr Ingress- / Pod-CIDR, sodass nur dieses Netzwerk die Client-IP setzen darf. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (Array) | *(leer)* | Einzelne Proxy-IP-Adressen (String-Array), die weitergeleitete Header setzen duerfen. Verwenden Sie dies zusammen mit oder anstelle von `KnownNetworks`. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

> ⚠️ **TLS-terminierender Proxy erforderlich.** Authagonal muss hinter einem TLS-terminierenden Reverse-Proxy laufen. Das Sitzungs-Cookie verwendet `SecurePolicy = SameAsRequest`, und HSTS (`Strict-Transport-Security`) wird nur bei HTTPS-Anfragen gesendet, sodass der Proxy `X-Forwarded-Proto: https` weiterleiten muss, damit Cookies als `Secure` markiert werden und HSTS gesendet wird. Konfigurieren Sie `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` auf Ihren vertrauenswuerdigen Proxy, damit Schema und Client-IP nicht gefaelscht werden können.

## Ratenbegrenzung

Integrierte IP-basierte Ratenbegrenzungen werden über das Cluster-Gossip-Protokoll auf allen Instanzen durchgesetzt:

| Endpunkt | Limit | Zeitfenster |
|---|---|---|
| `POST /api/auth/register` | 5 Registrierungen | 1 Stunde |

Wenn Clustering aktiviert ist, werden diese Limits über alle Instanzen hinweg konsolidiert. Bei Deaktivierung setzt jede Instanz ihr eigenes Limit unabhängig durch.

## CORS

CORS wird dynamisch konfiguriert. Urspruenge aus den `AllowedCorsOrigins` aller registrierten Clients werden automatisch zugelassen, mit einem 60-Minuten-Cache.

## HashiCorp Vault Transit

Authagonal kann JWTs mit der HashiCorp Vault Transit Secrets Engine signieren. Private Schlüssel verlassen Vault nie — nur die Signaturoperation wird remote delegiert. Öffentliche Schlüssel werden lokal zur Verifizierung zwischengespeichert.

Dies wird programmatisch konfiguriert, wenn als Bibliothek gehostet wird. Details finden Sie unter [Erweiterbarkeit](extensibility).

## Vollständiges Beispiel

```json
{
  "Storage": {
    "TableServiceUri": "https://myaccount.table.core.windows.net/",
    "NameIndexesEnabled": true
  },
  "Issuer": "https://auth.example.com",
  "LoginAppUrl": "/login",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "RefreshTokenReuseGraceSeconds": 0,
    "DynamicClientRegistrationEnabled": false,
    "SigningKeyLifetimeDays": 90
  },
  "SecretProvider": {
    "VaultUri": "https://my-vault.vault.azure.net/"
  },
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"]
  },
  "Cluster": {
    "Enabled": true,
    "Secret": "shared-secret-here"
  },
  "AdminApi": {
    "Enabled": true,
    "Scope": "authagonal-admin"
  },
  "Authentication": {
    "CookieLifetimeHours": 48
  },
  "PasswordPolicy": {
    "MinLength": 8,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": true
  },
  "Email": {
    "ResendApiKey": "re_xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth"
  },
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com"]
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "...",
      "ClientSecret": "...",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["gmail.com"]
    }
  ],
  "ProvisioningApps": {
    "backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret"
    }
  },
  "Clients": [
    {
      "ClientId": "web",
      "ClientName": "Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "MfaPolicy": "Enabled",
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```

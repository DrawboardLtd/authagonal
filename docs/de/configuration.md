---
layout: default
title: Konfiguration
locale: de
---

# Konfiguration

Authagonal wird ueber `appsettings.json` oder Umgebungsvariablen konfiguriert. Umgebungsvariablen verwenden `__` als Abschnittstrennzeichen (z.B. `Storage__ConnectionString`).

## Erforderliche Einstellungen

| Einstellung | Umgebungsvariable | Beschreibung |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Azure Table Storage Verbindungszeichenfolge |
| `Issuer` | `Issuer` | Die oeffentliche Basis-URL dieses Servers (z.B. `https://auth.example.com`) |

## Authentifizierung

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Cookie-Sitzungsdauer (gleitend) |
| `Auth:MaxFailedAttempts` | `5` | Fehlgeschlagene Anmeldeversuche vor Kontosperre |
| `Auth:LockoutDurationMinutes` | `10` | Kontosperrdauer nach maximalen Fehlversuchen |
| `Auth:MaxRegistrationsPerIp` | `5` | Maximale Registrierungen pro IP-Adresse innerhalb des Zeitfensters |
| `Auth:RegistrationWindowMinutes` | `60` | Zeitfenster fuer Registrierungsratenbegrenzung |
| `Auth:EmailVerificationExpiryHours` | `24` | Gueltigkeitsdauer des E-Mail-Verifizierungslinks |
| `Auth:PasswordResetExpiryMinutes` | `60` | Gueltigkeitsdauer des Passwortzuruecksetzungslinks |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Gueltigkeitsdauer des MFA-Abfrage-Tokens |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Gueltigkeitsdauer des MFA-Setup-Tokens (fuer erzwungene Registrierung) |
| `Auth:Pbkdf2Iterations` | `100000` | PBKDF2-Iterationsanzahl fuer Passwort-Hashing |
| `Auth:RefreshTokenReuseGraceSeconds` | `60` | Toleranzfenster fuer gleichzeitige Refresh-Token-Wiederverwendung |
| `Auth:SigningKeyLifetimeDays` | `90` | RSA-Signaturschluessel-Lebensdauer vor automatischer Rotation |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Wie oft Signaturschluessel aus dem Speicher neu geladen werden |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervall zwischen Cookie-Sicherheitsstempel-Pruefungen |
| `DataProtection:BlobUri` | *(keine)* | Azure Blob-URI zur Persistierung von Data Protection-Schluesseln ueber Instanzen hinweg |

## Cache und Timeouts

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Wie lange CORS-erlaubte Urspruenge gecacht werden |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Cache-Dauer des OIDC-Discovery-Dokuments |
| `Cache:SamlMetadataCacheMinutes` | `60` | Cache-Dauer der SAML-IdP-Metadaten |
| `Cache:OidcStateLifetimeMinutes` | `10` | Lebensdauer des OIDC-Autorisierungs-State-Parameters |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Lebensdauer der SAML-AuthnRequest-ID (Replay-Schutz) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Timeout fuer Table Storage Gesundheitspruefung |

## Hintergrunddienste

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Anfangsverzoegerung vor der ersten Bereinigung abgelaufener Token |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Intervall fuer die Bereinigung abgelaufener Token |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Anfangsverzoegerung vor der ersten Grant-Abstimmung |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Intervall fuer die Grant-Abstimmung |

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
      "ProvisioningApps": ["my-backend"]
    }
  ]
}
```

### Gewaehrungstypen

| Gewaehrungstyp | Anwendungsfall |
|---|---|
| `authorization_code` | Interaktive Benutzeranmeldung (Webanwendungen, SPAs, Mobilgeraete) |
| `client_credentials` | Dienst-zu-Dienst-Kommunikation |
| `refresh_token` | Token-Erneuerung (erfordert `AllowOfflineAccess: true`) |

### Refresh-Token-Verwendung

| Wert | Verhalten |
|---|---|
| `OneTime` (Standard) | Bei jeder Aktualisierung wird ein neues Refresh-Token ausgestellt. Das alte wird mit einem 60-Sekunden-Toleranzfenster fuer gleichzeitige Anfragen ungueltig. Wiederholung nach dem Toleranzfenster widerruft alle Token fuer diesen Benutzer+Client. |
| `ReUse` | Dasselbe Refresh-Token wird bis zum Ablauf wiederverwendet. |

### Bereitstellungs-Apps

Das `ProvisioningApps`-Array verweist auf App-IDs, die im Konfigurationsabschnitt `ProvisioningApps` definiert sind. Wenn ein Benutzer sich ueber diesen Client autorisiert, wird er ueber TCC in diese Apps bereitgestellt. Details finden Sie unter [Bereitstellung](provisioning).

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

Die vollstaendige TCC-Protokollspezifikation finden Sie unter [Bereitstellung](provisioning).

## MFA-Richtlinie

Multi-Faktor-Authentifizierung wird pro Client ueber die Eigenschaft `MfaPolicy` durchgesetzt:

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

Wenn `MfaPolicy` auf `Required` gesetzt ist und der Benutzer keine MFA registriert hat, gibt die Anmeldung `{ mfaSetupRequired: true, setupToken: "..." }` zurueck. Das Setup-Token authentifiziert den Benutzer bei den MFA-Setup-Endpunkten (ueber den `X-MFA-Setup-Token`-Header), damit er sich registrieren kann, bevor eine Cookie-Sitzung erstellt wird.

Foederierte Anmeldungen (SAML/OIDC) ueberspringen MFA -- der externe Identitaetsanbieter uebernimmt dies.

### IAuthHook-Ueberschreibung

Die Methode `IAuthHook.ResolveMfaPolicyAsync` kann die Client-Richtlinie pro Benutzer ueberschreiben:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // MFA fuer Admin-Benutzer erzwingen, unabhaengig von der Client-Einstellung
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

Die Richtlinie wird bei Passwortzuruecksetzung und Admin-Benutzerregistrierung durchgesetzt. Die Login-Oberflaeche ruft die aktive Richtlinie von `GET /api/auth/password-policy` ab, um Anforderungen dynamisch anzuzeigen.

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
| `AllowedDomains` | Nein | E-Mail-Domaenen, die ueber SSO zu diesem Anbieter geleitet werden |

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
| `ClientSecret` | Ja | OAuth2-Client-Geheimnis (beim Start ueber `ISecretProvider` geschuetzt) |
| `RedirectUrl` | Ja | Beim IdP registrierte OAuth2-Weiterleitungs-URI |
| `AllowedDomains` | Nein | E-Mail-Domaenen, die ueber SSO zu diesem Anbieter geleitet werden |

> **Hinweis:** Anbieter koennen auch zur Laufzeit ueber die [Admin-API](admin-api) verwaltet werden. Konfigurationsinitialisierte Anbieter werden bei jedem Start per Upsert aktualisiert, sodass Konfigurationsaenderungen nach dem Neustart wirksam werden.

## Geheimnis-Anbieter

Client-Geheimnisse und OIDC-Anbieter-Geheimnisse koennen optional in Azure Key Vault gespeichert werden:

| Einstellung | Beschreibung |
|---|---|
| `SecretProvider:VaultUri` | Key Vault URI (z.B. `https://my-vault.vault.azure.net/`). Wenn nicht gesetzt, werden Geheimnisse als Klartext behandelt. |

Bei Konfiguration werden Geheimniswerte, die wie Key Vault-Referenzen aussehen, zur Laufzeit aufgeloest. Verwendet `DefaultAzureCredential` zur Authentifizierung.

## E-Mail

Standardmäßig verwendet Authagonal einen No-Op-E-Mail-Dienst, der alle E-Mails stillschweigend verwirft. Um den E-Mail-Versand zu aktivieren, registrieren Sie eine `IEmailService`-Implementierung vor dem Aufruf von `AddAuthagonal()`. Der integrierte `EmailService` verwendet SendGrid.

| Einstellung | Beschreibung |
|---|---|
| `Email:SendGridApiKey` | SendGrid-API-Schluessel zum Versenden von E-Mails |
| `Email:SenderEmail` | Absender-E-Mail-Adresse |
| `Email:SenderName` | Absender-Anzeigename |
| `Email:VerificationTemplateId` | SendGrid Dynamic Template ID fuer E-Mail-Verifizierung |
| `Email:PasswordResetTemplateId` | SendGrid Dynamic Template ID fuer Passwortzuruecksetzung |

E-Mails an `@example.com`-Adressen werden stillschweigend uebersprungen (nuetzlich zum Testen).

## Cluster

Authagonal-Instanzen bilden automatisch einen Cluster, um den Ratenbegrenzungsstatus zu teilen. Clustering ist standardmaessig ohne Konfiguration aktiviert.

| Einstellung | Umgebungsvariable | Standard | Beschreibung |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Hauptschalter fuer Clustering. Auf `false` setzen fuer lokale Ratenbegrenzung. |
| `Cluster:MulticastGroup` | `Cluster__MulticastGroup` | `239.42.42.42` | UDP-Multicast-Gruppe fuer Peer-Erkennung |
| `Cluster:MulticastPort` | `Cluster__MulticastPort` | `19847` | UDP-Multicast-Port fuer Peer-Erkennung |
| `Cluster:InternalUrl` | `Cluster__InternalUrl` | *(keine)* | Load-Balanced-Fallback-URL fuer Gossip, wenn Multicast nicht verfuegbar ist |
| `Cluster:Secret` | `Cluster__Secret` | *(keine)* | Gemeinsames Geheimnis fuer Gossip-Endpunkt-Authentifizierung (empfohlen wenn `InternalUrl` gesetzt ist) |
| `Cluster:GossipIntervalSeconds` | `Cluster__GossipIntervalSeconds` | `5` | Wie oft Instanzen den Ratenbegrenzungsstatus austauschen |
| `Cluster:DiscoveryIntervalSeconds` | `Cluster__DiscoveryIntervalSeconds` | `10` | Wie oft Instanzen sich per Multicast ankuendigen |
| `Cluster:PeerStaleAfterSeconds` | `Cluster__PeerStaleAfterSeconds` | `30` | Peers verwerfen, von denen nach dieser Anzahl Sekunden nichts gehoert wurde |

**Zero-Config (Standard):** Instanzen finden sich gegenseitig ueber UDP-Multicast. Funktioniert in Kubernetes, Docker Compose oder jedem gemeinsamen Netzwerk.

**Multicast deaktiviert (z.B. einige Cloud-VPCs):**

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

**Clustering vollstaendig deaktiviert:**

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

Weitere Details zur verteilten Ratenbegrenzung finden Sie unter [Skalierung](scaling).

## Ratenbegrenzung

Integrierte IP-basierte Ratenbegrenzungen werden ueber das Cluster-Gossip-Protokoll auf allen Instanzen durchgesetzt:

| Endpunkt | Limit | Zeitfenster |
|---|---|---|
| `POST /api/auth/register` | 5 Registrierungen | 1 Stunde |

Wenn Clustering aktiviert ist, werden diese Limits ueber alle Instanzen hinweg konsolidiert. Bei Deaktivierung setzt jede Instanz ihr eigenes Limit unabhaengig durch.

## CORS

CORS wird dynamisch konfiguriert. Urspruenge aus den `AllowedCorsOrigins` aller registrierten Clients werden automatisch zugelassen, mit einem 60-Minuten-Cache.

## Vollstaendiges Beispiel

```json
{
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..."
  },
  "Issuer": "https://auth.example.com",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "SigningKeyLifetimeDays": 90
  },
  "Cluster": {
    "Enabled": true
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
    "SendGridApiKey": "SG.xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth",
    "VerificationTemplateId": "d-xxx",
    "PasswordResetTemplateId": "d-yyy"
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
      "ProvisioningApps": ["backend"]
    }
  ]
}
```

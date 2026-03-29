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
| `Email:FromAddress` | Absender-E-Mail-Adresse |
| `Email:FromName` | Absender-Anzeigename |
| `Email:VerificationTemplateId` | SendGrid Dynamic Template ID fuer E-Mail-Verifizierung |
| `Email:PasswordResetTemplateId` | SendGrid Dynamic Template ID fuer Passwortzuruecksetzung |

E-Mails an `@example.com`-Adressen werden stillschweigend uebersprungen (nuetzlich zum Testen).

## Ratenbegrenzung

Integrierte IP-basierte Ratenbegrenzungen:

| Endpunktgruppe | Limit | Zeitfenster |
|---|---|---|
| Auth-Endpunkte (Login, SSO) | 20 Anfragen | 1 Minute |
| Token-Endpunkt | 30 Anfragen | 1 Minute |

## CORS

CORS wird dynamisch konfiguriert. Urspruenge aus den `AllowedCorsOrigins` aller registrierten Clients werden automatisch zugelassen, mit einem 60-Minuten-Cache.

## Vollstaendiges Beispiel

```json
{
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..."
  },
  "Issuer": "https://auth.example.com",
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
    "FromAddress": "noreply@example.com",
    "FromName": "Example Auth",
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
      "ProvisioningApps": ["backend"]
    }
  ]
}
```

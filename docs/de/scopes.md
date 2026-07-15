---
layout: default
title: OAuth Scopes
locale: de
---

# OAuth-Scopes

Authagonal unterstützt sowohl **integrierte** OAuth/OIDC-Scopes als auch **benutzerdefinierte** Scopes, die zur Laufzeit verwaltet werden. Benutzerdefinierte Scopes werden dauerhaft gespeichert, über das Discovery-Dokument bekannt gegeben und zusammen mit den integrierten Scopes auf dem Zustimmungsbildschirm angezeigt.

## Integrierte Scopes

Diese Scopes sind immer verfügbar und müssen nicht registriert werden:

| Scope | Zweck |
|---|---|
| `openid` | Erforderlich, um einen OIDC-Ablauf zu starten. Stellt ein ID-Token aus. |
| `profile` | Standard-Profil-Claims (name, family_name, given_name usw.) |
| `email` | E-Mail-Adresse und `email_verified`-Claims |
| `offline_access` | Stellt zusätzlich zum Zugriffstoken ein Refresh Token aus |

## Benutzerdefinierte Scopes

Benutzerdefinierte Scopes werden über die Admin-API unter `/api/v1/scopes` verwaltet. Sie erfordern ein JWT-Zugriffstoken mit dem Scope `authagonal-admin` (konfigurierbar über `AdminApi:Scope`).

### Scope-Modell

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| Feld | Beschreibung |
|---|---|
| `Name` | Die Scope-Kennung, die in Token-Anfragen gesendet wird (z. B. `billing.read`) |
| `DisplayName` | Menschenlesbarer Name, der auf dem Zustimmungsbildschirm angezeigt wird |
| `Description` | Längere Beschreibung, die auf dem Zustimmungsbildschirm angezeigt wird |
| `Emphasize` | Bei `true` hebt der Zustimmungsbildschirm diesen Scope als sensibel hervor |
| `Required` | Bei `true` kann der Benutzer diesen Scope beim Zustimmen nicht abwählen |
| `ShowInDiscoveryDocument` | Bei `true` erscheint der Scope in `/.well-known/openid-configuration` unter `scopes_supported` |
| `UserClaims` | Claims, die dem Zugriffstoken hinzugefügt werden, wenn dieser Scope gewährt wird |

## Admin-Endpunkte

### Scopes auflisten

```
GET /api/v1/scopes
```

Gibt `{ "scopes": [ ... ] }` zurück.

### Scope abrufen

```
GET /api/v1/scopes/{name}
```

Gibt den Scope zurück oder `404`, wenn er nicht gefunden wird.

### Scope erstellen

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing — read-only",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

Gibt `201 Created` mit dem Scope zurück. Gibt `409` zurück, wenn bereits ein Scope mit demselben Namen existiert.

### Scope aktualisieren

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing — read",
  "description": "View invoices",
  "emphasize": true
}
```

Nur die angegebenen Felder werden aktualisiert; ausgelassene Felder behalten ihre aktuellen Werte.

### Scope löschen

```
DELETE /api/v1/scopes/{name}
```

Gibt `204 No Content` zurück (`404`, wenn der Scope nicht existiert). Bereits ausgestellte Token, die diesen Scope enthalten, bleiben bis zu ihrem Ablauf gültig; widerrufen Sie sie bei Bedarf explizit über `/connect/revocation`.

## Discovery-Dokument

Scopes mit `ShowInDiscoveryDocument = true` erscheinen unter `scopes_supported` in `/.well-known/openid-configuration`. Integrierte Scopes werden immer bekannt gegeben.

```json
{
  "scopes_supported": ["openid", "profile", "email", "offline_access", "billing.read"]
}
```

## Zustimmungsbildschirm

Wenn ein Client einen Scope anfordert, der nicht in seiner Liste der zustimmungsfreien Scopes enthalten ist, listet der Zustimmungsbildschirm jeden angeforderten Scope nach `DisplayName` (mit Rückgriff auf `Name`) mit der `Description` darunter auf. Scopes mit `Emphasize = true` erhalten eine optisch abgehobene Darstellung. `Required`-Scopes können nicht abgewählt werden.

Siehe [OAuth-Zustimmungsbildschirm](index#features) für den benutzerseitigen Ablauf.

## Dynamische Client-Registrierung

Clients, die über [Dynamische Client-Registrierung](client-registration) registriert wurden, dürfen nur Scopes anfordern, die entweder integriert oder zuvor über die Admin-API erstellt wurden. Unbekannte Scopes werden mit `invalid_scope` abgelehnt.

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
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
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
| `Group` | Überschrift auf dem Zustimmungsbildschirm, unter der dieser Scope einsortiert wird. Reine Darstellung -- es beeinflusst nie, was gewährt wird |
| `Required` | Bei `true` kann der Benutzer diesen Scope beim Zustimmen nicht abwählen |
| `ShowInDiscoveryDocument` | Bei `true` erscheint der Scope in `/.well-known/openid-configuration` unter `scopes_supported` |
| `AllowedRoles` | Rollen, die ein Benutzer besitzen muss, damit ihm dieser Scope gewährt wird. Leer (Standard) lässt ihn ungebunden -- siehe [Rollengebundene Scopes](#role-gated-scopes) |
| `UserClaims` | Claims, die dem Zugriffstoken hinzugefügt werden, wenn dieser Scope gewährt wird |

### Rollengebundene Scopes {#role-gated-scopes}

Die `AllowedScopes` eines Clients beantworten *darf diese Anwendung diesen Scope überhaupt anfragen*
-- eine Frage, die geklärt ist, bevor sich irgendjemand angemeldet hat. `AllowedRoles` beantwortet
die andere Hälfte: *darf diese Person ihn haben*. Beide Schranken gelten, und keine ersetzt die
andere.

```json
{
  "name": "staff-admin",
  "displayName": "Staff administration",
  "allowedRoles": ["staff", "super-admin"]
}
```

Einem Benutzer, der keine der aufgeführten Rollen besitzt, wird der Scope **aus der Gewährung
entfernt**, nicht verweigert: Der Client hat seinen vollständigen Satz angefragt und erfährt über den
in der Token-Antwort zurückgegebenen `scope` (RFC 6749 §3.3), dass er weniger erhalten hat. Genau das
erlaubt es einer Anwendung, sowohl Mitarbeitende als auch alle anderen zu bedienen -- die
Mitarbeitendenoberfläche ist einer von mehreren Scopes, und nur die dazu Berechtigten erhalten ihn.

Eine Anfrage, bei der *jeder* angefragte Scope entfernt wird, scheitert mit `access_denied`, weil
nichts mehr übrig ist, wofür ein Token ausgestellt werden könnte.

Die Schranke gilt überall dort, wo ein Token für einen Menschen ausgestellt wird:

| Ablauf | Wo sie greift |
|---|---|
| Authorization Code | Bei `/connect/authorize`, sobald der Benutzer bekannt ist und **vor** der Zustimmung -- so bietet der Bildschirm nie eine Berechtigung an, die nicht gewährt werden kann |
| Device Code | Bei `/api/auth/device/approve`, dem ersten Punkt in diesem Ablauf, an dem das Subjekt bekannt ist |
| Refresh | Bei jeder Rotation, gegen frisch aufgelöste Rollen. Hier wird der Entzug einer Rolle tatsächlich wirksam, denn die Gewährung hält weiterhin fest, was bei der Anmeldung genehmigt wurde |
| Token Exchange | Nicht gesondert gebunden: Ein Exchange darf nur innerhalb der Scopes des Subject-Tokens herunterskalieren und kann daher nie einen erreichen, den das Subjekt nicht erhalten hat |

Client-Credentials-Gewährungen haben kein Subjekt und bleiben bewusst unberührt -- die Autorität
eines Maschinen-Clients ist seine Registrierung.

Das Initialisieren eines Scopes aus der Konfiguration kann `AllowedRoles` hinzufügen oder ändern,
aber nicht leeren (wie bei `UserClaims` bewahrt ein weggelassenes Feld den gespeicherten Wert). Um
eine Bindung zu entfernen, senden Sie den Scope per `PUT` mit einem ausdrücklich leeren Array.

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

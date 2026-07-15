---
layout: default
title: SCIM 2.0-Bereitstellung
locale: de
---

# SCIM 2.0-Bereitstellung

Authagonal unterstützt SCIM 2.0 (System for Cross-domain Identity Management) für die automatische Benutzerbereitstellung durch Enterprise-Identitätsanbieter wie Microsoft Entra ID, Okta und OneLogin.

## Übersicht

SCIM ist ein eingehendes Bereitstellungsprotokoll: Ihr Identitätsanbieter überträgt Benutzer- und Gruppenänderungen an Authagonal. Dies ergänzt die bestehende TCC-Bereitstellung (Try-Confirm-Cancel), die Benutzer ausgehend an nachgelagerte Anwendungen überträgt.

**Unterstützte Vorgänge:**
- Benutzer-CRUD (Erstellen, Lesen, Aktualisieren, Löschen über Soft-Deaktivierung)
- Gruppen-CRUD mit Mitgliederverwaltung
- Filterung (Operatoren `eq` und `co` für `userName`, `externalId`, `displayName`)
- Paginierung: cursorbasiert für Benutzerlisten (`cursor`/`nextCursor`), `startIndex` und `count` für Gruppen
- PATCH für Teilaktualisierungen (einschließlich Deaktivierung über `active=false`)
- Zuordnung von Gruppen zu Rollen, aufgelöst bei der Token-Ausstellung

**Nicht unterstützt:** Massenoperationen, Sortierung, ETags, Passwortverwaltung über SCIM.

Alle Ressourcen sind auf den SCIM-Client beschränkt, der sie bereitgestellt hat: Ein von einem SCIM-Token-Client erstellter Benutzer oder eine Gruppe ist für jeden anderen SCIM-Client unsichtbar (404).

## SCIM-Token generieren

SCIM-Endpunkte werden mit statischen Bearer-Tokens authentifiziert. Generieren Sie Tokens über die Admin-API:

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365
}
```

Die Antwort enthält das rohe Token **einmalig**. Es wird als SHA-256-Hash gespeichert und kann später nicht wiederhergestellt werden, bewahren Sie es daher sicher auf:

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z"
}
```

Lassen Sie `expiresInDays` weg (oder übergeben Sie `0`) für ein nicht ablaufendes Token.

### Tokens auflisten

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### Token widerrufen

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## Identitätsanbieter konfigurieren

### Tenant-URL

```
https://your-authagonal-instance/scim/v2
```

### Authentifizierung

Verwenden Sie **OAuth Bearer Token** mit dem oben generierten Token.

### Microsoft Entra ID

1. Gehen Sie im Azure-Portal zu **Enterprise Applications** > Ihre App > **Provisioning**
2. Setzen Sie den Provisioning-Modus auf **Automatic**
3. Geben Sie die Tenant-URL ein: `https://your-instance/scim/v2`
4. Geben Sie das Secret Token ein: das im Generierungsschritt erhaltene rohe Token
5. Klicken Sie auf **Test Connection**, um die Verbindung zu prüfen
6. Konfigurieren Sie die Attributzuordnungen (siehe unten)

### Okta

1. Gehen Sie in der Okta-Admin-Konsole zu **Applications** > Ihre App > **Provisioning**
2. Aktivieren Sie den **SCIM-Connector**
3. Setzen Sie die Base-URL: `https://your-instance/scim/v2`
4. Setzen Sie den Authentifizierungsmodus: **HTTP Header**
5. Geben Sie das Bearer-Token ein

### OneLogin

1. Gehen Sie in der OneLogin-Verwaltung zu **Applications** > Ihre App > **Provisioning**
2. Aktivieren Sie die Bereitstellung
3. Setzen Sie die SCIM-Base-URL: `https://your-instance/scim/v2`
4. Setzen Sie das SCIM-Bearer-Token

## SCIM-Endpunkte

| Methode | Pfad | Beschreibung |
|--------|------|-------------|
| GET | `/scim/v2/Users` | Benutzer auflisten/filtern |
| GET | `/scim/v2/Users/{id}` | Benutzer abrufen |
| POST | `/scim/v2/Users` | Benutzer erstellen |
| PUT | `/scim/v2/Users/{id}` | Benutzer ersetzen |
| PATCH | `/scim/v2/Users/{id}` | Teilaktualisierung |
| DELETE | `/scim/v2/Users/{id}` | Soft-deaktivieren |
| GET | `/scim/v2/Groups` | Gruppen auflisten/filtern |
| GET | `/scim/v2/Groups/{id}` | Gruppe abrufen |
| POST | `/scim/v2/Groups` | Gruppe erstellen |
| PUT | `/scim/v2/Groups/{id}` | Gruppe ersetzen |
| PATCH | `/scim/v2/Groups/{id}` | Mitglieder hinzufügen/entfernen |
| DELETE | `/scim/v2/Groups/{id}` | Gruppe löschen |
| GET | `/scim/v2/ServiceProviderConfig` | Funktionsumfang |
| GET | `/scim/v2/Schemas` | Schemadefinitionen |
| GET | `/scim/v2/ResourceTypes` | Ressourcentypen |

Jeder Endpunkt ist zusätzlich ohne das Segment `/v2` verfügbar (z. B. `/scim/Users`), für Identitätsanbieter, die ihren eigenen Pfad anhängen. Die Discovery-Endpunkte (`ServiceProviderConfig`, `Schemas`, `ResourceTypes` sowie die reinen Basis-URLs `/scim/` und `/scim/v2/`, die die ServiceProviderConfig zurückgeben) sind anonym zugänglich; alle anderen erfordern ein SCIM-Bearer-Token.

Benutzer-Endpunkte sind auf 200 Anfragen pro Minute pro SCIM-Client ratenbegrenzt; überzählige Anfragen erhalten einen SCIM-Fehler mit Status `429`.

## Attributzuordnung

### Benutzerattribute

| SCIM-Attribut | Authagonal-Feld |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage` (mit Rückfall auf `locale`) | `Locale` |

### Gruppenattribute

| SCIM-Attribut | Authagonal-Feld |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

## Verhaltensdetails

### Benutzererstellung
- SCIM-bereitgestellte Benutzer werden mit `EmailConfirmed = true` erstellt (nur SSO, kein Passwort).
- Das Feld `ScimProvisionedByClientId` verfolgt, welcher SCIM-Client den Benutzer erstellt hat.
- Wenn für den Client `ProvisioningApps` konfiguriert ist, wird die TCC-Bereitstellung automatisch ausgelöst. Lehnt die Bereitstellung den Benutzer ab, wird die SCIM-Erstellung mit einer `422`-Antwort zurückgerollt.
- Das Erstellen eines Benutzers, dessen `userName` oder `externalId` bereits existiert, liefert einen SCIM-Konflikt `409`. E-Mail-Änderungen über PUT oder PATCH werden auf dieselbe Weise auf Konflikte geprüft.

### Benutzerdeaktivierung
- `DELETE /scim/v2/Users/{id}` führt ein **Soft Delete** durch, indem `IsActive = false` gesetzt wird. Der Benutzerdatensatz bleibt erhalten: Ein nachfolgendes `GET /scim/v2/Users/{id}` liefert ihn weiterhin (mit `active: false`) statt eines 404.
- `PATCH` mit `active = false` deaktiviert den Benutzer ebenfalls.
- Deaktivierte Benutzer können sich weder per Passwort noch per SAML oder OIDC anmelden.
- Alle Grants (Refresh Tokens, Sitzungen) werden bei der Deaktivierung widerrufen.
- Die Deprovisionierung nachgelagerter Anwendungen wird nur durch `DELETE` ausgelöst; eine Deaktivierung per `PATCH` widerruft Grants, lässt nachgelagerte Anwendungen aber unangetastet.

### Filterung
Unterstützte Filterausdrücke:
- `userName eq "user@example.com"`
- `externalId eq "12345"`
- `displayName co "John"`

Es werden nur Filter mit einem einzelnen Attribut unterstützt. Komplexe boolesche Ausdrücke (`and`, `or`) werden nicht unterstützt.

`eq`-Filter auf `userName` und `externalId` (die Lookups, die Entra und Okta vor jedem Erstellen oder Aktualisieren ausführen) werden über indizierte Punkt-Lookups statt über einen Listenscan aufgelöst und bleiben daher bei jeder Benutzeranzahl schnell. Andere Filter (`co`, oder Filter auf `displayName`) werden beim Durchblättern der Benutzer des Clients angewendet.

### Paginierung
Benutzerlisten verwenden **Cursor-Paginierung**. Jede Seite von `GET /scim/v2/Users` liefert eine Eigenschaft `nextCursor` in der Listenantwort zurück; übergeben Sie diese als `?cursor=`, um die nächste Seite abzurufen. Fehlt `nextCursor`, ist die Liste vollständig. Die Seitengröße wird über `count` gesteuert (Standard 100, Maximum 200).

Eine Anfrage mit `startIndex` größer als 1 am Users-Endpunkt liefert einen `400`-Fehler, der auf die Cursor-Paginierung verweist; Offset-Paginierung über die erste Seite hinaus wird nicht angeboten. `totalResults` gibt die Anzahl der in der Antwort zurückgegebenen Ressourcen an (nur dann die tatsächliche Gesamtzahl, wenn `nextCursor` fehlt).

Gruppenlisten verwenden weiterhin die Offset-Paginierung über `startIndex`/`count`.

### Gruppenmitgliedschaft über PATCH
`PATCH /scim/v2/Groups/{id}` akzeptiert die Mitgliedschaftsformen, die die wichtigsten Identitätsanbieter tatsächlich senden:

- **Mitglieder hinzufügen:** `op: "add"` mit `path: "members"` und einem Werte-Array von `{ "value": "user-id" }`-Objekten. Duplikate werden ignoriert.
- **Mitglieder ersetzen:** `op: "replace"` mit `path: "members"` ersetzt die gesamte Mitgliedschaft durch das übergebene Array.
- **Bestimmtes Mitglied entfernen (Werte-Array):** `op: "remove"` mit `path: "members"` und einem Werte-Array der zu entfernenden Mitglieds-IDs (die Form, die Entra ID sendet).
- **Bestimmtes Mitglied entfernen (Pfadfilter):** `op: "remove"` mit `path: 'members[value eq "user-id"]'`, wobei die ID im Pfadfilter ohne Wert übergeben wird (die Form, die Okta zur Deprovisionierung sendet).
- **Alle Mitglieder entfernen:** `op: "remove"` mit `path: "members"` und ohne Wert leert die Gruppe.

### Gruppen-zu-Rollen-Zuordnung
Die Mitgliedschaft in einer SCIM-Gruppe kann Anwendungsrollen gewähren. Zuordnungen sind je eine Zeile pro (Gruppe, Rolle)-Paar, und eine Gruppe kann mehrere Rollen gewähren. Sie werden bei der **Token-Ausstellung** aufgelöst: Die effektiven Rollen eines Benutzers sind seine direkt zugewiesenen Rollen plus die Rollen jeder zugeordneten Gruppe, der er angehört, sodass das Hinzufügen oder Entfernen eines Gruppenmitglieds beim nächsten Token wirksam wird, ohne den Benutzerdatensatz zu berühren. Ein leerer Zuordnungsspeicher ist ein No-Op.

Zuordnungen werden über den `IScimGroupRoleMappingStore` gespeichert (implementiert von den Azure- und AWS-Speicheranbietern; andernfalls wird standardmäßig eine In-Memory-Implementierung registriert) und über die Admin-Oberfläche der Hosting-Anwendung verwaltet, nicht über die SCIM-API selbst.

Optional erhält ein Client mit aktiviertem `IncludeGroupsInTokens` zusätzlich die Anzeigenamen der SCIM-Gruppen des Benutzers als `groups`-Claim in ausgestellten Tokens.

## Bekannte Einschränkungen

- **Keine Massenoperationen:** Benutzer und Gruppen müssen einzeln bereitgestellt werden.
- **Keine Sortierung:** Benutzerlisten werden unter Cursor-Paginierung in Speicherreihenfolge zurückgegeben; Gruppenlisten sind nach Erstellungsdatum sortiert.
- **Filterteilmenge:** nur die Operatoren `eq` und `co` für `userName`, `externalId` und `displayName` (Gruppen: `displayName` und `externalId`).
- **Keine Passwortverwaltung:** SCIM-bereitgestellte Benutzer authentifizieren sich nur über SSO.
- **Nur Soft Delete:** `DELETE` deaktiviert Benutzer, statt sie dauerhaft zu entfernen.

---
layout: default
title: SAML
locale: de
---

# SAML 2.0 SP

Authagonal enthält eine eigene SAML 2.0 Service Provider-Implementierung. Keine Drittanbieter-SAML-Bibliothek -- gebaut auf `System.Security.Cryptography.Xml.SignedXml` (Teil von .NET).

## Umfang

- **SP-initiiertes SSO** (Benutzer startet bei Authagonal, wird zum IdP weitergeleitet)
- **HTTP-Redirect-Bindung** für AuthnRequest
- **HTTP-POST-Bindung** für Response (ACS)
- Azure AD ist das primäre Ziel, aber jeder konforme IdP funktioniert

### Nicht unterstützt

- SAML-Abmeldung (verwenden Sie Sitzungstimeout)
- Assertion-Verschlüsselung (kein Verschlüsselungszertifikat veröffentlichen)
- Artifact-Bindung

IdP-initiiertes SSO wird unterstützt -- der ACS-Endpunkt verarbeitet Antworten ohne `InResponseTo` (überspringt die Replay-Validierung für unaufgeforderte Antworten).

## Azure AD-Einrichtung

### 1. SAML-Anbieter erstellen

**Option A -- Konfiguration (empfohlen für statische Setups):**

Zu `appsettings.json` hinzufügen:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

Anbieter werden beim Start initialisiert. SSO-Domainzuordnungen werden automatisch aus `AllowedDomains` registriert.

**Option B -- Admin-API (für Laufzeitverwaltung):**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

### 2. Azure AD konfigurieren

1. In Azure AD -> Unternehmensanwendungen -> Neue Anwendung -> Eigene erstellen
2. Einmaliges Anmelden einrichten -> SAML
3. **Bezeichner (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **Antwort-URL (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **Anmelde-URL:** `https://auth.example.com/saml/acme-azure/login`

### 3. SSO-Domainrouting

Wenn `AllowedDomains` angegeben ist (in der Konfiguration oder über die Create-API), werden SSO-Domainzuordnungen automatisch registriert. Wenn ein Benutzer `user@acme.com` auf der Login-Seite eingibt, erkennt die SPA, dass SSO erforderlich ist, und zeigt "Weiter mit SSO" an.

Sie können Domaenen auch zur Laufzeit über die Admin-API verwalten -- siehe [Admin-API](admin-api).

## Endpunkte

| Endpunkt | Beschreibung |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...` | Initiiert SP-initiiertes SSO. Erstellt einen AuthnRequest und leitet zum IdP weiter. |
| `POST /saml/{connectionId}/acs` | Assertion Consumer Service. Empfaengt die SAML-Antwort, validiert sie, erstellt/meldet den Benutzer an. |
| `GET /saml/{connectionId}/metadata` | SP-Metadaten-XML zur Konfiguration des IdP. |

## Azure AD-Kompatibilität

| Azure AD-Verhalten | Behandlung |
|---|---|
| Signiert nur Assertion (Standard) | Validiert Signatur auf dem Assertion-Element |
| Signiert nur Response | Validiert Signatur auf dem Response-Element |
| Signiert beides | Validiert beide Signaturen |
| SHA-256 (Standard) | Unterstützt SHA-256 und SHA-1 |
| NameID: emailAddress | Direkte E-Mail-Extraktion |
| NameID: persistent (undurchsichtig) | Fällt auf E-Mail-Claim aus Attributen zurück |
| NameID: transient, unspecified | Fällt auf E-Mail-Claim aus Attributen zurück |

## Claim-Zuordnung

Azure AD-Claims (vollständiges URI-Format) werden auf einfache Namen abgebildet:

| Azure AD Claim-URI | Zugeordnet zu |
|---|---|
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` | `email` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname` | `firstName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname` | `lastName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name` | `name` (UPN) |
| `http://schemas.microsoft.com/identity/claims/objectidentifier` | `oid` |
| `http://schemas.microsoft.com/identity/claims/displayname` | `displayName` |

## Sicherheit

- **Wiederholungsschutz:** InResponseTo wird gegen eine gespeicherte Anfrage-ID validiert. Jede ID ist einmalig verwendbar.
- **Taktabweichung:** 5-Minuten-Toleranz bei NotBefore/NotOnOrAfter
- **Wrapping-Angriff-Schutz:** Signaturvalidierung verwendet die korrekte Referenzauflösung
- **Offene Weiterleitungs-Schutz:** RelayState (returnUrl) muss ein wurzel-relativer Pfad sein (beginnt mit `/`, ohne Schema oder Host)

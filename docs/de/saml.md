---
layout: default
title: SAML
locale: de
---

# SAML 2.0 SP

Authagonal enthält eine selbst entwickelte SAML 2.0 Service Provider-Implementierung. Keine SAML-Bibliothek von Drittanbietern: aufgebaut auf `System.Security.Cryptography.Xml.SignedXml` (Teil von .NET).

## Umfang

- **SP-initiiertes SSO** (Benutzer beginnt bei Authagonal, wird zum IdP weitergeleitet)
- **HTTP-Redirect-Bindung** für AuthnRequest (optional signiert, siehe unten)
- **HTTP-POST-Bindung** für Response (ACS)
- **Verschlüsselte Assertions** (`EncryptedAssertion`), entschlüsselt mit einem verbindungsspezifischen SP-Schlüsselpaar
- **Single Logout** (SP-initiiert und IdP-initiiert, Redirect- und POST-Bindung)
- Azure AD / Entra ID ist das primäre Ziel, aber jeder konforme IdP funktioniert (Okta, OneLogin, Ping, Google Workspace, ADFS, Shibboleth-Attributnamen werden unterstützt)

### Nicht unterstützt

- Artifact-Bindung
- AES-GCM-Assertion-Verschlüsselung (Einschränkung von .NET `EncryptedXml`; konfigurieren Sie AES-CBC beim IdP, siehe unten)

**IdP-initiierte Anmeldung funktioniert, und die Kachel muss nicht umkonfiguriert werden** — aber die unaufgeforderte Assertion ist nicht das, was den Benutzer anmeldet. Eine Response ohne `InResponseTo` wird verworfen, und der ACS leitet den Browser auf `/saml/{connectionId}/login` um, wo ein frischer, an diesen Browser gebundener AuthnRequest ausgestellt wird. Der Benutzer ist beim IdP bereits authentifiziert, also antwortet dieses sofort und der Umweg bleibt unsichtbar; das `RelayState` des IdP wird als Rücksprung-URL mitgeführt, sodass der Benutzer weiterhin auf dem Deeplink landet, für den die Kachel konfiguriert war.

Die Assertion muss verworfen werden, weil das Akzeptieren einer unaufgeforderten Assertion jedem mit einem Konto bei diesem IdP erlaubt, in einem beliebigen User-Agent eine Sitzung anzumelden — jede Regel aus §4.1.4.3 wird von einer Assertion erfüllt, die der Angreifer für sein EIGENES Konto rechtmäßig erhalten hat — und weil das Request-Cookie auf dem SP-initiierten Pfad nichts wert ist, solange dieselbe Assertion ohne `InResponseTo` erneut eingespielt werden kann. Der Neustart des Flows hält die Kachel funktionsfähig, ohne davon etwas zu akzeptieren: Wer am Ende angemeldet ist, ist derjenige, den das IdP im NEUEN Austausch nennt.

Der Neustart erfolgt einmal pro Browser. Ein IdP, das den AuthnRequest mit einer weiteren unaufgeforderten Response beantwortet, wird mit `error=saml_unsolicited` abgelehnt statt erneut umgeleitet, sodass ein falsch konfiguriertes IdP keine Weiterleitungsschleife erzeugen kann.

Um die unaufgeforderte Assertion stattdessen unverändert zu akzeptieren, setzen Sie `allowUnsolicitedResponses: true` auf der Verbindung — **standardmäßig aus**. Ist die Option aktiv, wird die Prüfung der Anfrage-ID bei unaufgeforderten Antworten übersprungen, die Einmaligkeit der Assertion-ID aber weiterhin durchgesetzt (siehe Sicherheit).

## Azure AD-Einrichtung

### 1. SAML-Anbieter erstellen

**Option A: Konfiguration (empfohlen für statische Setups)**

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

Anbieter werden beim Start initialisiert. SSO-Domainzuordnungen werden automatisch aus `AllowedDomains` registriert. Über die Konfiguration bereitgestellte Anbieter benötigen eine `MetadataLocation`-URL und erhalten kein SP-Schlüsselpaar (also keine signierten AuthnRequests, verschlüsselten Assertions oder signierten Abmeldenachrichten); verwenden Sie für diese Funktionen die Admin-API.

`EntityId` ist **Ihre SP-Entity-ID** (die Kennung, die Sie beim IdP registrieren), nicht die Entity-ID des IdP.

**Option B: Admin-API (für Laufzeitverwaltung)**

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

Die API generiert die `connectionId` (eine GUID) und gibt sie im `Location`-Header sowie im Antworttext zurück. Zusätzliche optionale Felder: `metadataXml` (eingefügte Metadaten, siehe unten), `nameIdFormat` (siehe unten), `signAuthnRequests` (signierte AuthnRequests erzwingen), `iconUrl` (Symbol für die Login-Schaltfläche), `disableJitProvisioning` (unbekannte Benutzer ablehnen, statt sie automatisch anzulegen), `allowUnsolicitedResponses` (eine IdP-initiierte Assertion unverändert akzeptieren, statt den Flow neu zu starten — standardmäßig aus, siehe oben). Über die API erstellte Verbindungen erhalten außerdem automatisch ein generiertes SP-Schlüsselpaar (siehe SP-Schlüsselpaar unten).

Verbindungen werden über `POST` / `GET` / `PUT` / `DELETE` auf `/api/v1/saml/connections[/{connectionId}]` verwaltet. `PUT` ist eine Teilaktualisierung: Es werden nur die Felder geändert, die tatsächlich übermittelt wurden.

### 2. Azure AD konfigurieren

1. In Azure AD → Unternehmensanwendungen → Neue Anwendung → Eigene erstellen
2. Einmaliges Anmelden einrichten → SAML
3. **Bezeichner (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **Antwort-URL (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **Anmelde-URL:** `https://auth.example.com/saml/acme-azure/login`

### 3. SSO-Domainrouting

Wenn `AllowedDomains` angegeben ist (in der Konfiguration oder über die Create-API), werden SSO-Domainzuordnungen automatisch registriert. Wenn ein Benutzer `user@acme.com` auf der Login-Seite eingibt, erkennt die SPA, dass SSO erforderlich ist, und zeigt "Weiter mit SSO" an. Eine Domain kann nur einer einzigen Verbindung zugeordnet werden; die API lehnt eine Domain ab, die bereits einer anderen Verbindung zugeordnet ist.

Sie können Domänen auch zur Laufzeit über die Admin-API verwalten; siehe [Admin-API](admin-api).

## Eingefügtes Metadaten-XML

Manche IdPs veröffentlichen keine Metadaten-URL (Google Workspace), oder ihr Metadaten-Endpunkt ist vom SP aus nicht erreichbar (ADFS in einem privaten Netzwerk). Fügen Sie in diesen Fällen stattdessen das Metadatendokument direkt ein: Übergeben Sie `metadataXml` bei der Erstellung/Aktualisierung. Es muss genau eines von `metadataLocation` oder `metadataXml` angegeben werden; wird bei einer Aktualisierung eines der beiden übergeben, wird das jeweils andere gelöscht.

Eingefügte Metadaten werden beim Speichern validiert und auf einen kanonischen, minimalen `EntityDescriptor` **verdichtet** (`SamlMetadataParser.Condense`), der genau das enthält, was der SP verwendet: entityID, Signaturzertifikate, den SSO-Endpunkt, den SLO-Endpunkt (falls vorhanden) und das `WantAuthnRequestsSigned`-Flag. Anbieterdokumente können 100 KB überschreiten (ADFS-`FederationMetadata.xml`), was über der 64-KB-Grenze für Azure-Table-Eigenschaften liegt, während die vom SP tatsächlich genutzten Teile nur wenige KB umfassen. Nicht parsbare Einfügungen werden mit einem 400er-Fehler abgelehnt; das Dokument muss einen `IDPSSODescriptor` mit einem Signaturzertifikat und einem `SingleSignOnService` enthalten.

## NameID-Format

Das Feld `nameIdFormat` steuert das im AuthnRequest angeforderte `NameIDPolicy`-Format:

| Wert | Verhalten |
|---|---|
| weggelassen / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (der historische Standard) |
| `"none"` | Das `NameIDPolicy`-Element vollständig weglassen. Die ADFS-sichere Einstellung: ADFS bricht die gesamte Anmeldung ab (MSIS7070), wenn seine Claim-Regeln das angeforderte Format nicht ausgeben. |
| jeder andere Wert | Wird unverändert als Format-URN gesendet (muss mit `urn:` beginnen) |

Bei einer Aktualisierung setzt `""` das Format auf den emailAddress-Standard zurück. Die SP-Metadaten geben das für die Verbindung angeforderte Format bekannt (und lassen `NameIDFormat` weg, wenn es auf `"none"` gesetzt ist).

## Endpunkte

| Endpunkt | Beschreibung |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | Initiiert SP-initiiertes SSO. Erstellt einen AuthnRequest (signiert, sofern zutreffend) und leitet zum IdP weiter. `loginHint` wird als `login_hint` an IdPs übergeben, die dies berücksichtigen (Entra, Google). |
| `POST /saml/{connectionId}/acs` | Assertion Consumer Service. Empfängt die SAML-Antwort, validiert sie, erstellt den Benutzer bzw. meldet ihn an. |
| `GET /saml/{connectionId}/metadata` | SP-Metadaten-XML zur Konfiguration des IdP. |
| `GET /saml/{connectionId}/logout?returnUrl=...` | SP-initiierter Single Logout. Beendet zunächst die lokale Sitzung und sendet anschließend ein LogoutRequest an den IdP, sofern dieser SLO unterstützt. |
| `GET/POST /saml/{connectionId}/slo` | Single-Logout-Endpunkt. Empfängt IdP-initiierte LogoutRequests (Redirect- oder POST-Bindung) sowie den LogoutResponse-Teil eines SP-initiierten SLO. |

Die Rückkehr-URL nach der Anmeldung wird serverseitig am gespeicherten AuthnRequest mitgeführt (indiziert über die Anfrage-ID) und nicht in RelayState: Die SAML-Spezifikation begrenzt RelayState auf 80 Bytes, und manche IdPs kürzen es. RelayState wird nur bei IdP-initiierten Abläufen herangezogen.

## SP-Schlüsselpaar & verschlüsselte Assertions

Jede über die API erstellte Verbindung erhält ein automatisch generiertes SP-Schlüsselpaar: ein selbstsigniertes 2048-Bit-RSA-Zertifikat (Gültigkeit 10 Jahre), gespeichert als PKCS#12 und im Ruhezustand durch den Secret-Provider des Hosts geschützt. Es ist ausschließlich serverseitig und wird von der API niemals zurückgegeben. Das Schlüsselpaar ermöglicht:

- **Signierte AuthnRequests** (Signierung der `SigAlg`/`Signature`-Query-Parameter bei der Redirect-Bindung). Die Signierung wird automatisch aktiviert, wenn die Metadaten des IdP `WantAuthnRequestsSigned` deklarieren, oder immer, wenn die Verbindung `signAuthnRequests: true` gesetzt hat.
- **Entschlüsselung verschlüsselter Assertions.** Sobald die SP-Metadaten ein Verschlüsselungszertifikat bekannt geben, beginnt ADFS standardmäßig, Assertions zu verschlüsseln; der ACS entschlüsselt sie mit dem privaten SP-Schlüssel und führt die entschlüsselte Assertion durch dieselbe Signatur-/Bedingungsprüfung wie eine unverschlüsselte. Unterstützt werden: RSA-OAEP (SHA-1/SHA-256) für den Schlüsseltransport; AES-128/192/256-CBC und 3DES für die Datenverschlüsselung. **RSA-1.5 für den Schlüsseltransport wird abgelehnt** — PKCS#1-v1.5-Entpacken ist ein Bleichenbacher/ROBOT-Orakel — und **AES-GCM wird nicht unterstützt** (Einschränkung von .NET `EncryptedXml`). Konfigurieren Sie den IdP auf RSA-OAEP und AES-CBC. Beide Fehler liefern absichtlich dieselbe konstante Meldung („Could not decrypt the assertion.“): den Algorithmus oder die fehlgeschlagene Stufe zu nennen, ist genau das, was das Orakel erzeugt — diagnostizieren Sie daher über die IdP-Konfiguration, nicht über die Fehlermeldung.
- **Signierte Abmeldenachrichten** (LogoutRequest/LogoutResponse bei der Redirect-Bindung).

Die SP-Metadaten veröffentlichen das Zertifikat sowohl als `signing`- als auch als `encryption`-`KeyDescriptor` und setzen `AuthnRequestsSigned="true"`, wenn die Verbindung die Signierung erzwingt.

## Single Logout

Der ACS speichert die SAML-Sitzung im Auth-Cookie (Claims `saml_connection`, `saml_name_id`, `saml_name_id_format`, `saml_session_index`), damit die Abmeldung mit der IdP-Sitzung verknüpft werden kann.

- **SP-initiiert:** `GET /saml/{connectionId}/logout` beendet immer zuerst die lokale Cookie-Sitzung (der Benutzer hat die Abmeldung angefordert; die IdP-SLO erfolgt nach bestem Bemühen). Stammt die Browser-Sitzung von dieser Verbindung und geben die IdP-Metadaten einen `SingleLogoutService` bekannt, wird ein LogoutRequest (NameID + SessionIndex, signiert, sofern der SP ein Schlüsselpaar besitzt) über die Redirect-Bindung gesendet; die LogoutResponse des IdP kommt an `/slo` zurück, wodurch der Benutzer auf der gespeicherten `returnUrl` landet. Bei IdPs ohne SLO-Endpunkt (Google) erfolgt lediglich die lokale Abmeldung.
- **IdP-initiiert:** Der IdP sendet ein LogoutRequest an `/saml/{connectionId}/slo` (Redirect-GET- oder POST-Bindung). Signierte Anfragen werden gegen die Zertifikate in den IdP-Metadaten validiert. **Ein unsigniertes oder nicht verifizierbares LogoutRequest wird mit 400 abgelehnt**, bevor überhaupt eine Sitzung herangezogen wird. Es gibt keinen sitzungsbezogenen Rückfallpfad: eine fremde Seite, die den Browser des *Opfers* hierher navigiert, liefert die Sitzung des Opfers, nicht die des Angreifers — eine Begrenzung auf die aktuelle Sitzung hätte also nicht eingeschränkt, wen man abmelden kann. Profiles §4.4.3.1 verlangt vom IdP ohnehin, ein LogoutRequest auf der Redirect- oder POST-Bindung zu signieren, und die Metadaten der Verbindung liefern die Zertifikate bereits, sodass die Ablehnung keinen konformen IdP etwas kostet. Eine signierte LogoutResponse wird zurückgegeben, wenn der IdP über einen SLO-Endpunkt verfügt. Nur Front-Channel: Die Nachricht trifft im Browser des Benutzers ein, sodass das Beenden der Cookie-Sitzung genau diesen Browser abmeldet.

## Metadaten-Caching & Zertifikatswechsel

- IdP-Metadaten, die von `MetadataLocation` abgerufen werden, werden 60 Minuten lang im Arbeitsspeicher zwischengespeichert (konfigurierbar über `Cache:SamlMetadataCacheMinutes`), indiziert über die Metadaten-URL (nicht die Connection-ID, sodass keine mandantenübergreifende Cache-Verwechslung möglich ist).
- Eingefügte Metadaten werden inhaltsadressiert zwischengespeichert (Hash des XML) und niemals erneut abgerufen.
- **Erneuter Abruf bei Signaturfehler:** Ein Signaturvalidierungsfehler unmittelbar nach einem Zertifikatswechsel beim IdP bedeutet, dass die zwischengespeicherten Metadaten veraltet sind. Bei genau diesem Fehler wird der Cache-Eintrag verworfen und die Metadaten einmalig erneut abgerufen, anschließend wird die Validierung wiederholt, mit einer 5-Minuten-Sperrfrist je Metadaten-Standort, damit eine unbrauchbare Assertion nicht dazu missbraucht werden kann, den Metadaten-Endpunkt des IdP zu überlasten. Ohne dies würde ein Zertifikatswechsel Anmeldungen so lange scheitern lassen, bis die Cache-TTL abgelaufen ist. (Nur bei über eine URL abgerufenen Metadaten; eingefügte Metadaten haben nichts, was erneut abgerufen werden könnte.)

## Azure AD-Kompatibilität

| Azure AD-Verhalten | Behandlung |
|---|---|
| Signiert nur Assertion (Standard) | Validiert Signatur auf dem Assertion-Element |
| Signiert nur Response | Validiert Signatur auf dem Response-Element |
| Signiert beides | Validiert beide Signaturen |
| SHA-256 (Standard) | Unterstützt SHA-256 und SHA-1 |
| NameID: emailAddress | Direkte E-Mail-Extraktion |
| NameID: persistent (undurchsichtig) | Fällt auf E-Mail-Claim aus Attributen zurück |
| NameID: unspecified | Fällt auf E-Mail-Claim aus Attributen zurück |
| NameID: transient | Rotiert bei jeder Anmeldung und wird deshalb niemals als föderierter Schlüssel verwendet. Stattdessen wird das stabile Objekt-ID-Attribut des IdP verwendet; wird keines übermittelt, wird die Anmeldung mit einer klaren, umsetzbaren Fehlermeldung abgelehnt (konfigurieren Sie ein persistentes oder ein emailAddress-NameID, oder übermitteln Sie ein Objekt-ID-Attribut). |

## Attribut-Zuordnung

Attribute werden ohne Berücksichtigung der Groß-/Kleinschreibung sowohl unter ihrem `Name` als auch ihrem `FriendlyName` indiziert (Okta und Shibboleth geben OID-Names mit menschenlesbaren FriendlyNames aus; erst der Abgleich gegen beide macht die Zuordnung bei verschiedenen Anbietern möglich). Jedes Feld probiert der Reihe nach eine Aliasliste durch; der erste Alias ist die Microsoft-Claim-URI, sodass sich das Verhalten für Entra/ADFS nicht ändert, und die übrigen decken die Friendly- und OID-Namen ab, die Okta, OneLogin, Ping, Google und Shibboleth standardmäßig ausgeben:

| Feld | Akzeptierte Attributnamen |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` steht abkürzend für die vollständige URI `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` oder `http://schemas.microsoft.com/ws/2008/06/identity/claims/...`.)

Reihenfolge der E-Mail-Auflösung: explizites E-Mail-Attribut (jeder Alias) → NameID, wenn dessen Format emailAddress ist → der `name`-Claim, sofern er ein `@` enthält → Ablehnung (eine E-Mail-Adresse ist erforderlich).

**Gruppen sind mehrwertig:** Es wird jedes `AttributeValue`-Element erfasst (eines pro Gruppenmitgliedschaft), nicht nur das erste.

## JIT-Bereitstellung

Unbekannte Benutzer werden bei der ersten Anmeldung automatisch angelegt (E-Mail, Vor-/Nachname aus der Assertion, E-Mail als bestätigt markiert) und über ihre stabile föderierte Identität mit der Verbindung verknüpft (`saml:{connectionId}` + NameID, oder die Objekt-ID bei transienten NameIDs). Setzen Sie `disableJitProvisioning: true`, um unbekannte Benutzer stattdessen abzulehnen. Wiederkehrende Benutzer werden zuerst über die föderierte Verknüpfung abgeglichen, niemals allein über die E-Mail-Adresse; ein bestehendes lokales Konto wird nur dann über die E-Mail-Adresse verknüpft, wenn die `AllowedDomains` der Verbindung die Domain dieser E-Mail-Adresse abdecken (die explizite Aussage des Administrators, dass dieser IdP die Domain besitzt), was eine Kontoübernahme durch einen betrügerischen IdP verhindert.

## Sicherheit

- **Wiederholungsschutz:** Bei SP-initiierten Abläufen wird `InResponseTo` gegen eine gespeicherte Anfrage-ID validiert (einmalig verwendbar). Unabhängig davon wird die ID jeder akzeptierten Assertion gespeichert und deren einmalige Verwendung erzwungen, was auch IdP-initiierte Antworten sowie Antworten abdeckt, deren `InResponseTo` entfernt wurde (die Assertion-ID befindet sich innerhalb der signierten Assertion und kann daher nicht verändert werden, ohne die Signatur zu brechen).
- **Taktabweichung:** 5-Minuten-Toleranz bei NotBefore/NotOnOrAfter
- **Schutz vor Wrapping-Angriffen:** Die Reference-URI der Signatur muss mit der ID des signierten Elements übereinstimmen
- **Schutz vor offener Weiterleitung:** Die Rückkehr-URL nach der Anmeldung muss ein wurzel-relativer Pfad sein (beginnt mit `/`, kein `//`, keine Backslashes, da Browser `\` wie `/` behandeln)
- **Domain-Bürgschaft:** Wenn `AllowedDomains` konfiguriert ist, werden Assertions für E-Mail-Adressen außerhalb dieser Domains abgelehnt, sodass eine Verbindung nicht die Domain einer anderen Verbindung oder die E-Mail-Adresse eines lokalen Benutzers behaupten kann
- **MFA:** Die Föderation belegt nur den ersten Faktor. Erfordert die für den Benutzer geltende Richtlinie MFA, wird die Anmeldung stattdessen über die lokale MFA-Abfrage/-Einrichtung geleitet, statt direkt eine vollständig authentifizierte Sitzung auszustellen.
</content>

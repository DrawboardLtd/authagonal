---
layout: default
title: Pushed Authorization Requests
locale: de
---

# Pushed Authorization Requests (PAR)

[RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) erlaubt es einem Client, seine Autorisierungsparameter mit standardmäßiger Client-Authentifizierung direkt per POST an den Server zu senden und dafür einen kurzlebigen, opaken `request_uri` zu erhalten, den er an den Browser weitergibt. Der Browser ruft anschließend `/connect/authorize?request_uri=...&client_id=...` auf, anstatt sämtliche Parameter in der URL mitzuführen.

Warum PAR nutzen:

- Autorisierungsparameter erscheinen niemals im Browserverlauf, in Server-Logs oder in `Referer`-Headern.
- Der Server authentifiziert den Client bereits beim Push, sodass die Parameter auf Integrität geprüft werden, bevor überhaupt eine Weiterleitung stattfindet.
- Umfangreiche Parametermengen (große `claims`-Anfragen, Multi-Resource-Abläufe) sprengen keine URL-Längenbegrenzungen.

## Endpunkt

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

Die Authentifizierung erfolgt wie bei `/connect/token`: HTTP Basic mit `client_id`/`client_secret` oder formularkodierten Anmeldedaten. Vertrauliche (confidential) Clients müssen sich authentifizieren, öffentliche Clients senden ohne Secret. Fehler bei der Client-Authentifizierung liefern `401` (gemäß RFC 9126, anders als beim Token-Endpunkt, wo nur `invalid_client` einen 401 auslöst).

Der Formularkörper enthält dieselben Parameter, die normalerweise an `/connect/authorize` übergeben werden (`response_type`, `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method`, `nonce`, `resource` usw.). `request_uri` selbst wird abgelehnt: Das Verketten mehrerer PAR-Aufrufe ist gemäß §2.1 der Spezifikation untersagt. Enthält der Body ein `client_id`, muss es mit dem authentifizierten Client übereinstimmen.

### Antwort

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

Der `request_uri` ist nur einmal verwendbar. Er wird aus dem Store entfernt, sobald die passende `/connect/authorize`-Anfrage ihn verbraucht (oder sobald das 90-Sekunden-Fenster abläuft, je nachdem, was zuerst eintritt).

### Autorisierungsschritt

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

Ist `request_uri` vorhanden, werden alle anderen Parameter aus der gepushten Payload übernommen: Alles Übrige in der URL wird ignoriert. Das `client_id` dieser Anfrage muss mit dem Client übereinstimmen, der die Payload gepusht hat.

## PAR pro Client erzwingen

Setzen Sie `RequirePushedAuthorizationRequests = true` bei einem Client, um einfache `/connect/authorize`-Anfragen von ihm abzulehnen. Jeder Autorisierungsversuch ohne PAR liefert `invalid_request` mit der Beschreibung "This client requires requests to be pushed via /connect/par".

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

Dies ist die empfohlene Haltung für Clients, die sensible Scopes verarbeiten: In Kombination mit PKCE entfällt die URL-Leiste als Angriffsfläche.

## Lebensdauer und Speicherung

Die Lebensdauer des `request_uri` ist serverseitig auf 90 Sekunden festgelegt, was dem typischen Wert eines Referenz-IdP entspricht. Gepushte Payloads werden über denselben `IGrantStore` gespeichert wie Auth-Codes und Refresh Tokens, sodass sie automatisch die Persistenz- und Replikationsstrategie des Hosts übernehmen.

## Discovery

Der PAR-Endpunkt kündigt sich in `.well-known/openid-configuration` wie folgt an:

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```

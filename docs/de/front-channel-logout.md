---
layout: default
title: Front-Channel Logout
locale: de
---

# Front-Channel Logout

Authagonal implementiert **OpenID Connect Front-Channel Logout 1.0**, einen browsergesteuerten Logout-Mechanismus, der das [Back-Channel Logout](index#features) ergänzt. Während Back-Channel Logout ein Server-zu-Server-POST ist, rendert Front-Channel Logout die Logout-URL jeder Relying Party in einem versteckten iframe, sodass die Browsersitzung jeder App (Cookies, lokaler Speicher) direkt im Browser des Benutzers bereinigt wird.

## Wann welches Verfahren verwenden

| Aspekt | Back-Channel | Front-Channel |
|---|---|---|
| Serverseitige Sitzungen | ✅ | ❌ |
| Browser-Cookies / lokaler Speicher | ❌ | ✅ |
| Funktioniert, wenn der Browser des Benutzers offline ist | ✅ | ❌ |
| Übersteht Netzwerkfehler (Wiederholung) | ✅ | ❌ (einmaliger Best-Effort-Versuch) |

Die meisten Anwendungen profitieren davon, **beide** zu konfigurieren. Back-Channel garantiert, dass der Server informiert wird; Front-Channel bereinigt den Browser.

## Client-Konfiguration

Fügen Sie dem `OAuthClient`-Datensatz eine Front-Channel-Logout-URI hinzu:

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| Feld | Beschreibung |
|---|---|
| `FrontChannelLogoutUri` | Der im Browser des Clients sichtbare Logout-Endpunkt |
| `FrontChannelLogoutSessionRequired` | Wenn `true` (Standard), wird die URL mit den Query-Parametern `iss` und `sid` aufgerufen, damit der Client den Logout der jeweiligen Sitzung zuordnen kann |

## Funktionsweise

Wenn der Browser `/connect/endsession` aufruft:

1. Der Server ermittelt alle Clients, für die der Benutzer aktuell Grants besitzt.
2. Für jeden Client mit einer `FrontChannelLogoutUri` erstellt der Server eine URL: Er hängt `iss=<issuer>` an (sowie `sid=<session_id>`, sofern die Sitzung eine hat), wenn `FrontChannelLogoutSessionRequired` den Wert `true` hat.
3. Der Server meldet den Benutzer beim Cookie des Autorisierungsservers ab, löst im Hintergrund Back-Channel-Logout-Benachrichtigungen aus und liefert eine HTML-Seite mit einem versteckten `<iframe>` für jede Client-Logout-URL:
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
4. Nach einer Karenzzeit von 2 Sekunden wird der Browser zur `post_logout_redirect_uri` weitergeleitet: Dies geschieht nur, wenn die Anfrage zusätzlich einen `id_token_hint` enthält, der den Client identifiziert, und die URI in den registrierten `PostLogoutRedirectUris` dieses Clients enthalten ist (ein `state`-Parameter wird, sofern angegeben, an die Weiterleitung angehängt). Andernfalls wird eine Bestätigung „Abgemeldet" angezeigt.

## Client-seitiger Logout-Handler

Jede Relying Party sollte die von `FrontChannelLogoutUri` referenzierte URL implementieren. Ein minimaler Handler:

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. Prüfen Sie, ob `iss` mit dem erwarteten Autorisierungsserver übereinstimmt.
2. Falls `sid` angegeben ist, bestätigen Sie, dass sie mit der Sitzungs-ID des Sitzungscookies übereinstimmt.
3. Löschen Sie die lokale Sitzung (Cookies, serverseitige Sitzung, SPA-Speicher).
4. Antworten Sie mit `200 OK` und einem leeren Body (oder einer winzigen Seite): Die Antwort ist für den Benutzer ohnehin nie sichtbar.

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // iss/sid validieren, dann lokale Sitzung löschen
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## Discovery-Dokument

Front-Channel Logout wird in `/.well-known/openid-configuration` angekündigt:

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## Dynamische Client-Registrierung

Über [Dynamische Client-Registrierung](client-registration) registrierte Clients können Folgendes enthalten:

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

## Einschränkungen

- **Best Effort**: iframes werden nur einmal geladen. Blockiert ein Netzwerkfehler oder eine Browsererweiterung sie, gibt es keine Wiederholung. Kombinieren Sie es für Zuverlässigkeit mit Back-Channel Logout.
- **Cookies von Drittanbietern**: Manche Browser blockieren standardmäßig Cookies in Cross-Site-iframes. Falls Ihre Relying Party auf First-Party-Cookies angewiesen ist, stellen Sie sicher, dass der Logout-Handler nicht davon abhängt, dass Cookies gesendet werden.
- **Timeout**: Die Seite wartet ca. 2 Sekunden vor der Weiterleitung bzw. Bestätigung. Aufwendige Logout-Handler der Relying Party werden möglicherweise nicht rechtzeitig fertig.

## Verwandte Themen

- [Dynamische Client-Registrierung](client-registration): Front-Channel-Parameter in der Registrierungsanfrage
- [OAuth Scopes](scopes): Scope-bewusste Zustimmung ergänzt den Logout-Ablauf

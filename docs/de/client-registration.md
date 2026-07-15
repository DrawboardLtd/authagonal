---
layout: default
title: Dynamic Client Registration
locale: de
---

# Dynamische Client-Registrierung

Authagonal implementiert die **dynamische OAuth-2.0-Client-Registrierung** ([RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)), die es Client-Anwendungen ermöglicht, sich zur Laufzeit selbst zu registrieren, ohne dass ein Administrator eingebunden werden muss.

## Aktivieren des Endpunkts

Die dynamische Registrierung ist **standardmäßig deaktiviert**. Aktivieren Sie sie über die Konfiguration:

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

Oder setzen Sie `Auth__DynamicClientRegistrationEnabled=true` als Umgebungsvariable.

Wenn aktiviert, gibt das Discovery-Dokument den Endpunkt bekannt:

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## Einen Client registrieren

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### Antwort

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

Das `client_secret` wird **einmalig** zurückgegeben und kann später nicht erneut abgerufen werden. Bewahren Sie es sicher auf.

## Anfrageparameter

| Parameter | Erforderlich | Hinweise |
|---|---|---|
| `client_name` | nein | Standardmäßig der generierte `client_id`, falls weggelassen |
| `redirect_uris` | bedingt | Erforderlich, wenn `grant_types` `authorization_code` enthält. Muss absolute URIs sein; die Schemata `javascript:`/`data:`/`vbscript:`/`file:` werden abgelehnt (native benutzerdefinierte Schemata für mobile Deep-Links sind zulässig). |
| `post_logout_redirect_uris` | nein | Gültige Weiterleitungsziele nach der Abmeldung |
| `grant_types` | nein | Standardmäßig `["authorization_code"]`. **Nur `authorization_code` und `refresh_token` sind registrierbar**: `client_credentials`, `implicit`, Device und jeder andere Grant-Type werden mit `invalid_client_metadata` abgelehnt, sodass eine offene Registrierung niemals einen Machine-to-Machine-Client erzeugen kann. `refresh_token` wird automatisch hinzugefügt, wenn `offline_access` angefordert wird. |
| `token_endpoint_auth_method` | nein | `client_secret_basic` (Standard), `client_secret_post` oder `none` für öffentliche Clients |
| `scope` | nein | Durch Leerzeichen getrennte Scopes: müssen alle integriert oder zuvor registriert sein (siehe [Scopes](scopes)). Der administrative Scope (`AdminApi:Scope`, Standard `authagonal-admin`) kann niemals registriert werden. |
| `audiences` | nein | JWT-`aud`-Werte, die Access Tokens hinzugefügt werden |
| `allowed_cors_origins` | nein | Origins, die den Token-Endpunkt aus einem Browser heraus aufrufen dürfen |
| `backchannel_logout_uri` | nein | Aktiviert [Back-Channel-Logout](index#features) |
| `frontchannel_logout_uri` | nein | Aktiviert [Front-Channel-Logout](front-channel-logout) |
| `frontchannel_logout_session_required` | nein | Standardmäßig `true`; wenn `true`, enthält die Logout-URL die Parameter `iss` und `sid` |

## Standardwerte & Invarianten

- **PKCE erforderlich**: `RequirePkce` ist bei dynamisch registrierten Clients immer `true`.
- **Öffentliche Clients**: `token_endpoint_auth_method: "none"` erzeugt einen Client ohne Secret. PKCE bleibt trotzdem erforderlich.
- **Offline-Zugriff**: Das Anfordern des Scopes `offline_access` fügt `grant_types` implizit `refresh_token` hinzu.

## Fehlerantworten

| HTTP | `error` | Ursache |
|---|---|---|
| `400` | `invalid_redirect_uri` | Eine der `redirect_uris` ist keine gültige absolute URI oder verwendet ein Script-/Data-/File-Pseudoschema |
| `400` | `invalid_client_metadata` | Es wurde ein nicht registrierbarer Grant-Type angefordert, oder `redirect_uris` fehlt für einen Grant-Type, der dies erfordert |
| `400` | `invalid_scope` | Ein angeforderter Scope ist weder integriert noch registriert |
| `403` | `invalid_scope` | Der administrative Scope wurde angefordert: Er kann niemals über die Registrierung gewährt werden |
| `403` | `not_supported` | Die dynamische Client-Registrierung ist nicht aktiviert |
| `429` | `rate_limited` | Zu viele Registrierungen von dieser IP (10 pro Stunde) |

## Sicherheitsüberlegungen

Der Registrierungs-Endpunkt ist **nicht authentifiziert**, aber bewusst eingeschränkt konzipiert:

- **Ratenbegrenzt**: 10 Registrierungen pro IP und gleitender Stunde (`429 rate_limited`), sodass der Client-Store nicht geflutet werden kann.
- **Grant-Types eingeschränkt**: nur `authorization_code` + `refresh_token`; ein registrierter Client benötigt immer einen benutzervermittelten Ablauf und kann niemals als Machine-to-Machine-Client agieren.
- **Admin-Scope reserviert**: der Scope `authagonal-admin` (oder was auch immer `AdminApi:Scope` gesetzt ist) wird abgelehnt, sodass die Registrierung niemals einen Client erzeugen kann, der die [Admin-API](admin-api) erreicht.
- **PKCE ist bei registrierten Clients immer erforderlich.**

Für eine stärkere Zugriffskontrolle (initiale Access Tokens, mTLS, Software Statements) stellen Sie dem Endpunkt Ihre eigene Middleware oder einen `IAuthHook` vor. Erwägen Sie, die dynamische Registrierung in Umgebungen, in denen Self-Service-Registrierung keine Anforderung ist, ganz zu deaktivieren und Clients stattdessen über die Admin-API zu verwalten.

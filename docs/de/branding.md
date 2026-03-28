---
layout: default
title: Branding
locale: de
---

# Login-Oberflaeche anpassen

Die Login-SPA ist zur Laufzeit ueber eine `branding.json`-Datei konfigurierbar, die aus dem Web-Root bereitgestellt wird. Kein Neuaufbau erforderlich -- montieren Sie einfach Ihre Konfiguration und Assets.

## Funktionsweise

Beim Start ruft die SPA `/branding.json` ab. Wenn die Datei nicht existiert oder nicht erreichbar ist, werden Standardwerte verwendet. Die Konfiguration steuert:

- Anwendungsname (angezeigt in der Kopfzeile und im Seitentitel)
- Logo-Bild
- Primaerfarbe (Schaltflaechen, Links, Fokusringe)
- Sichtbarkeit des Passwort-vergessen-Links
- Benutzerdefiniertes CSS fuer tiefgreifendere Gestaltung

## Konfiguration

Platzieren Sie eine `branding.json`-Datei im `wwwroot/`-Verzeichnis (oder montieren Sie sie in den Docker-Container):

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
  "supportEmail": "help@acme.com",
  "showForgotPassword": true,
  "customCssUrl": "/branding/custom.css"
}
```

### Optionen

| Eigenschaft | Typ | Standard | Beschreibung |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Wird in der Kopfzeile und im Browser-Tab-Titel angezeigt |
| `logoUrl` | `string \| null` | `null` | URL zu einem Logo-Bild. Wenn gesetzt, ersetzt es die Textkopfzeile. |
| `primaryColor` | `string` | `"#2563eb"` | Hex-Farbe fuer Schaltflaechen, Links und Fokus-Indikatoren |
| `supportEmail` | `string \| null` | `null` | Support-Kontakt-E-Mail (fuer zukuenftige Verwendung reserviert) |
| `showForgotPassword` | `boolean` | `true` | Anzeigen/Ausblenden des "Passwort vergessen?"-Links auf der Login-Seite |
| `customCssUrl` | `string \| null` | `null` | URL zu einer benutzerdefinierten CSS-Datei, die nach den Standardstilen geladen wird |

## Docker-Beispiel

Montieren Sie Ihre Branding-Dateien in den Container:

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

Oder mit docker-compose:

```yaml
services:
  authagonal:
    build: .
    ports:
      - "8080:8080"
    volumes:
      - ./my-branding/branding.json:/app/wwwroot/branding.json
      - ./my-branding/assets:/app/wwwroot/branding
    environment:
      - Storage__ConnectionString=...
      - Issuer=https://auth.example.com
```

## Benutzerdefiniertes CSS

Die Option `customCssUrl` laedt ein zusaetzliches Stylesheet nach den Standardstilen, sodass Ihre Regeln Vorrang haben. Nuetzlich zum Aendern von Schriftarten, Anpassen von Abstaenden oder Neugestalten bestimmter Elemente.

### Verfuegbare CSS-Klassen

| Klasse | Element |
|---|---|
| `.auth-container` | Ganzseitiger Wrapper (Flex-Zentrierung) |
| `.auth-card` | Die Login-Karte (weisse Box mit Schatten) |
| `.auth-logo` | Logo-/Titelbereich |
| `.auth-logo h1` | Textkopfzeile (wenn kein Logo-Bild) |
| `.auth-logo-img` | Logo-Bild (wenn `logoUrl` gesetzt ist) |
| `.auth-title` | Seitentitel ("Anmelden", "Passwort zuruecksetzen") |
| `.auth-subtitle` | Sekundaertext unter Titeln |
| `.form-group` | Formularfeld-Wrapper |
| `.form-group label` | Feldbeschriftungen |
| `input` | Texteingaben |
| `.btn-primary` | Primaere Aktionsschaltflaeche |
| `.btn-secondary` | Sekundaere Schaltflaeche (z.B. "Weiter mit SSO") |
| `.alert-error` | Fehlermeldungen |
| `.alert-success` | Erfolgsmeldungen |
| `.link` | Textlinks |
| `.sso-notice` | SSO-Erkennungshinweis |
| `.password-requirements` | Passwortstaerke-Liste |

### CSS Custom Properties

Die Primaerfarbe ist als CSS Custom Property verfuegbar. Sie koennen sie in Ihrem benutzerdefinierten CSS ueberschreiben, anstatt `branding.json` zu verwenden:

```css
:root {
  --color-primary: #059669;
}
```

### Beispiel: Benutzerdefinierter Hintergrund und Schriftart

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}

.auth-card {
  border-radius: 16px;
  box-shadow: 0 4px 24px rgba(0, 0, 0, 0.2);
}

.auth-logo h1 {
  font-family: 'Inter', sans-serif;
  font-weight: 800;
}
```

## Anpassungsstufen

| Stufe | Vorgehensweise | Aktualisierungspfad |
|---|---|---|
| **Nur Konfiguration** | `branding.json` + Logo montieren | Nahtlos -- Docker-Image aktualisieren, Ihre Mounts behalten |
| **Konfiguration + CSS** | `customCssUrl` mit Stil-Ueberschreibungen hinzufuegen | Gleich -- CSS-Klassen sind stabil |
| **npm-Paket** | `npm install @drawboard/authagonal-login`, `branding.json` anpassen, in `wwwroot/` erstellen | Aktualisierbar -- `npm update` zieht neue Versionen |
| **SPA forken** | `login-app/` klonen, Quellcode aendern, eigene Version erstellen | Sie besitzen die Oberflaeche -- Server-Updates sind unabhaengig |
| **Eigene schreiben** | Vollstaendig benutzerdefiniertes Frontend gegen die Auth-API erstellen | Volle Kontrolle -- siehe [Auth-API](auth-api) fuer die Schnittstellenspezifikation |

Siehe `demos/custom-server/` fuer ein funktionierendes Beispiel mit benutzerdefiniertem Branding (gruenes Design, "Acme Corp").

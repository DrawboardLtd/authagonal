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
| `showRegistration` | `boolean` | `false` | Anzeigen/Ausblenden des Self-Service-Registrierungslinks |
| `customCssUrl` | `string \| null` | `null` | URL zu einer benutzerdefinierten CSS-Datei, die nach den Standardstilen geladen wird |
| `welcomeTitle` | `LocalizedString` | `null` | Ueberschreibt den Titel der Login-Seite (einfacher String oder `{ "en": "...", "de": "..." }`) |
| `welcomeSubtitle` | `LocalizedString` | `null` | Ueberschreibt den Untertitel der Login-Seite |
| `languages` | `array \| null` | `null` | Sprachauswahl-Optionen (`[{ "code": "en", "label": "English" }, ...]`) |

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

### CSS Custom Properties

Die Primaerfarbe wird ueber die CSS Custom Property `--brand-primary` gesetzt (die in das Tailwind-Theme einfliesst). Ueberschreiben Sie sie in Ihrem benutzerdefinierten CSS anstatt `branding.json` zu verwenden:

```css
:root {
  --brand-primary: #059669;
}
```

Die Login-Oberflaeche verwendet Tailwind CSS. Benutzerdefiniertes CSS kann Standard-HTML-Elemente und Tailwind-Utility-Klassen ansprechen. Die exportierten UI-Komponenten (`Button`, `Input`, `Card`, `Alert` usw.) verwenden intern Tailwind.

### Beispiel: Benutzerdefinierter Hintergrund und Schriftart

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Anpassungsstufen

| Stufe | Vorgehensweise | Aktualisierungspfad |
|---|---|---|
| **Nur Konfiguration** | `branding.json` + Logo montieren | Nahtlos -- Docker-Image aktualisieren, Ihre Mounts behalten |
| **Konfiguration + CSS** | `customCssUrl` mit Stil-Ueberschreibungen hinzufuegen | Gleich -- CSS-Klassen sind stabil |
| **npm-Paket** | `npm install @authagonal/login`, `branding.json` anpassen, in `wwwroot/` erstellen | Aktualisierbar -- `npm update` zieht neue Versionen |
| **SPA forken** | `login-app/` klonen, Quellcode aendern, eigene Version erstellen | Sie besitzen die Oberflaeche -- Server-Updates sind unabhaengig |
| **Eigene schreiben** | Vollstaendig benutzerdefiniertes Frontend gegen die Auth-API erstellen | Volle Kontrolle -- siehe [Auth-API](auth-api) fuer die Schnittstellenspezifikation |

Siehe `demos/custom-server/` fuer ein funktionierendes Beispiel mit benutzerdefiniertem Branding (gruenes Design, "Acme Corp").

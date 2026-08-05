---
layout: default
title: Branding
locale: de
---

# Login-Oberfläche anpassen

Die Login-SPA ist zur Laufzeit über eine `branding.json`-Datei konfigurierbar, die aus dem Web-Root bereitgestellt wird. Es ist kein Neuaufbau erforderlich: Montieren Sie einfach Ihre Konfiguration und Ihre Assets.

## Funktionsweise

Beim Start ruft die SPA `/branding.json` ab. Wenn die Datei nicht existiert oder nicht erreichbar ist, werden Standardwerte verwendet. (Ein Host-Server kann die Konfiguration auch als `<script type="application/json" id="authagonal-boot">`-Boot-Payload inline einbetten; ist dieser vorhanden, liest die SPA ihn, anstatt die Datei abzurufen.) Die Konfiguration steuert:

- Anwendungsname (angezeigt in der Kopfzeile und im Seitentitel)
- Logo-Bild, mit einem optionalen modusabhängigen Hintergrund-"Chip"
- Primärfarbe (Schaltflächen, Links, Fokusringe), mit einer optionalen Variante für den Dark Mode
- Hintergrundfarben von Seite und Karte, je Modus
- Sichtbarkeit der Links "Passwort vergessen" und "Registrierung"
- Standard für den Dark Mode (hell / Betriebssystem folgen / dunkel)
- Optionen der Sprachauswahl
- Die Fußzeile "Powered by Authagonal"
- Benutzerdefiniertes CSS für tiefgreifendere Gestaltung

## Konfiguration

Platzieren Sie eine `branding.json`-Datei im Verzeichnis `wwwroot/` (oder mounten Sie sie in den Docker-Container):

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
  "darkPrimaryColor": "#3b82f6",
  "darkMode": "auto",
  "supportEmail": "help@acme.com",
  "showForgotPassword": true,
  "customCssUrl": "/branding/custom.css"
}
```

### Optionen

| Eigenschaft | Typ | Standard | Beschreibung |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Wird in der Kopfzeile und im Titel des Browser-Tabs angezeigt |
| `logoUrl` | `string \| null` | `null` | URL zu einem Logo-Bild. Wenn gesetzt, ersetzt es die Textkopfzeile. |
| `primaryColor` | `string` | `"#2563eb"` | Hex-Farbe für Schaltflächen, Links und Fokusindikatoren |
| `supportEmail` | `string \| null` | `null` | Support-Kontakt-E-Mail (für zukünftige Verwendung reserviert) |
| `showForgotPassword` | `boolean` | `true` | Zeigt/verbirgt den Link "Passwort vergessen?" auf der Login-Seite |
| `showRegistration` | `boolean` | `false` | Zeigt/verbirgt den Self-Service-Registrierungslink |
| `customCssUrl` | `string \| null` | `null` | URL zu einer benutzerdefinierten CSS-Datei, die nach den Standardstilen geladen wird |
| `welcomeTitle` | `LocalizedString` | `null` | Optionale Begrüßung, die unter der Kopfzeile der Auth-Seiten gerendert wird (einfacher String oder `{ "en": "...", "de": "..." }`). Ohne Wert wird nichts gerendert. |
| `welcomeSubtitle` | `LocalizedString` | `null` | Optionale Zeile unter `welcomeTitle`, gleiche Form. Ohne Wert wird nichts gerendert. |
| `languages` | `array \| null` | `null` | Optionen der Sprachauswahl (`[{ "code": "en", "label": "English" }, ...]`). `null` zeigt alle mitgelieferten Sprachen außer den Neuheiten-Locales (siehe [Lokalisierung](localization)). |
| `poweredBy` | `boolean` | `true` | Zeigt/verbirgt die Fußzeile "Powered by Authagonal" auf den Auth-Seiten |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Standard-Theme, solange der Besucher noch keines gewählt hat: `"off"` (nur hell), `"auto"` (folgt der Betriebssystem-Einstellung), `"force"` (immer dunkel). Der Theme-Umschalter des Besuchers hat weiterhin Vorrang. |
| `lightBg` | `string \| null` | `null` | Hintergrundfarbe der Seite im hellen Modus |
| `lightCardBg` | `string \| null` | `null` | Hintergrundfarbe von Karte/Formular im hellen Modus |
| `darkBg` | `string \| null` | `null` | Hintergrundfarbe der Seite im Dark Mode |
| `darkCardBg` | `string \| null` | `null` | Hintergrundfarbe von Karte/Formular im Dark Mode |
| `darkPrimaryColor` | `string \| null` | `null` | Überschreibt `primaryColor` im Dark Mode |
| `lightLogoBg` | `string \| null` | `null` | Hintergrund des Logo-Chips im hellen Modus (siehe unten) |
| `darkLogoBg` | `string \| null` | `null` | Hintergrund des Logo-Chips im Dark Mode (siehe unten) |

Farbwerte müssen eine Hex-Farbe (`#rgb`, `#rrggbb`, `#rrggbbaa`) oder ein `rgb()`-/`rgba()`-/`hsl()`-/`hsla()`-Ausdruck sein; alles andere wird ignoriert. Die modusabhängigen Farben werden als `<style id="branding-theme-vars">`-Regel nach den gebündelten Stilen eingefügt (helle Werte bei `:root`, dunkle Werte bei `.dark`), sodass sich ein dunkler Wert von seinem hellen Gegenstück unterscheiden kann.

### Logo-Hintergrund-Chip

Wenn Ihr Logo weiße oder transparente Grafiken enthält, kann es auf der hellen Karte verschwinden. Setzen Sie `lightLogoBg` und/oder `darkLogoBg`, um das Logo innerhalb eines gepolsterten, abgerundeten "Chips" mit dieser Hintergrundfarbe darzustellen:

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

Der Chip (ein `data-auth="logo-chip"`-Wrapper, gesteuert über die CSS-Variable `--auth-logo-bg`) erhält Innenabstand und Hintergrund nur, wenn ein Logo-Hintergrund konfiguriert ist. Mandanten, die keinen setzen, sehen das Logo daher genau wie zuvor bündig auf der Karte. Die beiden Felder sind unabhängig voneinander: Setzen Sie nur `lightLogoBg`, um das Logo im hellen Modus mit Chip zu versehen und es im Dark Mode unverändert zu lassen.

## Docker-Beispiel

Mounten Sie Ihre Branding-Dateien in den Container:

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

Die Option `customCssUrl` lädt ein zusätzliches Stylesheet nach den Standardstilen, sodass Ihre Regeln Vorrang haben. Nützlich zum Ändern von Schriftarten, Anpassen von Abständen oder Neugestalten bestimmter Elemente. Die URL muss same-origin sein (relative URLs wie `/branding/custom.css` sind zulässig); Cross-Origin-Stylesheets werden stillschweigend übersprungen.

### CSS Custom Properties

Die Login-Oberfläche stellt mehrere CSS Custom Properties für die feingranulare Steuerung bereit:

| Eigenschaft | Standard | Beschreibung |
|---|---|---|
| `--brand-primary` | `#2563eb` | Primärfarbe für Schaltflächen, Links, Fokusringe |
| `--auth-bg` | `#f3f4f6` | Hintergrundfarbe der Seite |
| `--auth-card-bg` | `#ffffff` | Hintergrundfarbe von Karte/Formular |
| `--auth-logo-bg` | `transparent` | Hintergrund des Logo-Chips (der Innenabstand des Chips erscheint nur, wenn ein Logo-Hintergrund konfiguriert ist) |
| `--auth-radius` | `0.5rem` | Eckenradius der Auth-Karte |
| `--auth-font` | *(erbt; System-Font-Stack)* | Schriftfamilie für die Auth-Karte |
| `--auth-heading` | `#111827` | Textfarbe der Überschriften |

Die Farbvariablen hier entsprechen direkt den Konfigurationsfeldern (`primaryColor`, `lightBg`/`darkBg`, `lightCardBg`/`darkCardBg`, `lightLogoBg`/`darkLogoBg`); bevorzugen Sie daher die Konfiguration für einfache Farbänderungen und reservieren Sie benutzerdefiniertes CSS für alles Weitere.

Überschreiben Sie sie in Ihrem benutzerdefinierten CSS:

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

Die Login-Oberfläche verwendet Tailwind CSS. Benutzerdefiniertes CSS kann Standard-HTML-Elemente und Tailwind-Utility-Klassen ansprechen. Die exportierten UI-Komponenten (`Button`, `Input`, `Card`, `Alert` usw.) verwenden intern Tailwind.

## Dark Mode

Die Login-SPA wird mit hellen, dunklen und **System**-Themes ausgeliefert. Der Theme-Umschalter ist im Layout immer sichtbar. Die Auswahl des Benutzers wird unter dem Schlüssel `auth-theme` in `localStorage` gespeichert.

### Funktionsweise

- **Standard**: Bis der Besucher ein Theme auswählt, legt die Branding-Option `darkMode` den Standard fest: `"off"` (hell), `"auto"` (System, der Standardwert) oder `"force"` (dunkel). Sobald der Besucher den Umschalter benutzt, hat seine Wahl immer Vorrang.
- **Erkennung**: Wenn das Theme "system" ist, beobachtet die SPA `window.matchMedia('(prefers-color-scheme: dark)')` und wendet das Theme automatisch neu an, sobald sich die Betriebssystem-Einstellung ändert.
- **Anwendung**: Die SPA schaltet eine `.dark`-Klasse auf `<html>` um. Die Dark-Variante von Tailwind (`&:where(.dark, .dark *)`) aktiviert die in jeder Komponente kompilierten dunklen Stile.
- **Persistenz**: Explizite Auswahlen von "light" / "dark" / "system" werden in `localStorage` gespeichert.

### CSS-Variablen

Helle Werte werden bei `:root` deklariert; Dark-Mode-Überschreibungen sind auf `.dark` beschränkt, sodass das Mandanten-Branding in `customCssUrl` bei Angabe immer Vorrang hat.

| Variable | Hell | Dunkel |
|---|---|---|
| `--auth-bg` | `#f3f4f6` (oder `lightBg`) | `#030712` (oder `darkBg`) |
| `--auth-card-bg` | `#ffffff` (oder `lightCardBg`) | `#111827` (oder `darkCardBg`) |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent` (oder `lightLogoBg`) | `transparent` (oder `darkLogoBg`) |
| `--brand-primary` | `#2563eb` (oder `primaryColor`) | der helle Wert (oder `darkPrimaryColor`) |

### Deaktivieren oder Überschreiben

Das Mandanten-Branding hat immer Vorrang. Um ein einzelnes Theme zu erzwingen, setzen Sie eigene Werte in `customCssUrl`:

```css
/* Dunkle Palette unabhängig von der Benutzerauswahl erzwingen */
:root {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
.dark {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

Um den Theme-Umschalter vollständig zu entfernen, verwenden Sie den npm-Paket-Pfad: Importieren Sie `AuthLayout` und rendern Sie ohne den Umschalter, oder forken Sie die SPA.

### Data-Attribute

Alle Elemente des Login-Formulars verfügen über `data-auth`-Attribute für CSS-Targeting und Testautomatisierung:

| Attribut | Element |
|---|---|
| `data-auth="page"` | Haupt-Seiten-Wrapper |
| `data-auth="header"` | Kopfzeilenbereich |
| `data-auth="logo-chip"` | Wrapper um das Logo-Bild (nur gepolstert, wenn ein Logo-Hintergrund gesetzt ist) |
| `data-auth="logo"` | Logo-Bild |
| `data-auth="app-name"` | Überschrift mit dem App-Namen |
| `data-auth="content"` | Hauptinhaltsbereich |
| `data-auth="languages"` | Sprachauswahl |
| `data-auth="language-trigger"` | Auslöser-Schaltfläche der Sprachauswahl |
| `data-auth="theme-toggle"` | Umschalter für Hell-/System-/Dunkel-Theme |
| `data-auth="powered-by"` | Fußzeile "Powered by Authagonal" |

Sprechen Sie diese in Ihrem benutzerdefinierten CSS an:

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

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
| **Nur Konfiguration** | `branding.json` + Logo mounten | Nahtlos: Docker-Image aktualisieren, Ihre Mounts beibehalten |
| **Konfiguration + CSS** | `customCssUrl` mit Stil-Überschreibungen hinzufügen | Gleich: CSS-Klassen sind stabil |
| **npm-Paket** | `npm install @authagonal/login`, `branding.json` anpassen, nach `wwwroot/` bauen | Aktualisierbar: `npm update` zieht neue Versionen |
| **SPA forken** | `login-app/` klonen, Quellcode ändern, eigene Version bauen | Sie besitzen die Oberfläche: Server-Updates sind unabhängig |
| **Eigene schreiben** | Vollständig benutzerdefiniertes Frontend gegen die Auth-API erstellen | Volle Kontrolle: siehe [Auth-API](auth-api) für die Schnittstelle |

Siehe `demos/custom-server/` für ein funktionierendes Beispiel mit benutzerdefiniertem Branding (grünes Theme, "Acme Corp").

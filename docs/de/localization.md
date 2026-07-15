---
layout: default
title: Lokalisierung
locale: de
---

# Lokalisierung

Die Login-Oberfläche liefert standardmäßig elf Sprachen: Englisch, Vereinfachtes Chinesisch (`zh-Hans`), Deutsch (`de`), Französisch (`fr`), Spanisch (`es`), Vietnamesisch (`vi`), Portugiesisch (`pt`), Arabisch (`ar`), Afrikaans (`af`), Hindi (`hi`) und eine Klingonisch-Neuheitssprache (`tlh`). Die Server-API-Antworten sind in den ersten sieben davon lokalisiert. Die Lokalisierung umfasst die Server-API-Antworten, die Login-Oberfläche und diese Dokumentationsseite.

## Unterstützte Sprachen

| Code | Sprache | Login-UI | Server-API |
|---|---|---|---|
| `en` | Englisch (Standard) | ✓ | ✓ |
| `zh-Hans` | Vereinfachtes Chinesisch | ✓ | ✓ |
| `de` | Deutsch | ✓ | ✓ |
| `fr` | Französisch | ✓ | ✓ |
| `es` | Spanisch | ✓ | ✓ |
| `vi` | Vietnamesisch | ✓ | ✓ |
| `pt` | Portugiesisch | ✓ | ✓ |
| `ar` | Arabisch (rechts-nach-links) | ✓ | — |
| `af` | Afrikaans | ✓ | — |
| `hi` | Hindi | ✓ | — |
| `tlh` | Klingonisch (Neuheit) | ✓ | — |

## Server (API-Antworten)

Der Server verwendet die integrierte Lokalisierung von ASP.NET Core mit `IStringLocalizer<T>` und `.resx`-Ressourcendateien. Die Sprache wird aus dem `Accept-Language`-HTTP-Header ausgewählt.

### Was lokalisiert ist

- Passwort-Validierungsfehlermeldungen
- Passwortrichtlinien-Labels (`GET /api/auth/password-policy`)
- Nachrichten zum Passwort-Zurücksetzen (Token-Fehler, Ablauf, Erfolg)
- Allgemeine Fehlerbeschreibungen der Ausnahmebehandlungs-Middleware
- Admin-Benutzerverwaltungsnachrichten (E-Mail-Bestätigung, Verifizierung usw.)
- Bestätigungsnachricht zum Beenden der Sitzung

### Was NICHT lokalisiert ist

- Maschinenlesbare `error`-Codes (`"email_required"`, `"invalid_credentials"` usw.), diese sind API-Verträge und bleiben konstant
- OAuth/OIDC-Fehlercodes und entwicklerbezogene Fehlerbeschreibungen an Token-, Autorisierungs- und Widerrufsendpunkten
- Interne Protokollnachrichten und Ausnahmenachrichten

### Server-Lokalisierung testen

Senden Sie einen `Accept-Language`-Header an einen beliebigen lokalisierten Endpunkt:

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Ressourcendateien

Alle Server-Übersetzungszeichenketten befinden sich in `.resx`-Dateien unter `src/Authagonal.Server/Resources/`:

```
Resources/
  SharedMessages.cs          # Marker class
  SharedMessages.resx        # English (default)
  SharedMessages.zh-Hans.resx
  SharedMessages.de.resx
  SharedMessages.fr.resx
  SharedMessages.es.resx
  SharedMessages.vi.resx
  SharedMessages.pt.resx
```

## Login-Oberfläche

Die Login-SPA verwendet [react-i18next](https://react.i18next.com/) für die clientseitige Lokalisierung. Die Sprache wird automatisch aus der `navigator.language`-Einstellung des Browsers erkannt.

Die registrierten Sprachen befinden sich in einer einzigen `LANGUAGES`-Registry in `login-app/src/i18n/index.ts`, die sowohl die i18next-Ressourcenregistrierung als auch jede Sprachauswahl steuert, sodass die beiden nicht auseinanderdriften können. Als `novelty` gekennzeichnete Sprachen (derzeit `tlh`) bleiben voll funktionsfähig (`?lng=tlh` funktioniert), sind aber von der Standardauswahl ausgeschlossen; sie erscheinen nur dann in einem Dropdown, wenn die `BrandingConfig.languages` eines Mandanten sie ausdrücklich auflistet. Mandanten können die Auswahl auf die gleiche Weise einschränken: Ein `languages`-Array in `branding.json` ersetzt die Standardliste vollständig (siehe [Branding](branding)).

Die aktive Sprache wird auf `<html lang>` und `<html dir>` gespiegelt, sodass Rechts-nach-links-Sprachen (`ar`) die Auth-Karte automatisch umkehren, auch wenn die Sprache über die Auswahl direkt gewechselt wird.

### Spracherkennung

Die Erkennungsreihenfolge ist:

1. **localStorage**: gespeicherte Präferenz von einem früheren Besuch
2. **Abfrageparameter**: `?lng=de` überschreibt die Browsererkennung
3. **Browsersprache**: `navigator.language` (automatisch)
4. **Fallback**: Englisch (`en`)

### Übersetzungsdateien

Übersetzungs-JSON-Dateien sind mit der App gebündelt unter `login-app/src/i18n/`:

```
i18n/
  index.ts        # i18n initialization + the LANGUAGES registry
  en.json         # English
  zh-Hans.json    # Simplified Chinese
  de.json         # German
  fr.json         # French
  es.json         # Spanish
  vi.json         # Vietnamese
  pt.json         # Portuguese
  ar.json         # Arabic
  af.json         # Afrikaans
  hi.json         # Hindi
  tlh.json        # Klingon (novelty)
```

### Passwortrichtlinien-Labels

Die Passwort-Zurücksetzungsseite übersetzt ihre Passwortanforderungs-Checkliste clientseitig basierend auf dem `rule`-Schlüssel, der von `GET /api/auth/password-policy` zurückgegeben wird (mit Rückfall auf das vom Server bereitgestellte `label` für nicht erkannte Regeln). Dies stellt sicher, dass die Anforderungen der in der Oberfläche gewählten Sprache folgen, auch wenn der `Accept-Language`-Header des Browsers abweicht. Die Registrierungsseite zeigt die vom Server bereitgestellten `label`-Werte an, die aus `Accept-Language` lokalisiert werden.

### npm-Paketnutzer

Wenn Sie die Login-App über `@authagonal/login` nutzen, wird die i18n-Instanz exportiert:

```typescript
import { i18n } from '@authagonal/login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Dokumentation

Die Dokumentationsseite verwendet einen verzeichnisbasierten Ansatz. Englische Seiten befinden sich im Stammverzeichnis und Übersetzungen in Sprachunterverzeichnissen (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`, `/vi/`, `/pt/`). Ein Sprachumschalter-Dropdown in der Seitenleiste ermöglicht das Wechseln zwischen Sprachen.

## Eine neue Sprache hinzufügen

Um Unterstützung für eine neue Sprache hinzuzufügen (z.B. Japanisch `ja`):

### 1. Server

Erstellen Sie eine neue `.resx`-Datei, indem Sie die englische kopieren und die Werte übersetzen:

```
src/Authagonal.Server/Resources/SharedMessages.ja.resx
```

Fügen Sie `"ja"` zum Array der unterstützten Kulturen in `AuthagonalExtensions.cs` hinzu:

```csharp
var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja" };
```

### 2. Login-Oberfläche

Erstellen Sie eine neue Übersetzungs-JSON-Datei, indem Sie `en.json` kopieren und die Werte übersetzen:

```
login-app/src/i18n/ja.json
```

Registrieren Sie sie im `LANGUAGES`-Array in `login-app/src/i18n/index.ts`. Dieser eine Eintrag registriert die i18next-Ressource und fügt die Sprache zu jeder Auswahl hinzu:

```typescript
import ja from './ja.json';

// In the LANGUAGES array:
{ code: 'ja', label: '日本語', resource: ja },
```

### 3. Dokumentation

Erstellen Sie ein neues Verzeichnis mit übersetzten Markdown-Dateien:

```
docs/ja/
  index.md
  installation.md
  quickstart.md
  ...
```

Fügen Sie einen Sprach-Standard in `docs/_config.yml` hinzu:

```yaml
defaults:
  - scope:
      path: "ja"
    values:
      locale: "ja"
```

Fügen Sie die Sprachoption zum Umschalter in `docs/_layouts/default.html` hinzu.

## Neue Zeichenketten hinzufügen

### Server

1. Fügen Sie den Schlüssel und den englischen Wert zu `SharedMessages.resx` hinzu
2. Fügen Sie übersetzte Werte zu jeder `.resx`-Datei der jeweiligen Sprache hinzu
3. Verwenden Sie `IStringLocalizer<SharedMessages>`, um auf die Zeichenkette zuzugreifen:

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Login-Oberfläche

1. Fügen Sie den Schlüssel und den englischen Wert zu `en.json` hinzu
2. Fügen Sie übersetzte Werte zu jeder JSON-Datei der jeweiligen Sprache hinzu
3. Verwenden Sie die `t()`-Funktion in Komponenten:

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```

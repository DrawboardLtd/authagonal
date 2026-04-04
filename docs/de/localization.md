---
layout: default
title: Lokalisierung
locale: de
---

# Lokalisierung

Authagonal unterstutzt sechs Sprachen standardmassig: Englisch, Vereinfachtes Chinesisch (`zh-Hans`), Deutsch (`de`), Franzosisch (`fr`), Spanisch (`es`) und Vietnamesisch (`vi`). Die Lokalisierung umfasst die Server-API-Antworten, die Login-Oberflache und diese Dokumentationsseite.

## Unterstutzte Sprachen

| Code | Sprache |
|---|---|
| `en` | Englisch (Standard) |
| `zh-Hans` | Vereinfachtes Chinesisch |
| `de` | Deutsch |
| `fr` | Franzosisch |
| `es` | Spanisch |
| `vi` | Vietnamesisch |

## Server (API-Antworten)

Der Server verwendet die integrierte Lokalisierung von ASP.NET Core mit `IStringLocalizer<T>` und `.resx`-Ressourcendateien. Die Sprache wird aus dem `Accept-Language`-HTTP-Header ausgewahlt.

### Was lokalisiert ist

- Passwort-Validierungsfehlermeldungen
- Passwortrichtlinien-Labels (`GET /api/auth/password-policy`)
- Nachrichten zum Passwort-Zurucksetzen (Token-Fehler, Ablauf, Erfolg)
- Allgemeine Fehlerbeschreibungen der Ausnahmebehandlungs-Middleware
- Admin-Benutzerverwaltungsnachrichten (E-Mail-Bestatigung, Verifizierung usw.)
- Bestatigungsnachricht zum Beenden der Sitzung

### Was NICHT lokalisiert ist

- Maschinenlesbare `error`-Codes (`"email_required"`, `"invalid_credentials"` usw.) — diese sind API-Vertrage und bleiben konstant
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

Alle Server-Ubersetzungszeichenketten befinden sich in `.resx`-Dateien unter `src/Authagonal.Server/Resources/`:

```
Resources/
  SharedMessages.cs          # Marker class
  SharedMessages.resx        # English (default)
  SharedMessages.zh-Hans.resx
  SharedMessages.de.resx
  SharedMessages.fr.resx
  SharedMessages.es.resx
```

## Login-Oberflache

Die Login-SPA verwendet [react-i18next](https://react.i18next.com/) fur die clientseitige Lokalisierung. Die Sprache wird automatisch aus der `navigator.language`-Einstellung des Browsers erkannt.

### Spracherkennung

Die Erkennungsreihenfolge ist:

1. **Abfrageparameter** — `?lng=de` uberschreibt alles
2. **Browsersprache** — `navigator.language` (automatisch)
3. **Fallback** — Englisch (`en`)

### Ubersetzungsdateien

Ubersetzungs-JSON-Dateien sind mit der App gebundelt unter `login-app/src/i18n/`:

```
i18n/
  index.ts        # i18n initialization
  en.json         # English
  zh-Hans.json    # Simplified Chinese
  de.json         # German
  fr.json         # French
  es.json         # Spanish
```

### Passwortrichtlinien-Labels

Die Login-Oberflache ubersetzt Passwortanforderungs-Labels clientseitig basierend auf dem `rule`-Schlussel, der von `GET /api/auth/password-policy` zuruckgegeben wird, anstatt das vom Server bereitgestellte `label`-Feld zu verwenden. Dies stellt sicher, dass die Passwortanforderungen immer in der Browsersprache des Benutzers angezeigt werden, auch wenn der `Accept-Language`-Header des Servers abweicht.

### npm-Paketnutzer

Wenn Sie die Login-App uber `@drawboard/authagonal-login` nutzen, wird die i18n-Instanz exportiert:

```typescript
import { i18n } from '@drawboard/authagonal-login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Dokumentation

Die Dokumentationsseite verwendet einen verzeichnisbasierten Ansatz. Englische Seiten befinden sich im Stammverzeichnis und Ubersetzungen in Sprachunterverzeichnissen (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`). Ein Sprachumschalter-Dropdown in der Seitenleiste ermoglicht das Wechseln zwischen Sprachen.

## Eine neue Sprache hinzufugen

Um Unterstutzung fur eine neue Sprache hinzuzufugen (z.B. Japanisch `ja`):

### 1. Server

Erstellen Sie eine neue `.resx`-Datei, indem Sie die englische kopieren und die Werte ubersetzen:

```
src/Authagonal.Server/Resources/SharedMessages.ja.resx
```

Fugen Sie `"ja"` zum Array der unterstutzten Kulturen in `AuthagonalExtensions.cs` hinzu:

```csharp
var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "ja" };
```

### 2. Login-Oberflache

Erstellen Sie eine neue Ubersetzungs-JSON-Datei, indem Sie `en.json` kopieren und die Werte ubersetzen:

```
login-app/src/i18n/ja.json
```

Registrieren Sie sie in `login-app/src/i18n/index.ts`:

```typescript
import ja from './ja.json';

// In the resources object:
ja: { translation: ja },
```

### 3. Dokumentation

Erstellen Sie ein neues Verzeichnis mit ubersetzten Markdown-Dateien:

```
docs/ja/
  index.md
  installation.md
  quickstart.md
  ...
```

Fugen Sie einen Sprach-Standard in `docs/_config.yml` hinzu:

```yaml
defaults:
  - scope:
      path: "ja"
    values:
      locale: "ja"
```

Fugen Sie die Sprachoption zum Umschalter in `docs/_layouts/default.html` hinzu.

## Neue Zeichenketten hinzufugen

### Server

1. Fugen Sie den Schlussel und den englischen Wert zu `SharedMessages.resx` hinzu
2. Fugen Sie ubersetzte Werte zu jeder `.resx`-Datei der jeweiligen Sprache hinzu
3. Verwenden Sie `IStringLocalizer<SharedMessages>`, um auf die Zeichenkette zuzugreifen:

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Login-Oberflache

1. Fugen Sie den Schlussel und den englischen Wert zu `en.json` hinzu
2. Fugen Sie ubersetzte Werte zu jeder JSON-Datei der jeweiligen Sprache hinzu
3. Verwenden Sie die `t()`-Funktion in Komponenten:

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```

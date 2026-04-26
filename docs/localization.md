---
layout: default
title: Localization
---

# Localization

Authagonal supports eight languages out of the box: English, Simplified Chinese (`zh-Hans`), German (`de`), French (`fr`), Spanish (`es`), Vietnamese (`vi`), Portuguese (`pt`), and Klingon (`tlh`). Localization covers the server API responses, the login UI, and this documentation site.

## Supported Languages

| Code | Language |
|---|---|
| `en` | English (default) |
| `zh-Hans` | Simplified Chinese |
| `de` | German |
| `fr` | French |
| `es` | Spanish |
| `vi` | Vietnamese |
| `pt` | Portuguese |

## Server (API Responses)

The server uses ASP.NET Core's built-in localization with `IStringLocalizer<T>` and `.resx` resource files. The language is selected from the `Accept-Language` HTTP header.

### What is localized

- Password validation error messages
- Password policy labels (`GET /api/auth/password-policy`)
- Password reset flow messages (token errors, expiration, success)
- Generic error descriptions from the exception handling middleware
- Admin user management messages (email confirmation, verification, etc.)
- End session confirmation message

### What is NOT localized

- Machine-readable `error` codes (`"email_required"`, `"invalid_credentials"`, etc.) — these are API contracts and remain constant
- OAuth/OIDC error codes and developer-facing error descriptions on token, authorize, and revocation endpoints
- Internal log messages and exception messages

### Testing server localization

Send an `Accept-Language` header to any localized endpoint:

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Resource files

All server translation strings are in `.resx` files under `src/Authagonal.Server/Resources/`:

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

## Login UI

The login SPA uses [react-i18next](https://react.i18next.com/) for client-side localization. Language is auto-detected from the browser's `navigator.language` setting.

### Language detection

The detection order is:

1. **localStorage** — persisted preference from a previous visit
2. **Query parameter** — `?lng=de` overrides browser detection
3. **Browser language** — `navigator.language` (automatic)
4. **Fallback** — English (`en`)

### Translation files

Translation JSON files are bundled with the app at `login-app/src/i18n/`:

```
i18n/
  index.ts        # i18n initialization
  en.json         # English
  zh-Hans.json    # Simplified Chinese
  de.json         # German
  fr.json         # French
  es.json         # Spanish
  vi.json         # Vietnamese
  pt.json         # Portuguese
  tlh.json        # Klingon
```

### Password policy labels

The login UI translates password requirement labels client-side based on the `rule` key returned by `GET /api/auth/password-policy`, rather than using the server-provided `label` field. This ensures the password requirements are always shown in the user's browser language, even if the server `Accept-Language` header differs.

### npm package consumers

If you consume the login app via `@authagonal/login`, the i18n instance is exported:

```typescript
import { i18n } from '@authagonal/login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Documentation

The docs site uses a directory-based approach. English pages are at the root, and translations are in locale subdirectories (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`). A language switcher dropdown in the sidebar allows switching between languages.

## Adding a New Language

To add support for a new language (e.g., Japanese `ja`):

### 1. Server

Create a new `.resx` file by copying the English one and translating the values:

```
src/Authagonal.Server/Resources/SharedMessages.ja.resx
```

Add `"ja"` to the supported cultures array in `AuthagonalExtensions.cs`:

```csharp
var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja" };
```

### 2. Login UI

Create a new translation JSON file by copying `en.json` and translating the values:

```
login-app/src/i18n/ja.json
```

Register it in `login-app/src/i18n/index.ts`:

```typescript
import ja from './ja.json';

// In the resources object:
ja: { translation: ja },
```

### 3. Documentation

Create a new directory with translated markdown files:

```
docs/ja/
  index.md
  installation.md
  quickstart.md
  ...
```

Add a locale default in `docs/_config.yml`:

```yaml
defaults:
  - scope:
      path: "ja"
    values:
      locale: "ja"
```

Add the language option to the switcher in `docs/_layouts/default.html`.

## Adding New Strings

### Server

1. Add the key and English value to `SharedMessages.resx`
2. Add translated values to each locale's `.resx` file
3. Use `IStringLocalizer<SharedMessages>` to access the string:

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Login UI

1. Add the key and English value to `en.json`
2. Add translated values to each locale's JSON file
3. Use the `t()` function in components:

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```

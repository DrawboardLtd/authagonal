# @drawboard/authagonal-login

Default login UI for [Authagonal](https://github.com/DrawboardLtd/authagonal) — an OAuth 2.0 / OpenID Connect authentication server backed by Azure Table Storage.

## Usage

### As a standalone app

The login SPA is built into the Authagonal Docker image and served from `wwwroot/`. Configure it at runtime via `branding.json`.

### As an npm package

Install the package to build a custom login experience while reusing the API client, branding, i18n, and base components:

```bash
npm install @drawboard/authagonal-login
```

```tsx
import {
  // API client
  login, logout, ssoCheck, getProviders, getSession, forgotPassword, resetPassword,

  // Branding
  useBranding, loadBranding, resolveLocalized,

  // i18n (re-exported to avoid module duplication)
  useTranslation,

  // Components & pages
  AuthLayout, LoginPage, ForgotPasswordPage, ResetPasswordPage,

  // Types
  type BrandingConfig, type LocalizedString, type ExternalProvider,
} from '@drawboard/authagonal-login';
import '@drawboard/authagonal-login/styles.css';
```

Override individual pages while reusing the rest:

```tsx
// Custom login page with Terms of Service checkbox
import { AuthLayout, ForgotPasswordPage, ResetPasswordPage, useTranslation } from '@drawboard/authagonal-login';
import '@drawboard/authagonal-login/styles.css';
import MyLoginPage from './MyLoginPage';

function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route element={<AuthLayout />}>
          <Route path="/login" element={<MyLoginPage />} />
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/reset-password" element={<ResetPasswordPage />} />
        </Route>
      </Routes>
    </BrowserRouter>
  );
}
```

See [`demos/custom-server/login-app`](https://github.com/DrawboardLtd/authagonal/tree/master/demos/custom-server/login-app) for a complete example.

## Branding

Place a `branding.json` in your public directory:

```json
{
  "appName": "My App",
  "logoUrl": "/logo.png",
  "primaryColor": "#2563eb",
  "showForgotPassword": true,
  "welcomeTitle": { "en": "Welcome", "es": "Bienvenido" },
  "welcomeSubtitle": { "en": "Sign in to continue", "es": "Inicia sesion para continuar" }
}
```

String fields like `welcomeTitle` accept either a plain string or a `{ lang: text }` object for per-language overrides.

## i18n

Built-in support for 7 languages: English, Chinese (Simplified), German, French, Spanish, Vietnamese, Portuguese. Language is auto-detected from the browser and persisted to localStorage. Add `?lng=es` to force a language.

## License

Proprietary — Drawboard Ltd.

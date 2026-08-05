# @authagonal/login

Default login UI for [Authagonal](https://github.com/authagonal/authagonal), a self-hosted OAuth 2.0 / OpenID Connect / SAML 2.0 authentication server with pluggable storage (Azure Table Storage or AWS DynamoDB).

Use as a standalone app (built into the Authagonal Docker image) or as an npm package to build a custom login experience while reusing the API client, branding, i18n, and base components.

## Installation

```bash
npm install @authagonal/login react react-dom react-router
```

`react`, `react-dom`, and `react-router` are externalized at build time and declared as **peer**
dependencies, your app must provide them. That is not a formality: the exported pages call
`useNavigate` and `useSearchParams`, so they have to resolve to the same `react-router` instance as
the `<BrowserRouter>` you wrap them in, and their hooks have to run against the React instance that
renders them. A second copy installed under this package produces "Invalid hook call" or
"useNavigate() may be used only in the context of a `<Router>` component" from a host that did
everything right.

`react-router` v8, not `react-router-dom` — v8 removes that package and ships one.

## Quick start

Import the base components and styles, then mount the router:

```tsx
import { BrowserRouter, Routes, Route } from 'react-router';
import { AuthLayout, LoginPage, ForgotPasswordPage, ResetPasswordPage } from '@authagonal/login';
import '@authagonal/login/styles.css';

function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route element={<AuthLayout />}>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/reset-password" element={<ResetPasswordPage />} />
        </Route>
      </Routes>
    </BrowserRouter>
  );
}
```

## Customizing pages

Override individual pages while keeping the rest. Your custom page has access to the same API client, branding hooks, and i18n as the built-in pages:

```tsx
import { AuthLayout, ForgotPasswordPage, ResetPasswordPage } from '@authagonal/login';
import { login, useBranding, useTranslation, ApiRequestError } from '@authagonal/login';
import '@authagonal/login/styles.css';

function MyLoginPage() {
  const { t } = useTranslation();
  const branding = useBranding();
  const [agreedToTerms, setAgreedToTerms] = useState(false);

  async function handleSubmit(email: string, password: string) {
    if (!agreedToTerms) throw new Error('You must agree to the Terms of Service');
    await login(email, password);
    window.location.href = '/';
  }

  return (
    <form onSubmit={/* ... */}>
      {/* Your custom UI using t(), branding, login(), etc. */}
    </form>
  );
}

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

See [`demos/custom-server/login-app`](https://github.com/authagonal/authagonal/tree/master/demos/custom-server/login-app) for a complete working example with a Terms of Service checkbox and branded footer.

## API client

All functions call the Authagonal auth API with cookie credentials. Set `VITE_API_URL` to point to a different origin during development.

```ts
import { login, logout, forgotPassword, resetPassword, getSession, ssoCheck, getProviders, getPasswordPolicy, ApiRequestError } from '@authagonal/login';

// Password login — sets a session cookie
await login('user@example.com', 'password');

// End the session
await logout();

// Check if the user has an active session
const session = await getSession();
// → { authenticated: true, userId, email, name }

// Check if an email domain requires SSO
const sso = await ssoCheck('user@corp.com');
// → { ssoRequired: true, redirectUrl: '/oidc/azure/login' }

// List configured external providers (Google, Azure AD, etc.)
const { providers } = await getProviders();
// → [{ connectionId: 'google', name: 'Google', loginUrl: '/oidc/google/login' }]

// Password reset flow
await forgotPassword('user@example.com');
await resetPassword(token, newPassword);

// Fetch password policy rules for frontend validation
const { rules } = await getPasswordPolicy();
// → [{ rule: 'MinLength', value: 8, label: 'At least 8 characters' }, ...]

// Error handling
try {
  await login(email, password);
} catch (err) {
  if (err instanceof ApiRequestError) {
    switch (err.error) {
      case 'invalid_credentials': /* wrong email/password */ break;
      case 'locked_out':          /* account locked, err.retryAfter has seconds */ break;
      case 'email_not_confirmed': /* email verification pending */ break;
      case 'sso_required':        /* must use SSO, err.redirectUrl has the URL */ break;
    }
  }
}
```

## Branding

Place a `branding.json` in your public directory. `AuthLayout` fetches it on mount when nothing above
it has provided branding, so the quick start above needs no extra wiring. Wrap the tree in
`<BrandingContext.Provider>` (as `loadBranding()` + your own root does) and that value is used instead
— the layout does not fetch a second time.

```json
{
  "appName": "My App",
  "logoUrl": "/logo.png",
  "primaryColor": "#2563eb",
  "supportEmail": "help@example.com",
  "showForgotPassword": true,
  "customCssUrl": "/custom.css",
  "welcomeTitle": "Welcome to My App",
  "welcomeSubtitle": "Sign in to continue"
}
```

### BrandingConfig fields

| Field | Type | Default | Description |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Shown in the header and page title |
| `logoUrl` | `string \| null` | `null` | Image URL replacing the text header |
| `primaryColor` | `string` | `"#2563eb"` | Buttons, links, focus rings via CSS custom properties |
| `supportEmail` | `string \| null` | `null` | Contact email shown in the footer |
| `showForgotPassword` | `boolean` | `true` | Toggle the forgot password link |
| `showRegistration` | `boolean` | `false` | Toggle the self-registration link on the login page |
| `customCssUrl` | `string \| null` | `null` | URL to additional CSS for deeper styling |
| `welcomeTitle` | `LocalizedString` | `null` | Override the login page title |
| `welcomeSubtitle` | `LocalizedString` | `null` | Override the login page subtitle |
| `languages` | `{code, label}[] \| null` | `null` | Restrict/override the language picker entries |
| `poweredBy` | `boolean` | `true` | Show the "Powered by Authagonal" footer |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Default theme when the visitor hasn't picked one; the visitor's toggle wins |
| `lightBg` / `lightCardBg` / `darkBg` / `darkCardBg` | `string \| null` | `null` | Per-mode page/card surface colour overrides |
| `darkPrimaryColor` | `string \| null` | `null` | Overrides `primaryColor` in dark mode |
| `lightLogoBg` / `darkLogoBg` | `string \| null` | `null` | Optional per-mode background "chip" behind the logo, for white/transparent artwork |

### Localized strings

`welcomeTitle` and `welcomeSubtitle` accept either a plain string or an object mapping language codes to strings:

```json
{
  "welcomeTitle": {
    "en": "Welcome to Acme",
    "es": "Bienvenido a Acme",
    "de": "Willkommen bei Acme"
  }
}
```

Use `resolveLocalized()` to resolve these in your own components:

```ts
import { resolveLocalized, useBranding, useTranslation } from '@authagonal/login';

const branding = useBranding();
const { i18n } = useTranslation();
const title = resolveLocalized(branding.welcomeTitle, i18n.language) ?? 'Default Title';
```

## i18n

Built-in support for 10 languages (plus a Klingon easter egg):

| Code | Language |
|---|---|
| `en` | English |
| `zh-Hans` | Chinese (Simplified) |
| `de` | German |
| `fr` | French |
| `es` | Spanish |
| `vi` | Vietnamese |
| `pt` | Portuguese |
| `ar` | Arabic (RTL) |
| `af` | Afrikaans |
| `hi` | Hindi |
| `tlh` | Klingon (novelty; hidden from default pickers) |

Language is auto-detected from the browser and persisted to `localStorage`. Force a language via query string: `?lng=es`.

The `useTranslation` hook is re-exported from this package to avoid React context duplication. Always import it from `@authagonal/login`, not directly from `react-i18next`:

```ts
// Correct
import { useTranslation } from '@authagonal/login';

// Wrong — will get a different i18n instance
import { useTranslation } from 'react-i18next';
```

## Exports

### Components

| Export | Description |
|---|---|
| `AuthLayout` | Layout wrapper, loads branding, renders language selector, wraps `<Outlet />` |
| `LoginPage` | Login form with SSO check, external providers, session detection |
| `ForgotPasswordPage` | Email input → sends reset link |
| `ResetPasswordPage` | Token + new password form with policy validation |
| `MfaChallengePage` | TOTP/passkey/recovery code verification |
| `MfaSetupPage` | QR code scanning, passkey registration, recovery code generation |
| `RegisterPage` | Self-service registration form with email/password |
| `App` | Standalone SPA with full routing (login, register, forgot/reset password, MFA) |

### UI Components

| Export | Description |
|---|---|
| `Button` | Styled button with variants (`default`, `outline`, `ghost`, etc.) |
| `Input` | Styled text input |
| `Label` | Form label |
| `Card`, `CardHeader`, `CardTitle`, `CardDescription`, `CardContent`, `CardFooter` | Card layout primitives |
| `Alert` | Alert/notification banner |
| `Separator` | Visual divider |
| `cn` | Tailwind class merge utility |

### API client

| Export | Description |
|---|---|
| `login(email, password)` | Password login |
| `logout()` | End session |
| `forgotPassword(email)` | Request password reset |
| `resetPassword(token, password)` | Complete password reset |
| `getSession()` | Check current session |
| `ssoCheck(email)` | Check SSO requirement for email domain |
| `getProviders()` | List external identity providers |
| `getPasswordPolicy()` | Fetch password rules |
| `ApiRequestError` | Error class with `.error`, `.retryAfter`, `.redirectUrl` |
| `mfaVerify(challengeId, method, code)` | Verify MFA challenge |
| `mfaStatus()` | Get enrolled MFA methods |
| `mfaTotpSetup()` | Start TOTP enrollment |
| `mfaTotpConfirm(setupToken, code)` | Confirm TOTP enrollment |
| `mfaWebAuthnSetup()` | Start WebAuthn/passkey enrollment |
| `mfaWebAuthnConfirm(setupToken, attestation)` | Confirm passkey enrollment |
| `mfaRecoveryGenerate()` | Generate recovery codes |
| `mfaDeleteCredential(credentialId)` | Remove an MFA credential |

### Branding

| Export | Description |
|---|---|
| `loadBranding()` | Fetch and parse `/branding.json` |
| `BrandingContext` | React context for branding config |
| `useBranding()` | Hook to read branding config |
| `resolveLocalized(value, lang)` | Resolve a `LocalizedString` for a language |

### i18n

| Export | Description |
|---|---|
| `i18n` | Pre-configured i18next instance |
| `useTranslation` | Re-exported from `react-i18next`, always import from this package to avoid context duplication |

### Types

```ts
type LocalizedString = string | Record<string, string> | null;

interface BrandingConfig {
  appName: string;
  logoUrl: string | null;
  primaryColor: string;
  supportEmail: string | null;
  showForgotPassword: boolean;
  showRegistration: boolean;
  customCssUrl: string | null;
  welcomeTitle: LocalizedString;
  welcomeSubtitle: LocalizedString;
  languages: { code: string; label: string }[] | null;
}

interface ExternalProvider {
  connectionId: string;
  name: string;
  loginUrl: string;
}

interface SessionResponse {
  authenticated: boolean;
  userId: string;
  email: string;
  name: string;
}

interface SsoCheckResponse {
  ssoRequired: boolean;
  providerType?: string;
  connectionId?: string;
  redirectUrl?: string;
}

interface PasswordPolicyRule {
  rule: string;
  value: number | null;
  label: string;
}

interface LoginResponse {
  userId?: string;
  email?: string;
  name?: string;
  mfaRequired?: boolean;
  challengeId?: string;
  methods?: string[];
  webAuthn?: object;
  mfaSetupRequired?: boolean;
  setupToken?: string;
  mfaAvailable?: boolean;
}

interface MfaStatusResponse {
  enabled: boolean;
  methods: { id: string; type: string; name: string; createdAt: string; lastUsedAt: string }[];
}

interface MfaTotpSetupResponse {
  setupToken: string;
  qrCodeDataUri: string;
  manualKey: string;
}
```

## License

[MIT](../../LICENSE)

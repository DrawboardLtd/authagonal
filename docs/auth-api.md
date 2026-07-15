---
layout: default
title: Auth API
---

# Auth API

These endpoints power the login SPA. They use cookie authentication (`SameSite=Lax`, `HttpOnly`).

If you're building a custom login UI, these are the endpoints you need to implement against.

## Endpoints

### Login

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Success (200):** Sets an auth cookie and returns:

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` is `true` when the client's `MfaPolicy` is `Enabled` but the user hasn't enrolled yet (the UI can offer setup); in that case a `clientId` field is also included.

**MFA required (200):** If the user has MFA enrolled, they are **always** challenged — regardless of the requesting client's `MfaPolicy` (MFA is a property of the user/session, not of the client):

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

The client should redirect to an MFA challenge page and call `POST /api/auth/mfa/verify`.

**MFA setup required (200):** If `MfaPolicy` is `Required` and the user has no MFA enrolled:

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

The client should redirect to an MFA setup page. The setup token authenticates the user to the MFA setup endpoints via the `X-MFA-Setup-Token` header.

**Error responses:**

| `error` | Status | Description |
|---|---|---|
| `invalid_credentials` | 401 | Wrong email or password. Deliberately identical for unknown emails (anti-enumeration). |
| `locked_out` | 423 | Too many failed attempts. `retryAfter` (seconds) is included. |
| `account_disabled` | 403 | Account is deactivated (only surfaced after a correct password) |
| `email_not_confirmed` | 403 | Email not yet verified (only surfaced after a correct password) |
| `sso_required` | 409 | Domain requires SSO. `redirectUrl` points to the SSO login. |
| `captcha_failed` | 400 | Turnstile verification failed (only when Turnstile is configured; requests then need a `turnstileToken` field) |
| `email_required` | 400 | Email field is empty |
| `password_required` | 400 | Password field is empty |

### Register

```
POST /api/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Creates a new user account and sends a verification email. Returns `201 { "success": true, "userId": "..." }`. Optional fields: `locale` (BCP-47 tag persisted on the user) and `customAttributes` (a string map).

Registration is deliberately **enumeration-neutral**: if the email is already registered, the response is the same neutral `201` (with a throwaway `userId`) and the real owner is emailed a sign-in/reset notice instead. Registration is also rate-limited per IP — `429 rate_limited` when exceeded (window and cap configurable via `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes`).

### Confirm Email

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

Confirms the user's email address using the token from the verification email. `GET` is the clickable email link — it redirects to `/login?email_confirmed=1` (plus a `continue_client` parameter when the registration originated from an OAuth flow). `POST` is the programmatic path and returns JSON (the token may also be supplied in a JSON body as `{ "token": "..." }`); the response includes an optional `appLink` ("continue to app" target).

### Providers

```
GET /api/auth/providers
```

Returns the list of configured external identity providers (for rendering SSO buttons):

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

Connections with `AllowedDomains` configured are **excluded** — those are reached email-first via `/api/auth/sso-check` instead of a button. `turnstileSiteKey` is set when Cloudflare Turnstile is configured (the login UI must then send a `turnstileToken` with login/register/password requests).

### Logout

```
POST /api/auth/logout
```

Clears the auth cookie. Returns `200 { success: true }`.

### Forgot Password

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Always returns `200` (anti-enumeration). If the user exists, sends a reset email.

### Reset Password

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | Description |
|---|---|
| `weak_password` | Doesn't meet strength requirements |
| `invalid_token` | Token is malformed |
| `token_expired` | Token has expired (default 60-minute validity, configurable via `Auth:PasswordResetExpiryMinutes`) |

### Session

```
GET /api/auth/session
```

Returns current session info if authenticated:

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Returns `401` if not authenticated.

### Apps

```
GET /api/auth/apps
```

Returns the tenant's application links for the account page's "back to app" launcher: enabled clients that have a home URI (`initiateLoginUri` preferred over `clientUri`). Each entry is `{ clientId, clientName, homeUri, logoUri, isDefault }`; exactly one app is marked default (the flagged client, or the only client with a home URI). Requires cookie auth.

### Profile (self-service)

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

The authenticated user reads/updates their own non-sensitive profile fields: `firstName`, `lastName`, `companyName`, `phone`, `locale`. Null fields are unchanged; email, password, roles, active state and organization are **not** editable here. Both return the profile `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`.

### SSO Check

```
GET /api/auth/sso-check?email=user@acme.com
```

Checks if the email domain requires SSO:

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

If SSO is not required:

```json
{
  "ssoRequired": false
}
```

### Password Policy

```
GET /api/auth/password-policy
```

Returns the server's password requirements (configured via `PasswordPolicy` in settings):

```json
{
  "rules": [
    { "rule": "minLength", "value": 8, "label": "At least 8 characters" },
    { "rule": "uppercase", "value": null, "label": "Uppercase letter" },
    { "rule": "lowercase", "value": null, "label": "Lowercase letter" },
    { "rule": "digit", "value": null, "label": "Number" },
    { "rule": "specialChar", "value": null, "label": "Special character" }
  ]
}
```

The default login UI fetches this endpoint on the reset-password page to display requirements dynamically.

## Default Password Requirements

With default configuration, passwords must meet all of these:

- At least 8 characters
- At least one uppercase letter
- At least one lowercase letter
- At least one digit
- At least one non-alphanumeric character
- At least 2 unique characters

These can be customized via the `PasswordPolicy` configuration section — see [Configuration](configuration).

## MFA Endpoints

### MFA Verify

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

Verifies an MFA challenge. On success, sets the auth cookie and returns user info.

**Methods:**

| `method` | Required fields | Description |
|---|---|---|
| `totp` | `code` (6 digits) | Time-based one-time password from authenticator app |
| `webauthn` | `assertion` (JSON string) | WebAuthn assertion response from `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | One-time recovery code (consumed on use) |

**Retry semantics:** a wrong code does **not** burn the challenge — the code is validated first and the challenge is consumed only on success, so the user can retry the same `challengeId` after a mistyped digit (`401 invalid_code` / `assertion_failed`). Each challenge tolerates **5 failed attempts**; the 5th failure consumes it and returns `401 too_many_attempts`, forcing a fresh login (this bounds TOTP brute-force to 5 guesses per challenge). Challenges also expire (default 5 minutes, `Auth:MfaChallengeExpiryMinutes`); an expired, unknown, or already-consumed `challengeId` returns `invalid_challenge`. TOTP codes are additionally replay-protected — a code from an already-used time step is rejected.

### MFA Status

```
GET /api/auth/mfa/status
```

Returns the user's enrolled MFA methods. Requires cookie auth or `X-MFA-Setup-Token` header.

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

`offered` is `false` when every client's `MfaPolicy` is `Disabled` — the tenant has MFA off, so the setup UI can hide itself. Recovery-code entries additionally carry `isConsumed`.

### TOTP Setup

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### WebAuthn / Passkey Setup

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

Passkey enrolment requires a **confirmed TOTP credential first** (`400 totp_required_first`) — passkeys are a per-device convenience layered on a portable base factor, so an account can never end up passkey-only and locked to a device. Users whose email domain is SSO-routed cannot enrol a local passkey (`400 sso_managed`) — it would bypass the tenant's IdP. A credential ID already registered to a different user is rejected with `409 credential_already_registered`.

### Recovery Codes

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Generates 10 one-time recovery codes. Requires at least one primary method (TOTP or WebAuthn) to be enrolled. Regenerating replaces all existing recovery codes.

### Remove MFA Credential

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Removes a specific MFA credential. If the last primary method is removed, MFA is disabled for the user. Requires a real cookie session — a setup token is rejected with `403 session_required` (setup tokens exist only to add a first factor, never to downgrade MFA).

### Passwordless Passkey Login

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

Discoverable-credential (resident passkey) login with no prior user context: `begin` issues an assertion challenge with an empty `allowCredentials` list, and `complete` resolves the user **from** the chosen passkey, verifies the assertion, and signs them in (the session carries the MFA marker — a passkey is phishing-resistant strong auth). If the resolved user's email domain is SSO-routed, the login is refused with `409 sso_required` + `redirectUrl` so a local passkey can't sidestep a forced IdP.

## Device Authorization (RFC 8628)

### Request Device Code

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

Returns a device code, user code, and verification URI:

```json
{
  "device_code": "abc123...",
  "user_code": "ABCD-EFGH",
  "verification_uri": "https://auth.example.com/device",
  "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
  "expires_in": 300,
  "interval": 5
}
```

`expires_in` comes from the client's `DeviceCodeLifetimeSeconds` (default 300). The device displays the `verification_uri` and `user_code` to the user, then polls the token endpoint with the `device_code` — no faster than `interval` seconds apart, or the token endpoint answers `slow_down` (RFC 8628 §3.5). While the user hasn't approved yet the token endpoint returns `authorization_pending`. The user visits the verification URI, logs in, and enters the user code to approve.

### Approve Device

```
POST /api/auth/device/approve
Content-Type: application/json

{
  "userCode": "ABCD-EFGH"
}
```

Requires cookie authentication. Approves the device code for the current user. The device can then exchange the device code for tokens via the token endpoint using grant type `urn:ietf:params:oauth:grant-type:device_code`.

## Token Introspection (RFC 7662)

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

Or with form-encoded credentials:

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

Returns token metadata:

```json
{
  "active": true,
  "sub": "user-id",
  "client_id": "my-app",
  "scope": "openid profile",
  "iss": "https://auth.example.com",
  "exp": 1234567890,
  "iat": 1234567890,
  "token_type": "Bearer"
}
```

Inactive or invalid tokens return `{ "active": false }`. Supports both JWT access tokens and opaque refresh tokens.

## Consent Endpoints

### Consent Info

```
GET /consent/info?client_id=my-app&scope=openid%20profile%20email
```

Returns client details and the requested scopes for the consent page (`scope` defaults to `openid` when omitted):

```json
{
  "clientId": "my-app",
  "clientName": "My Application",
  "description": null,
  "clientUri": null,
  "logoUri": null,
  "scopes": ["openid", "profile", "email"]
}
```

Returns `404 client_not_found` for an unknown client.

### Submit Consent

```
POST /consent
Content-Type: application/json

{
  "clientId": "my-app",
  "decision": "allow",
  "scopes": ["openid", "profile", "email"],
  "returnUrl": "/connect/authorize?..."
}
```

Records the user's consent decision (requires cookie auth) and returns `{ "redirect": "..." }` for the SPA to navigate to. On allow, the granted scopes are persisted (filtered to the client's `AllowedScopes` — a tampered body can't record scopes the client couldn't request) and the redirect points back to the authorize flow. On `"decision": "deny"`, the redirect points to the client's `redirect_uri` with an `access_denied` error.

### List Grants

```
GET /consent/grants
```

Returns all applications the user has authorized:

```json
[
  {
    "clientId": "my-app",
    "clientName": "My Application",
    "scopes": ["openid", "profile", "email"],
    "consentedAt": "2026-04-09T12:00:00Z"
  }
]
```

### Revoke Grant

```
DELETE /consent/grants/{clientId}
```

Revokes consent for a specific application. The user will be prompted to re-consent on their next login.

## Building a Custom Login UI

The default SPA (`login-app/`) is one implementation of this API. To build your own:

1. Serve your UI at the paths `/login`, `/forgot-password`, `/reset-password`, `/consent`, `/device`
2. The authorize endpoint redirects unauthenticated users to `/login?returnUrl={encoded-authorize-url}`
3. After successful login (cookie set), redirect the user to the `returnUrl`
4. Password reset links use `{Issuer}/login/reset-password?p={token}` (the login SPA is mounted under `/login`)

Your UI must be served from the **same origin** as the API because:
- Cookie auth uses `SameSite=Lax` + `HttpOnly`
- The authorize endpoint redirects to `/login` (relative)
- Reset links use `{Issuer}/login/reset-password`

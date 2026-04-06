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
  "name": "Jane Doe"
}
```

**MFA required (200):** If the user has MFA enrolled and the client's `MfaPolicy` is `Enabled` or `Required`:

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
| `invalid_credentials` | 401 | Wrong email or password |
| `locked_out` | 423 | Too many failed attempts. `retryAfter` (seconds) is included. |
| `email_not_confirmed` | 403 | Email not yet verified |
| `sso_required` | 409 | Domain requires SSO. `redirectUrl` points to the SSO login. |
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

Creates a new user account and sends a verification email. Returns `409` if the email is already registered.

### Confirm Email

```
POST /api/auth/confirm-email?token={token}
```

Confirms the user's email address using the token from the verification email.

### Providers

```
GET /api/auth/providers
```

Returns the list of configured external identity providers (for rendering SSO buttons):

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "loginUrl": "/oidc/google/login" }
  ]
}
```

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

### MFA Status

```
GET /api/auth/mfa/status
```

Returns the user's enrolled MFA methods. Requires cookie auth or `X-MFA-Setup-Token` header.

```json
{
  "enabled": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

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

Removes a specific MFA credential. If the last primary method is removed, MFA is disabled for the user.

## Building a Custom Login UI

The default SPA (`login-app/`) is one implementation of this API. To build your own:

1. Serve your UI at the paths `/login`, `/forgot-password`, `/reset-password`
2. The authorize endpoint redirects unauthenticated users to `/login?returnUrl={encoded-authorize-url}`
3. After successful login (cookie set), redirect the user to the `returnUrl`
4. Password reset links use `{Issuer}/reset-password?p={token}`

Your UI must be served from the **same origin** as the API because:
- Cookie auth uses `SameSite=Lax` + `HttpOnly`
- The authorize endpoint redirects to `/login` (relative)
- Reset links use `{Issuer}/reset-password`

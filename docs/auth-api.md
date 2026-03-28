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

**Error responses:**

| `error` | Status | Description |
|---|---|---|
| `invalid_credentials` | 401 | Wrong email or password |
| `locked_out` | 423 | Too many failed attempts. `retryAfter` (seconds) is included. |
| `email_not_confirmed` | 403 | Email not yet verified |
| `sso_required` | 403 | Domain requires SSO. `redirectUrl` points to the SSO login. |
| `email_required` | 400 | Email field is empty |
| `password_required` | 400 | Password field is empty |

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
| `token_expired` | Token has expired (24-hour validity) |

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

## Password Requirements

Passwords must meet all of these:

- At least 8 characters
- At least one uppercase letter
- At least one lowercase letter
- At least one digit
- At least one non-alphanumeric character
- At least 2 unique characters

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

[← Back to home](.)

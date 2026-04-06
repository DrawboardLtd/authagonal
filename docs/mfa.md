---
layout: default
title: Multi-Factor Authentication
---

# Multi-Factor Authentication (MFA)

Authagonal supports multi-factor authentication for password-based logins. Three methods are available: TOTP (authenticator apps), WebAuthn/passkeys (hardware keys and biometrics), and one-time recovery codes.

Federated logins (SAML/OIDC) skip MFA — the external identity provider handles second-factor authentication.

## Supported Methods

| Method | Description |
|---|---|
| **TOTP** | Time-based one-time passwords (RFC 6238). Works with any authenticator app — Google Authenticator, Authy, 1Password, etc. |
| **WebAuthn / Passkeys** | FIDO2 hardware security keys, platform biometrics (Touch ID, Windows Hello), and synced passkeys. |
| **Recovery codes** | 10 one-time backup codes (`XXXX-XXXX` format) for account recovery when other methods aren't available. |

## MFA Policy

MFA enforcement is configured **per-client** via the `MfaPolicy` property in `appsettings.json`:

| Value | Behavior |
|---|---|
| `Disabled` (default) | No MFA challenge, even if the user has MFA enrolled |
| `Enabled` | Challenge users who have MFA enrolled; don't force enrollment |
| `Required` | Challenge enrolled users; force enrollment for users without MFA |

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "MfaPolicy": "Enabled"
    },
    {
      "ClientId": "admin-portal",
      "MfaPolicy": "Required"
    }
  ]
}
```

The default is `Disabled`, so existing clients are unaffected until you opt in.

### Per-User Override

Implement `IAuthHook.ResolveMfaPolicyAsync` to override the client policy for specific users:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    // Exempt service accounts
    if (email.EndsWith("@service.internal"))
        return Task.FromResult(MfaPolicy.Disabled);

    return Task.FromResult(clientPolicy);
}
```

See [Extensibility](extensibility) for full hook documentation.

## Login Flow

The login flow with MFA works as follows:

1. User submits email and password to `POST /api/auth/login`
2. Server verifies password, then resolves the effective MFA policy
3. Based on the policy and the user's enrollment status:

| Policy | User has MFA? | Result |
|---|---|---|
| `Disabled` | — | Cookie set, login complete |
| `Enabled` | No | Cookie set, login complete |
| `Enabled` | Yes | Returns `mfaRequired` — user must verify |
| `Required` | No | Returns `mfaSetupRequired` — user must enroll |
| `Required` | Yes | Returns `mfaRequired` — user must verify |

### MFA Challenge

When `mfaRequired` is returned, the login response includes a `challengeId` and the user's available methods. The client redirects to an MFA challenge page where the user verifies with one of their enrolled methods via `POST /api/auth/mfa/verify`.

Challenges expire after 5 minutes and are single-use.

### Forced Enrollment

When `mfaSetupRequired` is returned, the response includes a `setupToken`. This token authenticates the user to the MFA setup endpoints (via the `X-MFA-Setup-Token` header) so they can enroll a method before getting a cookie session.

## Enrolling MFA

Users enroll MFA through the self-service setup endpoints. These require either an authenticated cookie session or a setup token.

### TOTP Setup

1. Call `POST /api/auth/mfa/totp/setup` — returns a QR code (`data:image/png;base64,...`), a `manualKey` (Base32 for manual entry), and setup token
2. User scans the QR code with their authenticator app
3. User enters the 6-digit code to confirm: `POST /api/auth/mfa/totp/confirm`

### WebAuthn / Passkey Setup

1. Call `POST /api/auth/mfa/webauthn/setup` — returns `PublicKeyCredentialCreationOptions`
2. Client calls `navigator.credentials.create()` with the options
3. Send the attestation response to `POST /api/auth/mfa/webauthn/confirm`

### Recovery Codes

Call `POST /api/auth/mfa/recovery/generate` to generate 10 one-time codes. At least one primary method (TOTP or WebAuthn) must be enrolled first.

Regenerating codes replaces all existing recovery codes. Each code can only be used once.

## Managing MFA

### User Self-Service

- `GET /api/auth/mfa/status` — view enrolled methods
- `DELETE /api/auth/mfa/credentials/{id}` — remove a specific credential

If the last primary method is removed, MFA is disabled for the user.

### Admin API

Administrators can manage MFA for any user via the [Admin API](admin-api):

- `GET /api/v1/profile/{userId}/mfa` — view a user's MFA status
- `DELETE /api/v1/profile/{userId}/mfa` — reset all MFA (for locked-out users)
- `DELETE /api/v1/profile/{userId}/mfa/{id}` — remove a specific credential

### Audit Hook

Implement `IAuthHook.OnMfaVerifiedAsync` to log MFA events:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

## Custom Login UI

If you're building a custom login UI, handle these responses from `POST /api/auth/login`:

1. **Normal login** — `{ userId, email, name }` with cookie set. Redirect to `returnUrl`.
2. **MFA required** — `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Show MFA challenge form.
3. **MFA setup required** — `{ mfaSetupRequired: true, setupToken }`. Show MFA enrollment flow.

See [Auth API](auth-api) for the full endpoint reference.

---
layout: default
title: Multi-Factor Authentication
---

# Multi-Factor Authentication (MFA)

Authagonal supports multi-factor authentication. Three methods are available: TOTP (authenticator apps), WebAuthn/passkeys (hardware keys and biometrics), and one-time recovery codes. Passkeys can also be used for [passwordless login](#passwordless-passkey-login).

Federated logins (SAML/OIDC) are covered too: a SAML or OIDC assertion proves the first factor, not the second. A federated user with MFA enrolled is routed through the same local MFA challenge as a password login, and a `Required` policy forces enrollment before any session is issued. Only when MFA is neither enrolled nor required does federation stand alone.

## Supported Methods

| Method | Description |
|---|---|
| **TOTP** | Time-based one-time passwords (RFC 6238): 6 digits, 30-second step, SHA-1, verified with a one-step clock-skew window. Works with any authenticator app (Google Authenticator, Authy, 1Password, etc.). A code that has already been accepted cannot be replayed within its validity window. |
| **WebAuthn / Passkeys** | FIDO2 hardware security keys, platform biometrics (Touch ID, Windows Hello), and synced passkeys. Users can register multiple passkeys, and passkeys can sign in passwordless. |
| **Recovery codes** | 10 one-time backup codes (`XXXX-XXXX` format) for account recovery when other methods aren't available. Stored hashed and encrypted at rest. |

## MFA Policy

MFA enforcement is configured **per-client** via the `MfaPolicy` property in `appsettings.json`:

| Value | Behavior |
|---|---|
| `Disabled` (default) | Don't force enrollment; the self-service setup UI hides MFA when every client is `Disabled` |
| `Enabled` | Offer MFA enrollment; don't force it |
| `Required` | Force enrollment for users without MFA |

A user who has MFA enrolled is **always challenged at login, regardless of the client policy**. MFA is a property of the user and their session, not of the requesting client, so a request routed through a `Disabled` client cannot be used to skip an enrolled user's second factor.

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

The resolved policy governs enrollment (whether it is offered or forced). It does not exempt an already-enrolled user from the challenge; enrolled users are always challenged.

See [Extensibility](extensibility) for full hook documentation.

## Login Flow

The login flow with MFA works as follows:

1. User submits email and password to `POST /api/auth/login`
2. Server verifies password, then resolves the effective MFA policy
3. Based on the policy and the user's enrollment status:

| Policy | User has MFA? | Result |
|---|---|---|
| Any | Yes | Returns `mfaRequired`: user must verify |
| `Disabled` / `Enabled` | No | Cookie set, login complete |
| `Required` | No | Returns `mfaSetupRequired`: user must enroll |

### MFA Challenge

When `mfaRequired` is returned, the login response includes a `challengeId`, the user's available `methods`, and (when the user has passkeys) `webAuthn` assertion options. The client redirects to an MFA challenge page where the user verifies with one of their enrolled methods via `POST /api/auth/mfa/verify`:

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` is `totp`, `recovery`, or `webauthn` (WebAuthn sends an `assertion` instead of a `code`).

Challenges expire after 5 minutes (configurable via `Auth:MfaChallengeExpiryMinutes`) and are consumed on successful verification.

#### Retry Budget

A wrong code does not burn the challenge. The verify endpoint validates the code first and consumes the challenge only on success, so a mistyped TOTP digit can simply be retried against the same `challengeId`. Failed attempts return `invalid_code` (or `assertion_failed` for WebAuthn) with a 401 and increment a bounded counter on the challenge; the fifth wrong attempt consumes the challenge and returns `too_many_attempts`, forcing a fresh login. This applies to all three methods and bounds TOTP brute-force to 5 guesses per challenge.

A missing, expired, or already-consumed challenge returns `invalid_challenge`.

### Federated Logins

After a successful SAML or OIDC assertion, the server resolves the same effective MFA policy. An MFA-enrolled user is redirected to the hosted MFA challenge page (with a `challengeId`) instead of receiving a session; a user without MFA under a `Required` policy is redirected to the MFA setup page (with a `setupToken`). The session is only marked MFA-authenticated once verification completes.

### Forced Enrollment

When `mfaSetupRequired` is returned, the response includes a `setupToken`. This token authenticates the user to the MFA setup endpoints (via the `X-MFA-Setup-Token` header) so they can enroll a method before getting a cookie session. Setup tokens expire after 15 minutes (configurable via `Auth:MfaSetupTokenExpiryMinutes`).

## Enrolling MFA

Users enroll MFA through the self-service setup endpoints. These require either an authenticated cookie session or a setup token.

### TOTP Setup

1. Call `POST /api/auth/mfa/totp/setup`, returns a QR code (`data:image/png;base64,...`), a `manualKey` (Base32 for manual entry), and setup token
2. User scans the QR code with their authenticator app
3. User enters the 6-digit code to confirm: `POST /api/auth/mfa/totp/confirm`

### WebAuthn / Passkey Setup

1. Call `POST /api/auth/mfa/webauthn/setup`, returns a `setupToken` and `PublicKeyCredentialCreationOptions`
2. Client calls `navigator.credentials.create()` with the options
3. Send the attestation response to `POST /api/auth/mfa/webauthn/confirm`

Passkey enrollment requires a confirmed TOTP credential first (`totp_required_first`). Passkeys are a per-device convenience layered on top of a portable base factor, so every account keeps a device-independent factor and a `Required` policy can't be satisfied by a passkey alone.

Users can register multiple passkeys (one per device). A credential ID already registered — to any account, including the enrolling user's own — is rejected with `credential_already_registered` (409). Re-enrolling an authenticator that is already enrolled would create a second credential row sharing one credential ID: its signature counter would restart, weakening clone detection, and deleting either row would remove the lookup entry both depend on. The lookup entry is claimed with an insert-if-absent write, so two registrations of the same credential ID cannot both succeed. Users whose email domain is routed to an external IdP via forced SSO cannot enroll a local passkey (`sso_managed`), since it would bypass the IdP and its deprovisioning.

### Relying-party host

The FIDO2 relying-party ID and origin are resolved per request from the host, so each tenant hostname is its own relying party. Set `Auth:WebAuthnAllowedHosts` to the hostnames you serve, so a host outside that list cannot act as a relying party. An empty list (the default) keeps the previous behaviour rather than locking out existing passkey users on upgrade, and is logged as a gap — it is not a safe resting place. Setting `AllowedHosts` in `appsettings.json` as well, so ASP.NET Core's host filtering rejects unrecognised `Host` headers before any handler runs, is the cheaper outer layer.

### Recovery Codes

Call `POST /api/auth/mfa/recovery/generate` to generate 10 one-time codes. At least one primary method (TOTP or WebAuthn) must be enrolled first.

Regenerating codes replaces all existing recovery codes. Each code can only be used once; a redeemed code is marked consumed and no longer accepted.

Codes are never stored in plaintext: each code is hashed, and the hash is additionally encrypted at rest with the tenant's secret provider, so a storage dump yields ciphertext rather than an offline-brute-forceable hash.

## Passwordless Passkey Login

Passkeys aren't just a second factor: a user with an enrolled passkey can sign in without a password.

1. `POST /api/auth/mfa/passwordless/begin` returns a `challengeId` and assertion `options` for discoverable credentials, so the authenticator offers any resident passkey for the site
2. Client calls `navigator.credentials.get()` with the options
3. `POST /api/auth/mfa/passwordless/complete` with `{ challengeId, assertion }`: the server resolves the user from the passkey itself and signs them in

The hosted login page wires this into the email field via conditional mediation (passkey autofill): when the browser supports it, an available passkey is offered as an autofill suggestion without any extra UI.

A passkey is phishing-resistant strong auth, so the resulting session carries the MFA marker and is not re-challenged. If the user's email domain is routed to an external IdP via forced SSO, passwordless login is refused with a 409 `sso_required` response that includes the SSO redirect URL, so a local passkey can't sidestep the IdP.

## Managing MFA

### User Self-Service

- `GET /api/auth/mfa/status`, view enrolled methods (also reports whether MFA is offered by any client)
- `DELETE /api/auth/mfa/credentials/{id}`, remove a specific credential

Removing a credential requires a real authenticated session; a setup token only authorizes adding a first factor and gets `session_required` here, so a leaked setup token can't downgrade a user's MFA.

If the last primary method is removed, MFA is disabled for the user.

### Admin API

Administrators can manage MFA for any user via the [Admin API](admin-api):

- `GET /api/v1/profile/{userId}/mfa`, view a user's MFA status
- `DELETE /api/v1/profile/{userId}/mfa`, reset all MFA (for locked-out users)
- `DELETE /api/v1/profile/{userId}/mfa/{id}`, remove a specific credential

### Audit Hooks

Implement `IAuthHook.OnMfaVerifiedAsync` to log MFA events:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

The full MFA lifecycle is hookable: `OnMfaVerifyFailedAsync` (a failed verify attempt), `OnMfaEnrolledAsync` (a method confirmed), `OnMfaCredentialRemovedAsync` (a credential removed, with a flag for whether that disabled MFA), and `OnRecoveryCodesRegeneratedAsync`.

## Custom Login UI

If you're building a custom login UI, handle these responses from `POST /api/auth/login`:

1. **Normal login**: `{ userId, email, name }` with cookie set. Redirect to `returnUrl`.
2. **MFA required**: `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Show MFA challenge form.
3. **MFA setup required**: `{ mfaSetupRequired: true, setupToken }`. Show MFA enrollment flow.

When handling `POST /api/auth/mfa/verify` errors: `invalid_code` and `assertion_failed` are retryable against the same `challengeId` (up to the attempt budget); `too_many_attempts` and `invalid_challenge` are terminal, so send the user back to the sign-in form.

See [Auth API](auth-api) for the full endpoint reference.

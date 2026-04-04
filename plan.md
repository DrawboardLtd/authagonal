# Authagonal — Ship-to-Production Plan

Replacement for Duende IdentityServer + Sustainsys.Saml2, backed by Azure Table Storage.
Architecture: **API-only server + React client app** for login UI. No Razor Pages.

---

## Architecture Decision: API + React

The login/password-reset UI is a React SPA. The server is pure API. The auth flow:

```
/connect/authorize
  → user not authenticated
  → 302 {LoginAppUrl}?returnUrl={encoded authorize URL}
  → React SPA renders login form
  → POST /api/auth/login (JSON, returns Set-Cookie)
  → React redirects to returnUrl
  → /connect/authorize (now has cookie)
  → 302 {redirect_uri}?code=...&state=...
```

**Server auth API endpoints** (replace Razor Pages):

```
POST /api/auth/login               { email, password } → 200 + Set-Cookie / 401 / 423
POST /api/auth/logout              → clear cookie + 200
POST /api/auth/forgot-password     { email } → 200 always (anti-enumeration)
POST /api/auth/reset-password      { token, newPassword } → 200 / 400
GET  /api/auth/session             → { authenticated, userId, email, name } / 401
GET  /api/auth/sso-check           ?email=x → { ssoRequired, redirectUrl } (SSO domain routing)
```

**React app** (separate project, served as static files):

```
/login              email + password form, SSO domain check on email blur
/forgot-password    email form
/reset-password     new password form (token from ?p= query param)
```

**CSRF approach**: `SameSite=Lax` cookie + CORS origin whitelist. No antiforgery tokens needed.

---

## Architecture Decision: Homebrew SAML

No third-party SAML library. Custom SP implementation using `System.Security.Cryptography.Xml.SignedXml` (built into .NET).

**Scope**: SP-initiated SSO only. No SAML logout. No encryption. No IdP-initiated SSO. No artifact binding.

**Azure AD full support** (primary target):

| Azure AD Behavior | Our Handling |
|---|---|
| Signs **assertion only** (default) | Validate signature on Assertion element |
| Signs **response only** | Validate signature on Response element |
| Signs **both** | Validate both signatures |
| SHA-256 (default) | Support SHA-256 and SHA-1 |
| NameID: persistent (opaque pairwise) | Accept any format, prefer emailAddress |
| NameID: emailAddress | Direct email extraction |
| NameID: transient, unspecified | Fall back to email claim from AttributeStatement |
| Issuer: `https://sts.windows.net/{tenantId}/` | Store and validate per-provider |
| Claims: full URI names | Map standard URIs to simple names |
| Assertion validity: 70 min from NotBefore | Validate NotBefore/NotOnOrAfter with 5-min skew |
| Metadata: XML at well-known URL | Parse on provider create, cache signing certs |
| Encryption: opt-in (needs SP cert) | Don't publish encryption cert → never encrypted |

**Claim mapping** (Azure AD URI → simple name):

```
http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress    → email
http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name            → name (UPN)
http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname       → firstName
http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname         → lastName
http://schemas.microsoft.com/identity/claims/objectidentifier         → oid
http://schemas.microsoft.com/identity/claims/tenantid                 → tenantId
http://schemas.microsoft.com/identity/claims/displayname              → displayName
```

---

## Architecture Decision: User Provisioning Webhook

Authagonal is part of a larger system. When users are created (via SAML, OIDC, or admin API), the downstream backend must be notified and can approve/reject account creation. This solves the problem of SAML/OIDC SSO silently creating accounts that the consuming system doesn't know about.

**Flow**:

```
New user detected (SAML ACS / OIDC callback / admin API)
  → POST {Provisioning:WebhookUrl}/users/provision
    Authorization: Bearer {Provisioning:ApiKey}
    Body: { email, firstName, lastName, provider, providerKey, organizationId }
  ← 200 { approved: true, userId: "...", organizationId: "..." }  → create user with that ID + org
  ← 200 { approved: false, reason: "..." }                        → reject, don't create user
  ← non-2xx or error body                                         → reject with friendly message
  (no webhook configured)                                         → auto-approve with new GUID
```

**On user deletion**: `DELETE {WebhookUrl}/users/{userId}` (best-effort notification).

**Session invalidation on org change**: When `OrganizationId` is updated via admin API:
1. `SecurityStamp` is rotated → existing cookie sessions are invalidated (checked every 30 mins via `OnValidatePrincipal`)
2. All refresh tokens are revoked → clients must re-authenticate
3. Existing access tokens (JWTs) expire naturally within their TTL

The `security_stamp` claim is stored in all cookie sessions. The cookie auth middleware periodically validates it against the DB. If the stamp has changed (org change, password reset, etc.), the cookie is rejected and the user must re-authenticate. New tokens minted via refresh always read fresh user state from the store.

---

## Current State

### Complete (compiling, all files written)

| Component | Key Files | Notes |
|---|---|---|
| **OIDC Provider** | DiscoveryEndpoint, JwksEndpoint, AuthorizeEndpoint, TokenEndpoint, UserinfoEndpoint, RevocationEndpoint, EndSessionEndpoint | authorization_code+PKCE, client_credentials, refresh_token grants |
| **Token Service** | TokenService.cs | JWT creation (access, id, refresh), PKCE validation, one-time refresh rotation with 60s grace window, replay detection |
| **Key Management** | KeyManager.cs | RSA 2048, 90-day auto-rotation, in-memory cache, JWKS serving |
| **Password Hashing** | PasswordHasher.cs | PBKDF2 (SHA256, 100k iter) + BCrypt migration support |
| **Password Validation** | PasswordValidator.cs | Min 8, upper, lower, digit, non-alphanumeric, 2+ unique chars |
| **Azure Table Storage** | 10 entity types, 7 store implementations | Users, UserEmails, UserLogins, Clients, Grants, GrantsBySubject, SigningKeys, SsoDomains, SamlProviders, OidcProviders |
| **Email Service** | EmailService.cs | SendGrid dynamic templates, skips @example.com |
| **Auth API** | AuthEndpoints.cs | Login (SSO check, lockout, BCrypt rehash), logout, forgot-password, reset-password, session, sso-check |
| **SAML SP (homebrew)** | SamlRequestBuilder, SamlResponseParser, SamlMetadataParser, SamlReplayCache, SamlClaimMapper, SamlConstants | Full Azure AD support — signed response/assertion/both, wrapping attack prevention, all NameID formats |
| **SAML Endpoints** | SamlEndpoints.cs | SP-initiated login, ACS with user provisioning, SP metadata |
| **Dynamic OIDC Federation** | OidcDiscoveryClient, OidcStateStore, OidcEndpoints.cs | PKCE, id_token validation, nonce check, userinfo fallback, Azure AD emails array handling, user provisioning |
| **User Provisioning** | IUserProvisioningService, UserProvisioningService | Webhook-based approve/reject for all user creation paths (SAML, OIDC, admin API). Deletion notification. |
| **Session Invalidation** | Program.cs (OnValidatePrincipal) | SecurityStamp stored in cookie, validated every 30 mins against DB. Org change rotates stamp + revokes all refresh tokens. |
| **Admin APIs** | UserEndpoints, SsoEndpoints, TokenEndpoints | User CRUD (with provisioning), SAML/OIDC provider CRUD with domain routing, token impersonation |
| **Dynamic CORS** | DynamicCorsPolicyProvider.cs | Combines static config + all client AllowedCorsOrigins, 60-min cache |
| **Health Check** | TableStorageHealthCheck.cs | Table Storage connectivity via signing key read, 5s timeout |
| **Token Cleanup** | TokenCleanupService.cs | BackgroundService, 5-min initial delay, 60-min interval, removes expired grants |
| **Middleware** | ExceptionHandlingMiddleware | Correlation IDs, structured JSON error responses |
| **Auth Pipeline** | Program.cs | Cookie auth (48hr sliding + stamp validation) + JWT bearer with dynamic key resolver, IdentityAdmin policy, CORS |
| **React Login App** | LoginPage, ForgotPasswordPage, ResetPasswordPage, api.ts, types.ts | TypeScript, Vite, SSO check on email blur, password strength indicator, login_hint support |
| **Client Seeding** | ClientSeedService.cs | IHostedService, reads `Clients` array from appsettings, upserts on startup. Idempotent. |
| **Migration Tool** | tools/Authagonal.Migration | Console app. SQL Server → Table Storage: users (+claims join), external logins, SAML/OIDC providers + SSO domains, Duende clients (+child tables), refresh tokens (opt-in). Dry run mode. Idempotent. |

### To Build

| Component | Severity | Notes |
|---|---|---|
| Rate limiting | **Low** | Login and token endpoints |

---

## Remaining Phases

### Phase 4 — Client Configuration & Seeding

#### 4.1 Client Seed from appsettings — DONE

- `ClientSeedService : IHostedService` reads `Clients` array from config, upserts via `IClientStore`
- Idempotent — safe on every startup
- Config uses clean field names (`Id`, `Name`, `SecretHashes`, `GrantTypes`, `Scopes`, `CorsOrigins`, `RequireSecret`)

#### 4.2 Port Existing Client Configs

- Extract from existing Duende appsettings (dev, staging, prod)
- Key clients: web app, Zendesk (AlwaysIncludeUserClaimsInIdToken=true), mobile, service-to-service
- Verify redirect URIs, scopes, CORS origins per environment

---

### Phase 5 — Data Migration — DONE

Single console app `tools/Authagonal.Migration` handles all migration in one run:

```
dotnet run --project tools/Authagonal.Migration -- \
  --Source:ConnectionString "Server=...;Database=...;..." \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] [--MigrateRefreshTokens true]
```

| Step | Source | Target | Notes |
|---|---|---|---|
| Users | AspNetUsers + AspNetUserClaims (given_name, family_name, company, org_id) | Users + UserEmails | Single JOIN query, password hashes kept as-is (BCrypt auto-upgrades on login) |
| External logins | AspNetUserLogins | UserLogins (forward + reverse) | 409 = skip |
| SAML providers | SamlProviderConfigurations | SamlProviders + SsoDomains | AllowedDomains CSV split into individual SSO domain records |
| OIDC providers | OidcProviderConfigurations | OidcProviders + SsoDomains | Same domain splitting |
| Clients | Duende Clients + ClientSecrets, ClientGrantTypes, ClientRedirectUris, ClientPostLogoutRedirectUris, ClientScopes, ClientCorsOrigins | Clients | All child tables loaded per client |
| Refresh tokens | PersistedGrants (type=refresh_token, not expired) | Grants + GrantsBySubject | Opt-in via `--MigrateRefreshTokens true`. If skipped, users re-login. |

#### 5.4 Signing Key Migration — TODO

- Export RSA key from Duende appsettings (`SigningCertificate` / `SigningRolloverCertificate`, Base64 PKCS8) → import into SigningKeys table
- Must be done close to cutover so existing tokens remain valid

---

### Phase 6 — Integration Testing

#### 6.1 Automated Test Suite

- **Auth Code + PKCE**: full round-trip including login API
- **Client Credentials**: POST /connect/token, validate response
- **Refresh Token**: rotation, grace window, replay detection
- **SAML SP-initiated**: mock IdP or Azure AD test tenant
  - Validate AuthnRequest format
  - Validate Response parsing with signed assertion
  - Validate Response parsing with signed response
  - Validate Response parsing with both signed
  - Validate replay rejection (reused InResponseTo)
  - Validate clock skew handling
  - Validate claim extraction (all Azure AD claim URIs)
- **OIDC Federation**: mock IdP or real Google/Apple test app
- **User Provisioning**: webhook approve, reject, unavailable, no-webhook-configured
- **Session Invalidation**: org change → stamp rotation → cookie rejected, refresh fails
- **Discovery + JWKS**: spec compliance
- **Password Reset**: full flow with mocked email
- **Login API**: all error cases (invalid creds, lockout, SSO redirect, email not confirmed)
- **Session**: cookie lifecycle, sliding expiration, stamp validation

#### 6.2 Compatibility Testing

- Point existing web app at Authagonal in test environment
- Verify: login, token refresh, logout, Zendesk SSO, mobile flows
- Configure Azure AD SAML app → test full SSO flow
- Verify provisioning webhook integration with Bullclip backend

#### 6.3 Load Testing

- Token endpoint: 100 req/s target
- SAML ACS endpoint: 50 req/s target
- Table Storage latency: p99 < 100ms for point reads

---

### Phase 7 — Deployment

#### 7.1 Azure Infrastructure

- **Storage Account**: Standard LRS — ~$0.50-2/month
- **App Service / Container App**: B1 ($13/mo) or consumption ($0 at rest)
- **Static Web App or Blob Storage + CDN**: React login app — ~$1/month
- **Total**: ~$15-20/month

#### 7.2 CI/CD

- Build → Test → Publish → Deploy staging → Smoke test → Swap to production
- Separate pipelines for API server and React app

#### 7.3 Monitoring

- Application Insights or Seq
- Alerts: failed logins, token errors, key rotation failures, SAML signature failures, provisioning webhook failures
- Dashboard: sessions, token issuance rate, SAML/OIDC success rate, provisioning approve/reject rate

---

### Phase 8 — Cutover

#### 8.1 Pre-Cutover

1. Run user migration — can be done days ahead
2. Run provider migration
3. Import signing key — close to cutover
4. Seed client configs
5. Configure provisioning webhook URL in Authagonal + implement webhook handler in Bullclip backend
6. Deploy Authagonal to staging, full test suite
7. Optional: migrate active refresh tokens

#### 8.2 Cutover (< 5 min downtime)

1. Maintenance mode on existing IdentityServer
2. Final migration delta
3. DNS switch
4. Smoke test
5. Monitor 30 minutes

#### 8.3 Rollback

- DNS TTL 60s before cutover
- Shared signing key → tokens valid on both systems
- Switch DNS back if issues

#### 8.4 Post-Cutover

- Monitor 1 week
- Decommission Duende infrastructure
- Cancel Duende license

---

## Phase Sequencing

```
Phases 1-3 (Complete) ────→ Phase 6 (Testing) ─→ Phase 7 (Deploy) ─→ Phase 8 (Cutover)
                              ↑
Phase 4 (Client Config) ─────┘
                              ↑
Phase 5 (Migration) ──────────────────────────────────────────────→ Phase 8 (Cutover)
```

Phases 1-3 are complete. Phase 4 (client seeding) and Phase 5 (migration) are independent and can start now. Phase 6 depends on Phase 4 for full testing. Phase 5 is independent until cutover.

---

## What We're Not Building

- Introspection endpoint (unnecessary for JWT access tokens)
- Device authorization grant (RFC 8628)
- CIBA (Client Initiated Backchannel Authentication)
- Dynamic client registration (RFC 7591)
- Pushed Authorization Requests (RFC 9126)
- Mutual-TLS client auth (RFC 8705)
- Consent screens (RequireConsent = false on all clients)
- Resource indicators (RFC 8707)
- Back-channel logout
- SAML IdP functionality (we're SP only)
- SAML logout (use session timeout)
- SAML assertion decryption (don't publish encryption cert)
- SAML artifact binding
- IdP-initiated SSO

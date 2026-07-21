# Changelog

## [Unreleased]

## [0.10.5], 2026-07-21

### Fixed

- **Bff: array claims survive `/bff/user`** — repeated id_token claim types (`roles`, `groups`)
  are now space-joined into the session claim map; previously only the first value survived, so a
  multi-role user lost everything after their first role.

## [0.10.4], 2026-07-21

### Changed

- **`OidcSubject` is now a `record`** (was a sealed class; same shape, adds value equality). Lets a
  host DECORATE the registered `IOidcSubjectResolver` and overlay fields with a `with` expression —
  e.g. enriching subjects with live org context from an external system of record on every
  authorize/refresh — without hand-copying (and silently dropping) the remaining fields.

## [0.10.3], 2026-07-21

### Added

- **Per-connection MFA challenge control** (`ChallengeMfaAfterLogin`, SAML + OIDC connections,
  default `true`): whether users are still routed through the LOCAL MFA challenge after this
  connection authenticates them (F42). `false` = the tenant trusts the upstream IdP's own MFA as
  the second factor — federation signs the session in `mfa_authenticated` without a local
  challenge. Threaded through config seed, admin create/update DTOs and the Azure/Dynamo stores
  (persisted in the negative, so pre-existing rows keep challenging).
- **`Scopes[]` config seeding** (`ScopeSeedService`): register custom scopes (name, display name,
  `UserClaims`, discovery visibility) from configuration at startup, mirroring `Clients[]` — they
  appear in `scopes_supported` and are ready for per-scope claim release.
- **`BackchannelLogoutUri` on `Clients[]` seed entries**: config-seeded clients can now register a
  back-channel logout endpoint (previously only dynamic registration could), e.g. a BFF's
  `/bff/backchannel-logout`.

## [0.10.2], 2026-07-21

### Added

- **Bff: websocket tickets** (`AuthagonalBffOptions.WsTicketsEnabled`, off by default): `GET
  {BasePath}/ws-ticket` mints a short-lived (default 30s), single-use ticket bound to the session's
  refreshed access token, stored in the shared distributed cache under `agbff:wst:{ticket}`. A browser
  websocket handshake can't carry a bearer or custom headers, so the SPA fetches a ticket (anti-forgery
  header required), puts it on the connect URL, and the API host exchanges + deletes the cache key to
  authenticate the socket. Requires a shared cache (Redis) — the in-memory default can't serve another
  process. The ticket must never be written to client storage: fetch, connect, drop.

## [0.7.11], 2026-07-16

### Added

- **`ShowOnLogin`** on OIDC federation connections (default `true`): when `false`, the connection is
  reached only via an explicit `idp_hint` and is **not** rendered as a "Continue with {name}" button on
  the login page. For a bounded, machine-triggered connection such as a share-link / guest-OIDC provider
  a login button makes no sense. Threaded through config seed + the Azure Table store; persisted in the
  negative (`HiddenFromLogin`) so existing stored connections default to shown. The `/api/auth/providers`
  payload now filters on it alongside the existing domain-routed check.

## [0.7.10], 2026-07-16

### Added

- **`UseUpstreamSubjectAsUserId`** on OIDC federation connections: when set, a JIT-provisioned federated
  user's local id is the upstream `sub` rather than a fresh GUID, so a trusted first-party connection
  (e.g. a share-link / guest-OIDC provider) keeps the local `sub` equal to the downstream RP's own user
  id. Threaded through config seed + Azure/Dynamo stores. Do NOT enable for arbitrary external IdPs.
- OIDC provider config seed now honours `JitProvisioningEnabled`, `UseUpstreamSubjectAsUserId`,
  `PassthroughParams` and `SessionExpClaim` (previously dropped by `ProviderSeedService`).

### Changed

- **JIT provisioning is now OFF by default.** Renamed the per-connection `DisableJitProvisioning`
  (negative sense) to `JitProvisioningEnabled` (positive) on `OidcProviderConfig` / `SamlProviderConfig`,
  keeping `DisableJitProvisioning` as an inverting alias for back-compat. The stored column/blob is
  unchanged, so existing STORED connections keep their behaviour (an old `DisableJitProvisioning=false`
  reads back as JIT-on) — only the C# default flips, which affects config-seeded connections. A
  connection must now explicitly opt in to auto-provisioning of unknown federated users.

## [0.7.9], 2026-07-16

### Added

- **AWS provider parity**: `DynamoUserStore` now implements the full modern `IUserStore` surface: PII document encryption + blind-index lookup keys via the `IFieldCipher`/`IIndexTokenizer` seams (with plaintext dual-read and reindex/migration backfills), native cursor paging, id/login-state enumeration for retention sweeps, email-domain search, and attribute-only login stamps. New `AddAuthagonalAwsStorage(...)` one-call composition (DynamoDB + Secrets Manager + S3 DataProtection). The AWS lane is now covered by DynamoDB-Local integration tests (stores, crypto, clustering).
- **Email auto-wire**: the built-in Resend sender activates when `Email:ResendApiKey` is configured; `UseAuthagonal` warns at startup when mail would be discarded while the confirmed-email login gate is on.

### Fixed

- **Back-channel logout never notified relying parties**: the logout-token `events` claim used an anonymous object, which the JWT handler cannot serialize (IDX11025); the per-client catch swallowed the failure, so every RP was counted as failed and no request was ever sent. Both the internal fan-out and end-session mints are fixed and now covered by tests that observe the real HTTP fan-out.
- **Admin token mint issued a refresh token unconditionally**: `POST /api/v1/token` now issues one only when `offline_access` is requested and the client allows offline access.
- `appsettings.json` shipped dead SendGrid email keys; replaced with the real `Email:ResendApiKey`/`SenderEmail`/`SenderName`.

### Documentation

- Full refresh of the GitHub Pages docs against the current source, with the CHANGELOG backfilled from 0.4.1 and the dead `Authagonal.Storage` package references corrected to `Authagonal.AzureProvider`.
- All six non-English locales (de, es, fr, pt, vi, zh-Hans) brought to full parity with the English docs.

## [0.7.7] – [0.7.8], 2026-07-15

### Fixed

- **MFA challenge burn**: a wrong TOTP/recovery/WebAuthn code no longer consumes the one-time challenge; the code is validated first and the challenge is consumed only on success, with a bounded retry budget (5 attempts) before it burns. Fixes the "first typo traps you on the MFA page" bug.
- **MFA page escape**: the hosted MFA challenge page has a persistent "Back to sign in" link, and its back-links preserve the return URL.

### Added

- **Login logo background chip**: optional per-mode (`lightLogoBg`/`darkLogoBg`) background behind the login logo so white/transparent artwork stays visible on light cards. Non-regressive when unset.

## [0.7.5] – [0.7.6], 2026-07-15

### Fixed

- **Login-page CSP**: the server-inlined boot payload is now a non-executable `<script type="application/json">` tag (no longer blocked by `script-src`), and `font-src 'self' data:` allows inlined font subsets.
- **Packaging**: `Authagonal.AwsProvider` floats its `Microsoft.Extensions.*.Abstractions` references on `10.*`, fixing the NU1605 that blocked the whole NuGet publish. Note: the v0.7.5 NuGets were never published because of that failure (only npm went out), use v0.7.6 as the effective release.

## [0.7.3] – [0.7.4], 2026-07-14 → 07-15

### Added

- **SAML vendor-quirk readiness**: pasted-metadata support with a condensing parser (vendor metadata routinely exceeds storage limits), friendly-name + OID attribute aliases, multi-valued group attributes, per-connection `NameIdFormat` (including ADFS-safe omission), signature-failure metadata refetch, and the post-login return URL riding the stored AuthnRequest instead of RelayState.
- **SAML SP keypair**: per-connection self-signed SP certificate: signed AuthnRequests (auto-enabled when the IdP wants them), `EncryptedAssertion` decryption (RSA-OAEP/1.5 + AES-CBC/3DES; AES-GCM is not supported by .NET's `EncryptedXml` and reports a clear error), and SP metadata publishing signing + encryption key descriptors.
- **SAML Single Logout**: SP-initiated `/saml/{id}/logout` and IdP-initiated `/saml/{id}/slo` (Redirect + POST bindings), with session-bound safety for unsigned IdP logout requests.

## [0.7.0] – [0.7.2], 2026-07-12 → 07-13

### Added

- **Cursor-paged user listing**: `IUserStore.ListPageAsync`/`ListByScimClientPageAsync` with native continuation tokens; SCIM listing uses cursor pagination and SCIM `eq` filters resolve via blind-index point lookups.
- **`IUserStore.EnumerateLoginStatesAsync`**: a non-PII login-state stream for retention sweeps.
- **GDPR "Your data"**: self-service data export and account-deletion request on the hosted account page.

### Security

- **OIDC/federation + grant-store hardening (F32–F48)**: atomic grant consumption (`TryMarkConsumedAsync`), recovery-code encryption at rest, open-redirect fixes (backslash bypass; disabled/unknown-client redirect guard on both authorize endpoints), upstream-IdP scope filtering, federated logins now route MFA-enrolled users through the MFA challenge, device-flow `slow_down`, OIDC state atomicity + subject consistency checks, and tombstone-first expired-grant removal.

## [0.6.0] – [0.6.6], 2026-07-10 → 07-12

### Added

- **Change-log-driven incremental backups**: stores write every mutation to a change-log so incremental backups point-read changed rows instead of full-table scans. Opt-in via `BackupOptions.ChangeLoggedTables`; a daily full-scan backstop guarantees coverage. Includes the F24 restore-chain correctness cluster: exact EDM type round-trips, restores apply tombstones, a clock-skew margin on incremental filters, and tombstone-first delete ordering in every store.
- **Server-inlinable login boot payload**: the host can inline branding + providers as a JSON script tag to save a round-trip.

### Changed

- **BREAKING: `ITombstoneWriter` → `IChangeWriter`**: the change-log seam gained upsert capture (`WriteUpsertAsync`/`WriteUpsertBatchAsync`) and was renamed. Implementations and registrations must follow.

## [0.5.0] – [0.5.1], 2026-07-10

### Changed

- **BREAKING: OIDC endpoint plumbing deduplicated into `Authagonal.Protocol`**: discovery/JWKS/OAuth-error models moved to `Authagonal.Protocol.Endpoints`; embeddable hosts map them via `MapAuthagonalProtocolEndpoints`.

### Fixed

- **Grant re-key on re-store**: fetched grants are re-keyed before persisting, restoring device approval + refresh rotation (the store never persists the plaintext handle).

## [0.4.38] – [0.4.41], 2026-07-07 → 07-10

### Added

- **Encryption backfills**: legacy plaintext `UserExternalIds` index rows re-keyed; `UserLogins` and provisioning-app API keys encrypted at rest with backfills.

### Changed

- **Perf**: blind-index tokenization + login encryption batched into single Vault round-trips.

## [0.4.23] – [0.4.37], 2026-07-03 → 07-06

### Added

- **PII encryption at rest (searchable)**: Vault Transit (`aes256-gcm96`) field encryption behind the `IFieldCipher` seam for user PII and grant data, with keyed-HMAC blind indexes (`IIndexTokenizer`) for email, first/last-name prefixes, external IDs, email domain, and email local-part prefix, exact search over encrypted data, with lazy migration and an `ReindexUserAsync` backfill. Index updates write-before-delete so a Vault hiccup can't lock out logins.
- **Back to app**: client home URIs + a default application, so hosted pages (account, verification, reset) can send the user back to the right app; the originating client is threaded through verification + reset emails.
- **`OnMfaVerifyFailedAsync`**: auth-hook event for failed MFA/passkey verification.

### Fixed

- **Language pickers**: derive from the locale registry (hi/af/ar were missing).

## [0.4.15] – [0.4.22], 2026-07-01 → 07-02

### Added

- **Passkeys (WebAuthn)**: passwordless passkey login via conditional mediation, multiple passkeys per user, passkey enrollment gated behind TOTP, per-request relying-party config (multi-tenant-safe), and tenant-policy gating on self-service setup.
- **Edge-cacheable discovery**: `Cache-Control` on the OIDC discovery + JWKS documents.
- **Publish-ahead key rotation**: sign with a specific Transit key version.

## [0.4.6] – [0.4.14], 2026-06-24 → 06-28

### Added

- **Provider branding on the login screen**: icons + SAML connections surfaced on the hosted login page.
- **Per-tenant email-confirmation login gate** + an email-confirmed auth-hook event.
- **`extraRoutes` seam**: the host owns product routes inside the hosted login app.
- **Per-theme branding**: `darkMode`, `darkPrimaryColor`, and per-theme background/card colours.
- **Locales**: Hindi and Afrikaans added to the hosted auth pages.
- **Auth-hook events** for self-service MFA + password changes.

### Fixed

- **Email verification links**: served on GET (were POST-only) and the reset-password link prefix corrected.

## [0.4.1] – [0.4.5], 2026-06-21 → 06-23

### Added

- **User locale (preferred language)**: stored on the profile, editable on the new self-service account page, mapped from SCIM `preferredLanguage`, and used for localized email.
- **Arabic translation + RTL support** on the hosted login pages.

### Fixed

- **Storage→AzureProvider rename** completed across build files, tests, and `tag.sh`.
- **Login layout**: consent/grants pages no longer double-wrap `AuthLayout`; the "Powered by Authagonal" footer renders again.

## [0.4.0], 2026-06-17

### Added

- **AWS backend provider**: `Authagonal.AwsProvider` implements all stores and clustering on AWS (DynamoDB / S3 / Secrets Manager). The storage backend is now selectable: run on Azure Table Storage or AWS.

### Changed

- **`Authagonal.Storage` → `Authagonal.AzureProvider`**: the Azure Table Storage implementation moved to a provider-named package alongside `Authagonal.AwsProvider`. Update package references and service registration accordingly.

## [0.3.18] – [0.3.19], 2026-06-17

### Security

A second hardening pass:

- **Login & registration enumeration resistance**: responses no longer reveal whether an account exists.
- **Password-reset rate limiting**: per-email limit to prevent email-bombing.
- **Atomic lockout counter**: closes a parallel brute-force bypass.
- **Token audience validation**: issued tokens are validated against the configured `Audience(s)`.
- **Auth-hook ordering**: `IAuthHook`s now run *before* the session is established.

## [0.3.14] – [0.3.17], 2026-06-16 → 06-17

### Fixed

- **Accessibility**: `main` landmark, WCAG AA contrast, tap-target sizing, and a sensible min-width on the hosted login layout.
- **Login routing**: login-app router mounted at basename `/login` so sub-routes resolve.
- **Backup**: don't dispose the hash before reading it.

## [0.3.8] – [0.3.13], 2026-06-15

### Added

- **Cloudflare Turnstile**: opt-in CAPTCHA on login, registration, forgot-password, and reset-password; CSP allowance when configured; dedicated `captchaFailed` message across all login-app locales.
- **SCIM group → role mapping**: resolved at token issuance.

### Fixed

- **SCIM**: fixed user-listing overflow when fetching all users.

## [0.3.4] – [0.3.7], 2026-06-13 → 06-15

### Added

- **Pluggable event-bus clustering**: replaced gossip-based clustering with a pluggable event-bus architecture.
- **Admin registration**: custom user IDs, pre-confirmation, and extended attributes on admin-created users.

### Fixed

- **SAML (Entra)**: fixed RSA-SHA256 signature verification on .NET/Linux; removed the unsupported SAML `Subject` from `AuthnRequest`.
- **Build**: fixed a CS0433 break by bumping Azure.Identity to 1.21.0; net10.0 Docker publish.

## [0.3.1] – [0.3.3], 2026-05-30 → 05-31

### Added

- **User registration & OAuth screens**: login-app gains self-service registration plus OAuth consent and device-authorization pages.
- **Multi-framework**: net9.0 and net10.0 target support.
- **`example.com` auto-confirm config** and **WebAuthn credential-uniqueness** enforcement; expanded security regression tests.

### Security

Hardening from a full security review (the "top 8"):

- **MFA always challenges**: enrolled users are always challenged; closes a path where MFA could be bypassed.
- **Federation account-takeover prevention**: upstream logins now require `email_verified` and enforce the provider's `AllowedDomains` before linking or provisioning, preventing takeover via unverified or out-of-domain emails.
- **SAML assertion replay fix**: incoming SAML assertions are checked against the replay cache so a captured assertion cannot be re-submitted.
- **SCIM group isolation**: SCIM group access is scoped to the requesting client, closing a cross-client IDOR.
- **Admin scope reservation**: the admin scope (`AdminApi:Scope`) can no longer be granted to an OAuth client nor issued through the impersonation endpoint, preventing privilege persistence.
- **Forwarded-header trust config**: `ForwardedHeaders:ForwardLimit` / `KnownNetworks` / `KnownProxies` control which proxy hops may set the client IP, so `X-Forwarded-For` can't be spoofed to forge the IP used for rate limiting and lockout.
- **Internal-endpoint authentication**: `/_internal/cluster/gossip` and `/_internal/backchannel-logout` require the `X-Cluster-Secret` header when `Cluster:Secret` is set, and otherwise only accept loopback / private source IPs.
- **Backup signing-key exclusion + restore integrity**: the `SigningKeys` table is excluded from backups by default (`Backup:IncludeSigningKeys` to opt in), and restores verify file SHA-256 hashes against the manifest before writing.

## [0.3.0], 2026-05

### Added

- **JIT provisioning control**: just-in-time user provisioning on federated login is now configurable.
- **Partial SAML connection updates**: `PUT /api/v1/saml/connections/{connectionId}` supports partial updates of SAML provider configuration.

## [0.2.6]

### Added

- **Client admin API**: runtime CRUD for OAuth clients under `/api/v1/clients`, guarded by an `IClientScopeGuard` so callers can only grant scopes they are entitled to. Secret hashes are never returned.
- **Provisioning app admin API**: runtime CRUD for downstream provisioning apps under `/api/v1/provisioning/apps`, with a configurable per-deployment quota (`IProvisioningAppQuota`) and a `/test` connectivity check.

## [0.2.5]

### Fixed

- **JWT signature encoding**: corrected signature encoding for issued tokens.

### Changed

- **Release workflow**: optimized the build/release pipeline.

## [0.2.4], 2026

### Changed

- **Vault Transit ES256**: refactored the HashiCorp Vault Transit signing integration for proper ES256 (ECDSA P-256) support.

## [0.2.3]

### Changed

- Version bump across packages and demos.

## [0.2.2]

### Changed

- **ES256 signing**: migrated JWT signing from RS256 (RSA-2048) to ES256 (ECDSA P-256).
- **`@authagonal` package scope**: npm packages published under the `@authagonal` scope; added TypeScript type annotations.

## [0.2.1]

### Changed

- **Table store partitioning**: integrated the environment-aware partitioner into the table stores for sandbox table isolation.

## [0.2.0]

### Added

- **Pushed Authorization Requests (PAR, RFC 9126)**: clients can POST authorize parameters to `/connect/par` and receive a short-lived `request_uri`. Per-client enforcement via `RequirePushedAuthorizationRequests`. See [PAR](par).
- **OIDC federation**: extended upstream OIDC federation: upstream claim propagation, scope forwarding, and upstream session caps.

## [0.1.86], 2026-04-17

### Added

- **Dark mode**: login app supports light, dark, and system theme preferences. System color scheme is detected via `prefers-color-scheme` and user selection persists across browser sessions. New `useDarkMode` hook drives the theme toggle; all UI primitives (Alert, Button, Card, Input, Label, Separator) and pages (Login, Register, Consent, Device, Grants, MfaSetup, ResetPassword) updated with dark-mode styling. Tenant branding CSS variables still override theme defaults.

### Changed

- **Grant storage**: `GrantByExpiryEntity` partitioning refined for more reliable TTL sweeping of expired grants.

## [0.1.85], 2026-04-17

### Added

- **OAuth scope management**: admin CRUD endpoints under `/api/v1/scopes` backed by a new `ScopeEntity` table. Scopes defined at runtime are advertised by the discovery document and available to the consent screen alongside built-in scopes. New `IScopeStore` / `TableScopeStore` abstractions.
- **Dynamic client registration (RFC 7591)**: `POST /connect/register` endpoint allows OAuth clients to self-register at runtime. Gated by `Auth:DynamicClientRegistrationEnabled` (default off) and advertised via `registration_endpoint` in the discovery document when enabled.
- **Front-channel logout (RFC 7711)**: `EndSessionEndpoint` now supports front-channel logout URIs with configurable session requirements per client. Logs out of the authorization server and notifies registered clients via the browser.
- **Token revocation tracking**: access tokens can be invalidated before their natural expiry. New `IRevokedTokenStore` / `TableRevokedTokenStore` back a persistent revocation list consulted by the introspection endpoint. `RevokedTokens` table added to backup defaults.
- **Custom user attributes**: `AuthUser` gained a `CustomAttributes` dictionary for tenant-specific user metadata.
- **Client audience**: `OAuthClient` gained an `Audience` field that flows into issued access tokens.

### Changed

- **Discovery document**: now advertises custom scopes and the registration endpoint dynamically based on configured state.

## [0.1.84], 2026-04-16

### Fixed

- **`TokenResponse` JSON serialization**: registered in `AuthagonalJsonContext` so token endpoint responses serialize correctly under Native AOT.

## [0.1.83], 2026-04-16

### Added

- **Table Storage backup whitepaper**: `docs/whitepaper-table-storage-backup.md` documents the backup strategy: full/incremental runs, tombstone tracking for deletes, merge/rollup operations, and restore procedures.

### Fixed

- **Auth request DTO JSON serialization**: registered `LoginRequest`, `RegisterRequest`, `ConfirmEmailRequest`, `ForgotPasswordRequest`, `ResetPasswordRequest`, `MfaVerifyRequest`, `TotpConfirmRequest`, and `WebAuthnConfirmRequest` in `AuthagonalJsonContext` for AOT compatibility.

## [0.1.82], 2026-04-15

### Added

- **SAML assertion replay detection**: IdP-initiated SAML flows now check incoming assertions against a replay cache (`SamlReplayCache`), rejecting re-submitted assertions.
- **Absolute session expiration**: sessions now enforce a 7-day hard cap regardless of sliding renewal.
- **Constant-time SCIM token comparison**: `ScimBearerAuthenticationHandler` uses constant-time comparison on token hashes to prevent timing attacks.

### Changed

- **Password reset tokens**: reset flows now use single-use persisted grants instead of the user security stamp, enabling explicit invalidation and preventing token reuse.
- **PBKDF2 iterations increased**: password hashing cost raised in `AuthOptions`.
- **Refresh token grace period reduced**: shorter window for accepting a refresh token after rotation.
- **SCIM endpoints scoped to client**: SCIM requests are now scoped to the requesting OAuth client with per-client rate limiting, preventing cross-client enumeration.
- **Consent data error handling**: improved logging and error paths when persisted consent data is malformed.

## [0.1.74], 2026-04-10

### Changed

- **Request DTOs as classes**: all request DTOs converted from records to classes for Native AOT compatibility.

## [0.1.73], 2026-04-10

### Changed

- **`ScimPatchOperation.Op` optional**: `Op` property on `ScimPatchOperation` is now optional, improving compatibility with SCIM clients that omit it for replace operations.

## [0.1.72], 2026-04-10

### Fixed

- **Card UI imports**: added missing Card component imports in login-app pages.

## [0.1.71], 2026-04-10

### Fixed

- **Unused imports**: removed unused imports across login-app source files.

## [0.1.70], 2026-04-10

### Added

- **Native AOT support**: enabled IL trimming and source-generated JSON serialization across all packages (`Authagonal.Server`, `Authagonal.Core`, `Authagonal.Storage`, `Authagonal.Backup`). `AuthagonalJsonContext`, `StorageJsonContext`, and `BackupJsonContext` provide trim-safe serialization for 40+ types. `EnableRequestDelegateGenerator` and `EnableConfigurationBindingGenerator` enabled for minimal API AOT compatibility.

### Changed

- **Slimmed NuGet dependencies**: removed unused packages and resolved a security vulnerability.

## [0.1.69], 2026-04-09

### Added

- **OAuth consent screen**: clients with `RequireConsent: true` prompt users to approve requested scopes before issuing an authorization code. Consent decisions are persisted (5-year TTL) and re-prompted only when new scopes are requested.
  - `GET /consent/info`, returns client name and requested scopes for the consent page.
  - `POST /consent`, records the user's allow/deny decision.
  - `GET /consent/grants`, lists all applications the user has authorized.
  - `DELETE /consent/grants/{clientId}`, revokes consent for a specific application.
  - `ConsentPage` and `GrantsPage` added to the login SPA with full i18n support.
- **Backup integrity verification**: `BackupManifest` now includes a `FileHashes` dictionary (SHA-256) populated during backup creation and verified during restore.
- **Enhanced login UI customization**: added `data-auth` attributes to all login form elements for CSS targeting and test automation. New CSS custom properties: `--auth-bg`, `--auth-card-bg`, `--auth-radius`, `--auth-font`, `--auth-heading`.

## [0.1.68], 2026-04-09

### Changed

- Package version bump.

## [0.1.67], 2026-04-09

### Changed

- **Email service: SendGrid → Resend**: the built-in `EmailService` now uses the [Resend](https://resend.com) API instead of SendGrid. Configuration keys changed from `Email:SendGridApiKey` to `Email:ResendApiKey`. The `IEmailService` interface is unchanged, custom implementations are unaffected.

## [0.1.66], 2026-04-09

### Added

- **OIDC/SAML provider configuration properties**: `OidcProviderConfig` and `SamlProviderConfig` models extended with new properties. OIDC and SAML endpoints updated to support provider-level configuration.

### Changed

- Upgraded `Authagonal.Server` and `Authagonal.Storage` to 0.1.66.

## [0.1.65], 2026-04-09

### Added

- **OIDC Back-Channel Logout**: implements Back-Channel Logout 1.0. When a user logs out, Authagonal sends logout tokens to each client's registered `BackChannelLogoutUri` (fire-and-forget).
- **OAuth 2.0 Token Introspection**: `POST /connect/introspect` (RFC 7662) allows resource servers to verify token validity and retrieve claims. Authenticated via client credentials.
- **`BackChannelLogoutUri` on `OAuthClient`**: new optional property for registering a client's back-channel logout endpoint.
- **Comprehensive test coverage**: new test suites for admin SSO endpoints, OIDC SSO flow, SAML flow, SCIM discovery endpoints, and token introspection. Added `OidcMockHandler` and `SamlTestHelper` test utilities.

### Changed

- Discovery endpoint now advertises `introspection_endpoint` and `backchannel_logout_supported`.
- `EndSessionEndpoint` sends back-channel logout notifications to all clients with a registered logout URI.

## [0.1.64], 2026-04-09

### Added

- **Admin role endpoint tests**: full CRUD and assign/unassign coverage.
- **Admin SCIM token endpoint tests**: create, list, and revoke token coverage.
- **Device authorization flow tests**: end-to-end device code grant coverage.
- **Token edge case tests**: expired tokens, invalid grants, scope handling.
- **End session tests**: logout and session cleanup coverage.

### Fixed

- **SCIM pagination offset**: `startIndex` now correctly zero-indexed internally, fixing off-by-one in paginated SCIM list responses.

### Changed

- **`VaultTransitClient` testability**: removed `sealed` modifier and made methods `virtual` so the client can be mocked in tests.

## [0.1.63], 2026-04-09

### Changed

- **Span-based signing**: `VaultTransitSignatureProvider` now overrides the `Sign(byte[], int, int)` span-based method for more efficient memory handling and better P/Invoke interop support.

## [0.1.62], 2026-04-08

### Added

- **HashiCorp Vault Transit integration**: `VaultTransitClient`, `VaultTransitCryptoProvider`, `VaultTransitSecurityKey`, and `VaultTransitSignatureProvider` enable remote cryptographic key signing via Vault's Transit secrets engine. Allows JWT signing without local private key access.

### Changed

- **PBKDF2 iterations**: reduced from 100,000 to 50,000 to balance security with performance.

## [0.1.60], 2026-04-08

### Added

- **OAuth Device Authorization Grant**: implements RFC 8628 for input-constrained devices (smart TVs, CLIs, IoT).
  - `POST /connect/deviceauthorization`, initiates a device flow, returns `device_code`, `user_code`, and `verification_uri`.
  - Device code approval page, authenticated users enter the user code to authorize the device.
  - `urn:ietf:params:oauth:grant-type:device_code` grant type on the token endpoint, devices poll until the user approves.
- **Device authorization UI**: `DevicePage` added to the login SPA for user code entry and approval.
- Discovery endpoint now advertises `device_authorization_endpoint`.

## [0.1.59], 2026-04-08

### Changed

- **Auto-confirm `@example.com` emails**: user registration automatically confirms email addresses ending with `@example.com`, simplifying demo and test workflows.

## [0.1.58], 2026-04-08

This release is a re-cut of 0.1.57 with package version bumps. See [0.1.57] for the full feature list.

## [0.1.57], 2026-04-08

### Added

- **Auto-provisioning on all creation endpoints**: TCC provisioning now triggers when users are created via admin API, self-service registration, SAML SSO, OIDC SSO, and SCIM. If any provisioning app rejects the user in the Try phase, the user is deleted and the endpoint returns `422`.
- **`IProvisioningAppProvider`**: new extensibility point for resolving available provisioning apps dynamically. Default `ConfigProvisioningAppProvider` reads from `appsettings.json`. Override to resolve apps per-tenant or from an external source.
- **`SigningKeyOps` utility**: extracted signing key operations (generate, rotate, build JWKS) into a reusable static class. Enables both single-tenant `KeyManager` and multi-tenant wrappers to share key logic.
- **`RegisterPage` and `App` exports**: `@drawboard/authagonal-login` now exports the built-in registration page and a standalone `App` component with full routing for consumers who want the complete SPA.
- **Enter key SSO check**: email field on the login page accepts Enter to trigger SSO domain check (no longer requires tab/blur).
- **Load testing framework**: k6-based test suite (`tests/load/`) with smoke, stress, and soak tests targeting the demo deployment. GitHub Actions workflow runs smoke after every deploy and soak daily.
- **`Authagonal.Backup` NuGet package**: backup library now published to nuget.org alongside Server, Core, and Storage.

### Changed

- **Multi-tenant dependency resolution**: `IClientStore`, `IScimTokenStore`, and `IUserProvisionStore` now resolved from `HttpContext.RequestServices` for per-request tenant isolation. `IKeyManager` supports scoped registration for per-tenant signing keys.
- **`AddAuthagonalCore()` / `AddAuthagonal()` split**: `AddAuthagonalCore()` registers services safe for both single and multi-tenant hosts. `AddAuthagonal()` adds the full single-tenant stack (stores, KeyManager, background services). Multi-tenant hosts call `AddAuthagonalCore()` and register their own equivalents.
- **Conditional background services**: `GrantReconciliationService` and `SigningKeyRotationService` only register in single-tenant mode. Multi-tenant hosts manage these per-tenant.
- **Demo login pages**: `AcmeLoginPage` and `RegisterPage` refactored to use the library's UI components (`Button`, `Input`, `Label`, `Alert`, `CardTitle`, `CardFooter`) instead of the old pre-Tailwind CSS classes.

## [0.1.42], 2026-04-06

### Fixed

- **npm package CSS**: login-app now imports `styles.css` as a side effect in the entry point, so Vite emits `dist/index.css` in library mode. Fixed export map to point to `dist/index.css`.
- **npm package types**: swapped build order to `vite build && tsc -b` so Vite's `emptyOutDir` doesn't wipe TypeScript declaration files.

## [0.1.38], 2026-04-05

### Changed

- **Compiled library**: `@drawboard/authagonal-login` now ships compiled JS + CSS in `dist/` instead of raw TypeScript source. Consumers no longer need Tailwind or the Vite build toolchain.

## [0.1.31], 2026-03-31

### Added

- **Self-service registration**: `POST /api/auth/register` endpoint with distributed per-IP rate limiting (5 registrations per IP per hour, configurable).
- **Distributed rate limiting**: CRDT G-Counter shared via gossip protocol across cluster instances.
- **MIT license**: switched from proprietary to MIT.

### Changed

- **Cluster node identity**: each instance generates a random hex node ID at startup for gossip protocol identification.
- **Cluster leader election**: `ClusterLeaderService` elects a single leader among discovered peers for cluster-wide coordination.

## [0.1.30], 2026-03-30

### Added

- **Role-based access control**: `Role` model, `IRoleStore`/`TableRoleStore`, and admin API endpoints at `/api/v1/roles/` for CRUD, assign, unassign, and user-role queries.
- **Multi-tenant abstractions**: `ITenantContext` and `IKeyManager` interfaces for per-tenant configuration and signing key isolation. `DefaultTenantContext` reads from `IConfiguration`.
- **Tailwind CSS**: login SPA migrated from custom CSS to Tailwind. Reusable UI components exported: `Button`, `Input`, `Label`, `Card`, `Alert`, `Separator`, `cn`.
- **Multiple IAuthHook support**: all registered `IAuthHook` implementations now run in registration order via `IEnumerable<IAuthHook>` pipeline.
- **Backup & restore library**: `src/Authagonal.Backup` with `BackupService`, `RestoreService`, tombstone-based delete tracking, merge and rollup for backup compaction.

## [0.1.26], 2026-03-30

### Fixed

- **SCIM base URL handler**: Entra ID hits `/scim/v2` directly during credential validation. Added a handler that returns `ServiceProviderConfig` instead of falling through to the SPA catch-all.

## [0.1.25], 2026-03-30

### Added

- **Entra SAML integration**: configured Entra ID enterprise app for SAML SSO in the demo environment.
- **SAML login hint passthrough**: email entered on the login page is now passed to the SAML IdP via both the `Subject/NameID` element in the AuthnRequest and the `login_hint` query parameter.
- **MFA back navigation**: MFA setup page accepts a `backUrl` parameter, allowing users to return to the originating app after managing MFA settings.
- **Sample app tab UI**: logged-in view now uses horizontal tabs (Profile, API Explorer, Token) with an MFA Settings link.

## [0.1.24], 2026-03-30

### Added

- **SCIM 2.0 provisioning**: full inbound user and group provisioning from enterprise identity providers (Microsoft Entra ID, Okta, OneLogin).
  - `POST /scim/v2/Users`, `GET /scim/v2/Users`, `GET /scim/v2/Users/{id}`, `PUT /scim/v2/Users/{id}`, `PATCH /scim/v2/Users/{id}`, `DELETE /scim/v2/Users/{id}`, user CRUD with soft-delete deactivation.
  - `POST /scim/v2/Groups`, `GET /scim/v2/Groups`, `GET /scim/v2/Groups/{id}`, `PUT /scim/v2/Groups/{id}`, `PATCH /scim/v2/Groups/{id}`, `DELETE /scim/v2/Groups/{id}`, group CRUD with member add/remove via PATCH.
  - `GET /scim/v2/ServiceProviderConfig`, `GET /scim/v2/Schemas`, `GET /scim/v2/ResourceTypes`, SCIM discovery endpoints.
  - SCIM filter support: `eq` on `userName`/`externalId`/`displayName`, `co` on `displayName`.
  - Paginated list responses with `startIndex` and `count`.
- **SCIM token authentication**: per-client static Bearer tokens (stored SHA-256 hashed). Custom `ScimBearer` authentication scheme and `ScimProvisioning` authorization policy.
  - `POST /api/v1/scim/tokens`, generate a SCIM token for a client (returns raw token once).
  - `GET /api/v1/scim/tokens?clientId={id}`, list tokens (metadata only).
  - `DELETE /api/v1/scim/tokens/{tokenId}?clientId={id}`, revoke a token.
- **SCIM group model**: `ScimGroup` with `DisplayName`, `ExternalId`, `OrganizationId`, and `MemberUserIds`. `IScimGroupStore` interface and `TableScimGroupStore` Azure Table Storage implementation.
- **User externalId**: `AuthUser.ExternalId` property for IdP-assigned identifiers. `UserExternalIds` table provides O(1) lookup by `(clientId, externalId)`.
- **IsActive guard**: deactivated users (`IsActive = false`) are rejected at password login, SAML SSO, OIDC SSO, refresh token exchange, and cookie validation.
- **SCIM-triggered TCC provisioning**: SCIM user creation triggers downstream TCC provisioning if the client has `ProvisioningApps` configured.
- **SCIM documentation**: `docs/scim.md` with IdP setup guides (Entra ID, Okta, OneLogin), endpoint reference, and attribute mapping. Localized stubs for de, es, fr, pt, vi, zh-Hans.

### Changed

- **`AuthUser` model**: added `ExternalId`, `IsActive` (default `true`), `ScimProvisionedByClientId` properties.
- **`IUserStore`**: added `FindByExternalIdAsync`, `ListAsync`, `SetExternalIdAsync`, `RemoveExternalIdAsync` methods.
- **`TableUserStore`**: now accepts a `userExternalIdsTable` parameter; implements new `IUserStore` methods.
- **`ServiceCollectionExtensions`**: registers 4 new tables (`UserExternalIds`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`) and 2 new stores (`IScimTokenStore`, `IScimGroupStore`).
- **`AuthagonalExtensions`**: adds `ScimBearer` auth scheme, `ScimProvisioning` policy, wires SCIM endpoints.
- **Cookie validation**: `OnValidatePrincipal` now rejects inactive users.
- **Token refresh**: `HandleRefreshTokenAsync` now rejects deactivated users.

### Fixed

- **QR code test**: `TotpSetup_ReturnsQrCodeAndSetupToken` test assertion updated from SVG to PNG to match actual QR code output format.

## [0.1.9], 2026-03-29

### Improved

- **Login UX**: added a "Continue" button after the email field instead of relying on a hidden blur-triggered SSO check. External provider buttons (e.g. Google) now collapse into a compact "Or sign in with..." link once the password field is shown, and can be expanded again by clicking the link.
- **Registration link always visible**: "Don't have an account? Create one" link is now shown below the form at all times, not just after the password field appears.
- **i18n completeness**: added `continue`, `noAccount`, `createAccount`, and `orSignInWith` translation keys across all 8 languages (en, de, es, fr, pt, vi, zh-Hans, tlh).

## [0.1.8], 2026-03-29

### Added

- **Multi-factor authentication**: TOTP (authenticator apps), WebAuthn/passkeys, and recovery codes. MFA is enforced per-client via `MfaPolicy` on the client configuration (`Disabled`, `Enabled`, `Required`).
  - `POST /api/auth/mfa/verify`, challenge verification (TOTP code, WebAuthn assertion, or recovery code)
  - `GET /api/auth/mfa/status`, enrolled methods for the current user
  - `POST /api/auth/mfa/totp/setup` + `POST /api/auth/mfa/totp/confirm`, TOTP enrollment with QR code
  - `POST /api/auth/mfa/webauthn/setup` + `POST /api/auth/mfa/webauthn/confirm`, passkey enrollment
  - `POST /api/auth/mfa/recovery/generate`, generate 10 one-time recovery codes
  - `DELETE /api/auth/mfa/credentials/{id}`, remove an enrolled method
- **MFA admin endpoints**: `GET /api/v1/profile/{userId}/mfa`, `DELETE /api/v1/profile/{userId}/mfa`, `DELETE /api/v1/profile/{userId}/mfa/{id}` for admin MFA management.
- **MFA policy hook**: `IAuthHook.ResolveMfaPolicyAsync` allows per-user/org override of the client's MFA policy. `IAuthHook.OnMfaVerifiedAsync` fires after successful MFA verification.
- **Setup token flow**: when `MfaPolicy=Required` and the user has no MFA enrolled, login returns a setup token. The MFA setup endpoints accept this token via `X-MFA-Setup-Token` header, allowing enrollment before cookie authentication.
- **MFA frontend**: `MfaChallengePage` (TOTP/passkey/recovery code entry) and `MfaSetupPage` (QR code scanning, passkey registration, recovery code generation) added to the login SPA.
- **Demo: self-service registration**: `POST /api/auth/register` endpoint for the demo server, plus a registration page in the demo login app.
- **Demo: user purge**: background service deletes demo users older than 24 hours.
- **Table Storage restore tool**: `tools/Authagonal.Restore/` reads `.jsonl` backups produced by `authagonal-backup` and restores to Table Storage. Supports `upsert`, `merge`, and `clean` modes.

### Changed

- **Login response**: `POST /api/auth/login` may now return `{ mfaRequired, challengeId, methods, webAuthn }` or `{ mfaSetupRequired, setupToken }` instead of setting a cookie directly.
- **Fido2.AspNet** dependency added for WebAuthn credential verification.
- **QRCoder** dependency added for server-side QR code generation.

## [0.1.5], 2026-03-29

### Added

- **Integration test suite**: 48 API endpoint tests covering health, discovery/JWKS, auth (login, session, logout, SSO, lockout, password reset), OAuth (client_credentials, authorization code + PKCE, refresh tokens, revocation, userinfo), and admin endpoints (user CRUD, external identity linking). All tests use an in-memory test server with no external dependencies.
- **ASP.NET Identity hash compatibility**: `PasswordHasher` now verifies ASP.NET Identity V3 hashes (PBKDF2 with SHA1/256/384/512, variable iterations and salt sizes). Migrated users are auto-upgraded to the native format on next login.
- **Configurable admin scope**: `AdminApi:Scope` setting (default `authagonal-admin`) controls which JWT scope grants admin access. Set to `projects-identity-admin` for IdentityServer migration compatibility.
- **`NullEmailService`**: no-op email service is now the default. Register a real `IEmailService` (e.g., the built-in SendGrid `EmailService`) before `AddAuthagonal()` to enable email delivery.

## [0.1.4], 2026-03-29

### Added

- **npm package README**: `@drawboard/authagonal-login` now includes a comprehensive README covering installation, quick start, page customization, full API client reference, branding, i18n, and exports.
- **Docs favicon**: documentation site now uses the Authagonal logo as its browser tab icon.

## [0.1.3], 2026-03-29

### Changed

- **Demo consumes published packages**: demo `CustomAuthServer` now references NuGet packages from nuget.org and `@drawboard/authagonal-login` from npm, instead of building from source. The Docker build no longer needs the login-app source tree.
- **CI release workflow**: consolidated `nuget.yml`, `npm.yml`, and tag-triggered Docker builds into a single `release.yml` with proper job ordering: publish NuGet → wait for indexing → publish npm → wait for indexing → build Docker images → deploy. Eliminates the race condition where Docker builds could start before packages were available on registries.
- **Docker workflow**: `docker.yml` now only triggers on `master` branch pushes (tag builds handled by `release.yml`).

## [0.1.1], 2026-03-29

### Fixed

- **i18n module duplication**: consumers importing `useTranslation` from their own `react-i18next` copy got a different instance than the one initialized by the base package. Fixed by re-exporting `useTranslation` from `@drawboard/authagonal-login`.
- **OAuth returnUrl dropped on SSO redirect**: the authorize endpoint generated a full URL as the returnUrl, which was rejected by both client-side `isSafeReturnUrl()` and server-side `SanitizeReturnUrl()` (both require relative paths). Fixed by using a relative path.
- **Language detection not persisting**: added `localStorage` to `i18next-browser-languagedetector` order and caches arrays.
- **OIDC error display**: login page now reads `error` / `error_description` from URL params and displays them.

### Added

- **Localizable branding strings**: `welcomeTitle` and `welcomeSubtitle` in `branding.json` now accept `LocalizedString` (a plain string or a `{ "en": "...", "es": "..." }` object). New `resolveLocalized()` helper resolves the best match for the current language.
- **Sign-out button**: login page detects existing sessions and shows a "Signed in as" view with a sign-out button, instead of showing the login form.
- **NuGet package READMEs**: `Authagonal.Server`, `Authagonal.Core`, and `Authagonal.Storage` now include README files displayed on nuget.org.
- **i18n keys**: added `signedInAs`, `signedInMessage`, `signOut`, `welcomeTitle`, `welcomeSubtitle`, `continueWith`, `or` to all 7 language files.

## [0.1.0], 2026-03-29

### Added

- **Docker packaging**: multi-stage `Dockerfile` builds the React SPA and .NET server into a single image. SPA served as static files from `wwwroot/` on the same origin as the API.
- **`Dockerfile.migration`**: separate image for the SQL Server → Table Storage migration tool.
- **`docker-compose.yml`**: local development setup with Azurite storage emulator.
- **Static file serving**: `UseDefaultFiles()`, `UseStaticFiles()`, and `MapFallbackToFile("index.html")` added to `Program.cs` for SPA hosting.
- **TCC provisioning system**: replaces the single-webhook provisioning with a Try-Confirm-Cancel pattern:
  - N provisioning apps defined in configuration (`ProvisioningApps` section) with callback URLs and API keys.
  - Clients declare which apps they provision into via `ProvisioningApps` field.
  - Provisioning runs at the authorize endpoint, before a code is issued, the user is provisioned into all required apps.
  - Per-user/per-app tracking in the `UserProvisions` table prevents re-provisioning on subsequent logins.
  - Try phase: calls each app's `/try` endpoint with user details, app can approve or reject.
  - Confirm phase: on all-approve, calls `/confirm` on each app to commit.
  - Cancel phase: on any failure, calls `/cancel` on successful tries to clean up.
  - Partial confirm failure: stores provision records for confirmed apps so only failed ones are retried.
- **`IProvisioningOrchestrator`** interface and `TccProvisioningOrchestrator` implementation.
- **`IUserProvisionStore`** interface and `TableUserProvisionStore` for Azure Table Storage.
- **`UserProvisionEntity`**: table entity keyed by `(userId, appId)`.
- **Deprovision on user delete**: `DELETE /api/v1/profile/{userId}` now calls `DeprovisionAllAsync` to notify all downstream apps.
- **Runtime branding**: login SPA reads `/branding.json` at startup for customization without rebuilding:
  - `appName`, header text and browser tab title.
  - `logoUrl`, replaces text header with an image.
  - `primaryColor`, buttons, links, focus rings (via CSS custom properties).
  - `showForgotPassword`, toggle the forgot password link.
  - `customCssUrl`, load additional CSS for deeper styling.
- **CSS custom properties**: primary color exposed as `--color-primary`, used throughout styles via `color-mix()` for hover/focus variants.
- **GitHub Pages documentation site**: overview, installation, quickstart, configuration, branding, provisioning, SAML, OIDC federation, admin API, auth API, and migration guides.
- **`IAuthHook` extensibility point**: lifecycle hooks for authentication events. Implementations are called on login success/failure, user creation, and token issuance. Throw from a hook to abort the operation (e.g., reject a login). Default implementation is a no-op (`NullAuthHook`).
  - `OnUserAuthenticatedAsync`, after password, SAML, or OIDC login.
  - `OnUserCreatedAsync`, after user creation via SSO or admin API.
  - `OnLoginFailedAsync`, on invalid credentials or lockout.
  - `OnTokenIssuedAsync`, when tokens are issued via the token endpoint.
- **Composable extension methods**: `AddAuthagonal()`, `UseAuthagonal()`, `MapAuthagonalEndpoints()` allow hosting Authagonal as a library in any ASP.NET Core application. Override `IEmailService`, `IAuthHook`, `IProvisioningOrchestrator`, or `ISecretProvider` by registering before `AddAuthagonal()`.
- **Demo: custom-server**: shows hosting Authagonal with custom `IAuthHook` (audit logging), custom `IEmailService` (console output), custom branding, and custom endpoints.
- **Demo: sample-app**: shows a client application (ASP.NET API + React SPA) authenticating via Authagonal using OIDC authorization code + PKCE, with protected API calls using JWT bearer tokens.
- **Demo: custom-server frontend**: custom login-app that installs `@drawboard/authagonal-login` as an npm dependency and overrides `LoginPage` (adds Terms of Service checkbox) and `AuthLayout` (adds branded footer), while reusing base `ForgotPasswordPage` and `ResetPasswordPage` as-is.
- **Configurable password policy**: `PasswordPolicy` configuration section controls min length, character requirements. `GET /api/auth/password-policy` endpoint exposes rules dynamically. Frontend fetches policy instead of hardcoding. Password validation now enforced on admin user registration too.
- **SAML/OIDC providers from configuration**: `SamlProviders` and `OidcProviders` config sections seed identity providers on startup (same pattern as `Clients`). SSO domain mappings are registered automatically from `AllowedDomains`.
- **`ProviderSeedService`**: `IHostedService` that seeds SAML and OIDC providers from configuration, with secret protection via `ISecretProvider`.
- **Login-app component library exports**: `@drawboard/authagonal-login` now exports all components, pages, branding hooks, API client, and types via `src/index.ts` with proper `exports` field in package.json. Consumers can `npm install` and import individual pieces.
- **CI/CD**: GitHub Actions workflows for Docker Hub publishing (`drawboardci/authagonal`, `drawboardci/authagonal-migration`) and npm publishing (`@drawboard/authagonal-login`).
- **i18n**: login SPA supports 7 languages (English, Chinese Simplified, German, French, Spanish, Vietnamese, Portuguese) with browser detection and language selector.
- **NuGet packaging**: `Authagonal.Server`, `Authagonal.Core`, `Authagonal.Storage` published to nuget.org.

### Changed

- **CSP header**: changed from `default-src 'none'` to `default-src 'self'; frame-ancestors 'none'; object-src 'none'` to allow the SPA to load resources from the same origin.
- **`OAuthClient` model**: added `ProvisioningApps` list field.
- **`ClientEntity`**: added `ProvisioningAppsJson` column (JSON-serialized string array, same pattern as other list fields).
- **`ServiceCollectionExtensions`**: registers `UserProvisions` table and `IUserProvisionStore`.
- **Authorize endpoint**: now injects `IUserStore` and `IProvisioningOrchestrator`, runs TCC provisioning between auth check and code issuance.
- **Admin `RegisterUser`**: no longer calls provisioning webhook; creates users with a generated GUID. Provisioning happens at authorize time.
- **Admin `DeleteUser`**: calls `IProvisioningOrchestrator.DeprovisionAllAsync` instead of `NotifyUserDeletedAsync`.
- **`Program.cs`**: refactored from inline setup to composable extension methods (`AddAuthagonal`, `UseAuthagonal`, `MapAuthagonalEndpoints`). Now 13 lines.
- **Token endpoint**: fires `IAuthHook.OnTokenIssuedAsync` on successful token issuance.
- **Auth endpoints**: fire `IAuthHook.OnUserAuthenticatedAsync` / `OnLoginFailedAsync`.
- **SAML/OIDC endpoints**: fire `IAuthHook.OnUserCreatedAsync` and `OnUserAuthenticatedAsync`.
- **Admin user endpoint**: fires `IAuthHook.OnUserCreatedAsync` on user registration; now validates password strength.
- **`PasswordValidator`**: refactored from hardcoded constants to accept a `PasswordPolicy` parameter.
- **`ResetPasswordPage`**: fetches password requirements from `GET /api/auth/password-policy` instead of hardcoding rules.
- **Scope rename**: `projects-identity-admin` renamed to `authagonal-admin`.

### Removed

- **`IUserProvisioningService`**: replaced by `IProvisioningOrchestrator`.
- **`UserProvisioningService`**: replaced by `TccProvisioningOrchestrator`.
- **Provisioning in SAML/OIDC flows**: SSO endpoints now just create users in Authagonal without external provisioning calls. Provisioning is deferred to the authorize endpoint where the client (and its required apps) are known.
- **`Provisioning:WebhookUrl` / `Provisioning:ApiKey` config**: replaced by per-app configuration under `ProvisioningApps`.

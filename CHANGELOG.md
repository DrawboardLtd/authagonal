# Changelog

## [0.1.66] — 2026-04-09

### Added

- **OIDC/SAML provider configuration properties** — `OidcProviderConfig` and `SamlProviderConfig` models extended with new properties. OIDC and SAML endpoints updated to support provider-level configuration.

### Changed

- Upgraded `Authagonal.Server` and `Authagonal.Storage` to 0.1.66.

## [0.1.65] — 2026-04-09

### Added

- **OIDC Back-Channel Logout** — implements Back-Channel Logout 1.0. When a user logs out, Authagonal sends logout tokens to each client's registered `BackChannelLogoutUri` (fire-and-forget).
- **OAuth 2.0 Token Introspection** — `POST /connect/introspect` (RFC 7662) allows resource servers to verify token validity and retrieve claims. Authenticated via client credentials.
- **`BackChannelLogoutUri` on `OAuthClient`** — new optional property for registering a client's back-channel logout endpoint.
- **Comprehensive test coverage** — new test suites for admin SSO endpoints, OIDC SSO flow, SAML flow, SCIM discovery endpoints, and token introspection. Added `OidcMockHandler` and `SamlTestHelper` test utilities.

### Changed

- Discovery endpoint now advertises `introspection_endpoint` and `backchannel_logout_supported`.
- `EndSessionEndpoint` sends back-channel logout notifications to all clients with a registered logout URI.

## [0.1.64] — 2026-04-09

### Added

- **Admin role endpoint tests** — full CRUD and assign/unassign coverage.
- **Admin SCIM token endpoint tests** — create, list, and revoke token coverage.
- **Device authorization flow tests** — end-to-end device code grant coverage.
- **Token edge case tests** — expired tokens, invalid grants, scope handling.
- **End session tests** — logout and session cleanup coverage.

### Fixed

- **SCIM pagination offset** — `startIndex` now correctly zero-indexed internally, fixing off-by-one in paginated SCIM list responses.

### Changed

- **`VaultTransitClient` testability** — removed `sealed` modifier and made methods `virtual` so the client can be mocked in tests.

## [0.1.63] — 2026-04-09

### Changed

- **Span-based signing** — `VaultTransitSignatureProvider` now overrides the `Sign(byte[], int, int)` span-based method for more efficient memory handling and better P/Invoke interop support.

## [0.1.62] — 2026-04-08

### Added

- **HashiCorp Vault Transit integration** — `VaultTransitClient`, `VaultTransitCryptoProvider`, `VaultTransitSecurityKey`, and `VaultTransitSignatureProvider` enable remote cryptographic key signing via Vault's Transit secrets engine. Allows JWT signing without local private key access.

### Changed

- **PBKDF2 iterations** — reduced from 100,000 to 50,000 to balance security with performance.

## [0.1.60] — 2026-04-08

### Added

- **OAuth Device Authorization Grant** — implements RFC 8628 for input-constrained devices (smart TVs, CLIs, IoT).
  - `POST /connect/deviceauthorization` — initiates a device flow, returns `device_code`, `user_code`, and `verification_uri`.
  - Device code approval page — authenticated users enter the user code to authorize the device.
  - `urn:ietf:params:oauth:grant-type:device_code` grant type on the token endpoint — devices poll until the user approves.
- **Device authorization UI** — `DevicePage` added to the login SPA for user code entry and approval.
- Discovery endpoint now advertises `device_authorization_endpoint`.

## [0.1.59] — 2026-04-08

### Changed

- **Auto-confirm `@example.com` emails** — user registration automatically confirms email addresses ending with `@example.com`, simplifying demo and test workflows.

## [0.1.58] — 2026-04-08

This release is a re-cut of 0.1.57 with package version bumps. See [0.1.57] for the full feature list.

## [0.1.57] — 2026-04-08

### Added

- **Auto-provisioning on all creation endpoints** — TCC provisioning now triggers when users are created via admin API, self-service registration, SAML SSO, OIDC SSO, and SCIM. If any provisioning app rejects the user in the Try phase, the user is deleted and the endpoint returns `422`.
- **`IProvisioningAppProvider`** — new extensibility point for resolving available provisioning apps dynamically. Default `ConfigProvisioningAppProvider` reads from `appsettings.json`. Override to resolve apps per-tenant or from an external source.
- **`SigningKeyOps` utility** — extracted signing key operations (generate, rotate, build JWKS) into a reusable static class. Enables both single-tenant `KeyManager` and multi-tenant wrappers to share key logic.
- **`RegisterPage` and `App` exports** — `@drawboard/authagonal-login` now exports the built-in registration page and a standalone `App` component with full routing for consumers who want the complete SPA.
- **Enter key SSO check** — email field on the login page accepts Enter to trigger SSO domain check (no longer requires tab/blur).
- **Load testing framework** — k6-based test suite (`tests/load/`) with smoke, stress, and soak tests targeting the demo deployment. GitHub Actions workflow runs smoke after every deploy and soak daily.
- **`Authagonal.Backup` NuGet package** — backup library now published to nuget.org alongside Server, Core, and Storage.

### Changed

- **Multi-tenant dependency resolution** — `IClientStore`, `IScimTokenStore`, and `IUserProvisionStore` now resolved from `HttpContext.RequestServices` for per-request tenant isolation. `IKeyManager` supports scoped registration for per-tenant signing keys.
- **`AddAuthagonalCore()` / `AddAuthagonal()` split** — `AddAuthagonalCore()` registers services safe for both single and multi-tenant hosts. `AddAuthagonal()` adds the full single-tenant stack (stores, KeyManager, background services). Multi-tenant hosts call `AddAuthagonalCore()` and register their own equivalents.
- **Conditional background services** — `GrantReconciliationService` and `SigningKeyRotationService` only register in single-tenant mode. Multi-tenant hosts manage these per-tenant.
- **Demo login pages** — `AcmeLoginPage` and `RegisterPage` refactored to use the library's UI components (`Button`, `Input`, `Label`, `Alert`, `CardTitle`, `CardFooter`) instead of the old pre-Tailwind CSS classes.

## [0.1.42] — 2026-04-06

### Fixed

- **npm package CSS** — login-app now imports `styles.css` as a side effect in the entry point, so Vite emits `dist/index.css` in library mode. Fixed export map to point to `dist/index.css`.
- **npm package types** — swapped build order to `vite build && tsc -b` so Vite's `emptyOutDir` doesn't wipe TypeScript declaration files.

## [0.1.38] — 2026-04-05

### Changed

- **Compiled library** — `@drawboard/authagonal-login` now ships compiled JS + CSS in `dist/` instead of raw TypeScript source. Consumers no longer need Tailwind or the Vite build toolchain.

## [0.1.31] — 2026-03-31

### Added

- **Self-service registration** — `POST /api/auth/register` endpoint with distributed per-IP rate limiting (5 registrations per IP per hour, configurable).
- **Distributed rate limiting** — CRDT G-Counter shared via gossip protocol across cluster instances.
- **MIT license** — switched from proprietary to MIT.

### Changed

- **Cluster node identity** — each instance generates a random hex node ID at startup for gossip protocol identification.
- **Cluster leader election** — `ClusterLeaderService` elects a single leader among discovered peers for cluster-wide coordination.

## [0.1.30] — 2026-03-30

### Added

- **Role-based access control** — `Role` model, `IRoleStore`/`TableRoleStore`, and admin API endpoints at `/api/v1/roles/` for CRUD, assign, unassign, and user-role queries.
- **Multi-tenant abstractions** — `ITenantContext` and `IKeyManager` interfaces for per-tenant configuration and signing key isolation. `DefaultTenantContext` reads from `IConfiguration`.
- **Tailwind CSS** — login SPA migrated from custom CSS to Tailwind. Reusable UI components exported: `Button`, `Input`, `Label`, `Card`, `Alert`, `Separator`, `cn`.
- **Multiple IAuthHook support** — all registered `IAuthHook` implementations now run in registration order via `IEnumerable<IAuthHook>` pipeline.
- **Backup & restore library** — `src/Authagonal.Backup` with `BackupService`, `RestoreService`, tombstone-based delete tracking, merge and rollup for backup compaction.

## [0.1.26] — 2026-03-30

### Fixed

- **SCIM base URL handler** — Entra ID hits `/scim/v2` directly during credential validation. Added a handler that returns `ServiceProviderConfig` instead of falling through to the SPA catch-all.

## [0.1.25] — 2026-03-30

### Added

- **Entra SAML integration** — configured Entra ID enterprise app for SAML SSO in the demo environment.
- **SAML login hint passthrough** — email entered on the login page is now passed to the SAML IdP via both the `Subject/NameID` element in the AuthnRequest and the `login_hint` query parameter.
- **MFA back navigation** — MFA setup page accepts a `backUrl` parameter, allowing users to return to the originating app after managing MFA settings.
- **Sample app tab UI** — logged-in view now uses horizontal tabs (Profile, API Explorer, Token) with an MFA Settings link.

## [0.1.24] — 2026-03-30

### Added

- **SCIM 2.0 provisioning** — full inbound user and group provisioning from enterprise identity providers (Microsoft Entra ID, Okta, OneLogin).
  - `POST /scim/v2/Users`, `GET /scim/v2/Users`, `GET /scim/v2/Users/{id}`, `PUT /scim/v2/Users/{id}`, `PATCH /scim/v2/Users/{id}`, `DELETE /scim/v2/Users/{id}` — user CRUD with soft-delete deactivation.
  - `POST /scim/v2/Groups`, `GET /scim/v2/Groups`, `GET /scim/v2/Groups/{id}`, `PUT /scim/v2/Groups/{id}`, `PATCH /scim/v2/Groups/{id}`, `DELETE /scim/v2/Groups/{id}` — group CRUD with member add/remove via PATCH.
  - `GET /scim/v2/ServiceProviderConfig`, `GET /scim/v2/Schemas`, `GET /scim/v2/ResourceTypes` — SCIM discovery endpoints.
  - SCIM filter support: `eq` on `userName`/`externalId`/`displayName`, `co` on `displayName`.
  - Paginated list responses with `startIndex` and `count`.
- **SCIM token authentication** — per-client static Bearer tokens (stored SHA-256 hashed). Custom `ScimBearer` authentication scheme and `ScimProvisioning` authorization policy.
  - `POST /api/v1/scim/tokens` — generate a SCIM token for a client (returns raw token once).
  - `GET /api/v1/scim/tokens?clientId={id}` — list tokens (metadata only).
  - `DELETE /api/v1/scim/tokens/{tokenId}?clientId={id}` — revoke a token.
- **SCIM group model** — `ScimGroup` with `DisplayName`, `ExternalId`, `OrganizationId`, and `MemberUserIds`. `IScimGroupStore` interface and `TableScimGroupStore` Azure Table Storage implementation.
- **User externalId** — `AuthUser.ExternalId` property for IdP-assigned identifiers. `UserExternalIds` table provides O(1) lookup by `(clientId, externalId)`.
- **IsActive guard** — deactivated users (`IsActive = false`) are rejected at password login, SAML SSO, OIDC SSO, refresh token exchange, and cookie validation.
- **SCIM-triggered TCC provisioning** — SCIM user creation triggers downstream TCC provisioning if the client has `ProvisioningApps` configured.
- **SCIM documentation** — `docs/scim.md` with IdP setup guides (Entra ID, Okta, OneLogin), endpoint reference, and attribute mapping. Localized stubs for de, es, fr, pt, vi, zh-Hans.

### Changed

- **`AuthUser` model** — added `ExternalId`, `IsActive` (default `true`), `ScimProvisionedByClientId` properties.
- **`IUserStore`** — added `FindByExternalIdAsync`, `ListAsync`, `SetExternalIdAsync`, `RemoveExternalIdAsync` methods.
- **`TableUserStore`** — now accepts a `userExternalIdsTable` parameter; implements new `IUserStore` methods.
- **`ServiceCollectionExtensions`** — registers 4 new tables (`UserExternalIds`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`) and 2 new stores (`IScimTokenStore`, `IScimGroupStore`).
- **`AuthagonalExtensions`** — adds `ScimBearer` auth scheme, `ScimProvisioning` policy, wires SCIM endpoints.
- **Cookie validation** — `OnValidatePrincipal` now rejects inactive users.
- **Token refresh** — `HandleRefreshTokenAsync` now rejects deactivated users.

### Fixed

- **QR code test** — `TotpSetup_ReturnsQrCodeAndSetupToken` test assertion updated from SVG to PNG to match actual QR code output format.

## [0.1.9] — 2026-03-29

### Improved

- **Login UX** — added a "Continue" button after the email field instead of relying on a hidden blur-triggered SSO check. External provider buttons (e.g. Google) now collapse into a compact "Or sign in with..." link once the password field is shown, and can be expanded again by clicking the link.
- **Registration link always visible** — "Don't have an account? Create one" link is now shown below the form at all times, not just after the password field appears.
- **i18n completeness** — added `continue`, `noAccount`, `createAccount`, and `orSignInWith` translation keys across all 8 languages (en, de, es, fr, pt, vi, zh-Hans, tlh).

## [0.1.8] — 2026-03-29

### Added

- **Multi-factor authentication** — TOTP (authenticator apps), WebAuthn/passkeys, and recovery codes. MFA is enforced per-client via `MfaPolicy` on the client configuration (`Disabled`, `Enabled`, `Required`).
  - `POST /api/auth/mfa/verify` — challenge verification (TOTP code, WebAuthn assertion, or recovery code)
  - `GET /api/auth/mfa/status` — enrolled methods for the current user
  - `POST /api/auth/mfa/totp/setup` + `POST /api/auth/mfa/totp/confirm` — TOTP enrollment with QR code
  - `POST /api/auth/mfa/webauthn/setup` + `POST /api/auth/mfa/webauthn/confirm` — passkey enrollment
  - `POST /api/auth/mfa/recovery/generate` — generate 10 one-time recovery codes
  - `DELETE /api/auth/mfa/credentials/{id}` — remove an enrolled method
- **MFA admin endpoints** — `GET /api/v1/profile/{userId}/mfa`, `DELETE /api/v1/profile/{userId}/mfa`, `DELETE /api/v1/profile/{userId}/mfa/{id}` for admin MFA management.
- **MFA policy hook** — `IAuthHook.ResolveMfaPolicyAsync` allows per-user/org override of the client's MFA policy. `IAuthHook.OnMfaVerifiedAsync` fires after successful MFA verification.
- **Setup token flow** — when `MfaPolicy=Required` and the user has no MFA enrolled, login returns a setup token. The MFA setup endpoints accept this token via `X-MFA-Setup-Token` header, allowing enrollment before cookie authentication.
- **MFA frontend** — `MfaChallengePage` (TOTP/passkey/recovery code entry) and `MfaSetupPage` (QR code scanning, passkey registration, recovery code generation) added to the login SPA.
- **Demo: self-service registration** — `POST /api/auth/register` endpoint for the demo server, plus a registration page in the demo login app.
- **Demo: user purge** — background service deletes demo users older than 24 hours.
- **Table Storage restore tool** — `tools/Authagonal.Restore/` reads `.jsonl` backups produced by `authagonal-backup` and restores to Table Storage. Supports `upsert`, `merge`, and `clean` modes.

### Changed

- **Login response** — `POST /api/auth/login` may now return `{ mfaRequired, challengeId, methods, webAuthn }` or `{ mfaSetupRequired, setupToken }` instead of setting a cookie directly.
- **Fido2.AspNet** dependency added for WebAuthn credential verification.
- **QRCoder** dependency added for server-side QR code generation.

## [0.1.5] — 2026-03-29

### Added

- **Integration test suite** — 48 API endpoint tests covering health, discovery/JWKS, auth (login, session, logout, SSO, lockout, password reset), OAuth (client_credentials, authorization code + PKCE, refresh tokens, revocation, userinfo), and admin endpoints (user CRUD, external identity linking). All tests use an in-memory test server with no external dependencies.
- **ASP.NET Identity hash compatibility** — `PasswordHasher` now verifies ASP.NET Identity V3 hashes (PBKDF2 with SHA1/256/384/512, variable iterations and salt sizes). Migrated users are auto-upgraded to the native format on next login.
- **Configurable admin scope** — `AdminApi:Scope` setting (default `authagonal-admin`) controls which JWT scope grants admin access. Set to `projects-identity-admin` for IdentityServer migration compatibility.
- **`NullEmailService`** — no-op email service is now the default. Register a real `IEmailService` (e.g., the built-in SendGrid `EmailService`) before `AddAuthagonal()` to enable email delivery.

## [0.1.4] — 2026-03-29

### Added

- **npm package README** — `@drawboard/authagonal-login` now includes a comprehensive README covering installation, quick start, page customization, full API client reference, branding, i18n, and exports.
- **Docs favicon** — documentation site now uses the Authagonal logo as its browser tab icon.

## [0.1.3] — 2026-03-29

### Changed

- **Demo consumes published packages** — demo `CustomAuthServer` now references NuGet packages from nuget.org and `@drawboard/authagonal-login` from npm, instead of building from source. The Docker build no longer needs the login-app source tree.
- **CI release workflow** — consolidated `nuget.yml`, `npm.yml`, and tag-triggered Docker builds into a single `release.yml` with proper job ordering: publish NuGet → wait for indexing → publish npm → wait for indexing → build Docker images → deploy. Eliminates the race condition where Docker builds could start before packages were available on registries.
- **Docker workflow** — `docker.yml` now only triggers on `master` branch pushes (tag builds handled by `release.yml`).

## [0.1.1] — 2026-03-29

### Fixed

- **i18n module duplication** — consumers importing `useTranslation` from their own `react-i18next` copy got a different instance than the one initialized by the base package. Fixed by re-exporting `useTranslation` from `@drawboard/authagonal-login`.
- **OAuth returnUrl dropped on SSO redirect** — the authorize endpoint generated a full URL as the returnUrl, which was rejected by both client-side `isSafeReturnUrl()` and server-side `SanitizeReturnUrl()` (both require relative paths). Fixed by using a relative path.
- **Language detection not persisting** — added `localStorage` to `i18next-browser-languagedetector` order and caches arrays.
- **OIDC error display** — login page now reads `error` / `error_description` from URL params and displays them.

### Added

- **Localizable branding strings** — `welcomeTitle` and `welcomeSubtitle` in `branding.json` now accept `LocalizedString` (a plain string or a `{ "en": "...", "es": "..." }` object). New `resolveLocalized()` helper resolves the best match for the current language.
- **Sign-out button** — login page detects existing sessions and shows a "Signed in as" view with a sign-out button, instead of showing the login form.
- **NuGet package READMEs** — `Authagonal.Server`, `Authagonal.Core`, and `Authagonal.Storage` now include README files displayed on nuget.org.
- **i18n keys** — added `signedInAs`, `signedInMessage`, `signOut`, `welcomeTitle`, `welcomeSubtitle`, `continueWith`, `or` to all 7 language files.

## [0.1.0] — 2026-03-29

### Added

- **Docker packaging** — multi-stage `Dockerfile` builds the React SPA and .NET server into a single image. SPA served as static files from `wwwroot/` on the same origin as the API.
- **`Dockerfile.migration`** — separate image for the SQL Server → Table Storage migration tool.
- **`docker-compose.yml`** — local development setup with Azurite storage emulator.
- **Static file serving** — `UseDefaultFiles()`, `UseStaticFiles()`, and `MapFallbackToFile("index.html")` added to `Program.cs` for SPA hosting.
- **TCC provisioning system** — replaces the single-webhook provisioning with a Try-Confirm-Cancel pattern:
  - N provisioning apps defined in configuration (`ProvisioningApps` section) with callback URLs and API keys.
  - Clients declare which apps they provision into via `ProvisioningApps` field.
  - Provisioning runs at the authorize endpoint — before a code is issued, the user is provisioned into all required apps.
  - Per-user/per-app tracking in the `UserProvisions` table prevents re-provisioning on subsequent logins.
  - Try phase: calls each app's `/try` endpoint with user details, app can approve or reject.
  - Confirm phase: on all-approve, calls `/confirm` on each app to commit.
  - Cancel phase: on any failure, calls `/cancel` on successful tries to clean up.
  - Partial confirm failure: stores provision records for confirmed apps so only failed ones are retried.
- **`IProvisioningOrchestrator`** interface and `TccProvisioningOrchestrator` implementation.
- **`IUserProvisionStore`** interface and `TableUserProvisionStore` for Azure Table Storage.
- **`UserProvisionEntity`** — table entity keyed by `(userId, appId)`.
- **Deprovision on user delete** — `DELETE /api/v1/profile/{userId}` now calls `DeprovisionAllAsync` to notify all downstream apps.
- **Runtime branding** — login SPA reads `/branding.json` at startup for customization without rebuilding:
  - `appName` — header text and browser tab title.
  - `logoUrl` — replaces text header with an image.
  - `primaryColor` — buttons, links, focus rings (via CSS custom properties).
  - `showForgotPassword` — toggle the forgot password link.
  - `customCssUrl` — load additional CSS for deeper styling.
- **CSS custom properties** — primary color exposed as `--color-primary`, used throughout styles via `color-mix()` for hover/focus variants.
- **GitHub Pages documentation site** — overview, installation, quickstart, configuration, branding, provisioning, SAML, OIDC federation, admin API, auth API, and migration guides.
- **`IAuthHook` extensibility point** — lifecycle hooks for authentication events. Implementations are called on login success/failure, user creation, and token issuance. Throw from a hook to abort the operation (e.g., reject a login). Default implementation is a no-op (`NullAuthHook`).
  - `OnUserAuthenticatedAsync` — after password, SAML, or OIDC login.
  - `OnUserCreatedAsync` — after user creation via SSO or admin API.
  - `OnLoginFailedAsync` — on invalid credentials or lockout.
  - `OnTokenIssuedAsync` — when tokens are issued via the token endpoint.
- **Composable extension methods** — `AddAuthagonal()`, `UseAuthagonal()`, `MapAuthagonalEndpoints()` allow hosting Authagonal as a library in any ASP.NET Core application. Override `IEmailService`, `IAuthHook`, `IProvisioningOrchestrator`, or `ISecretProvider` by registering before `AddAuthagonal()`.
- **Demo: custom-server** — shows hosting Authagonal with custom `IAuthHook` (audit logging), custom `IEmailService` (console output), custom branding, and custom endpoints.
- **Demo: sample-app** — shows a client application (ASP.NET API + React SPA) authenticating via Authagonal using OIDC authorization code + PKCE, with protected API calls using JWT bearer tokens.
- **Demo: custom-server frontend** — custom login-app that installs `@drawboard/authagonal-login` as an npm dependency and overrides `LoginPage` (adds Terms of Service checkbox) and `AuthLayout` (adds branded footer), while reusing base `ForgotPasswordPage` and `ResetPasswordPage` as-is.
- **Configurable password policy** — `PasswordPolicy` configuration section controls min length, character requirements. `GET /api/auth/password-policy` endpoint exposes rules dynamically. Frontend fetches policy instead of hardcoding. Password validation now enforced on admin user registration too.
- **SAML/OIDC providers from configuration** — `SamlProviders` and `OidcProviders` config sections seed identity providers on startup (same pattern as `Clients`). SSO domain mappings are registered automatically from `AllowedDomains`.
- **`ProviderSeedService`** — `IHostedService` that seeds SAML and OIDC providers from configuration, with secret protection via `ISecretProvider`.
- **Login-app component library exports** — `@drawboard/authagonal-login` now exports all components, pages, branding hooks, API client, and types via `src/index.ts` with proper `exports` field in package.json. Consumers can `npm install` and import individual pieces.
- **CI/CD** — GitHub Actions workflows for Docker Hub publishing (`drawboardci/authagonal`, `drawboardci/authagonal-migration`) and npm publishing (`@drawboard/authagonal-login`).
- **i18n** — login SPA supports 7 languages (English, Chinese Simplified, German, French, Spanish, Vietnamese, Portuguese) with browser detection and language selector.
- **NuGet packaging** — `Authagonal.Server`, `Authagonal.Core`, `Authagonal.Storage` published to nuget.org.

### Changed

- **CSP header** — changed from `default-src 'none'` to `default-src 'self'; frame-ancestors 'none'; object-src 'none'` to allow the SPA to load resources from the same origin.
- **`OAuthClient` model** — added `ProvisioningApps` list field.
- **`ClientEntity`** — added `ProvisioningAppsJson` column (JSON-serialized string array, same pattern as other list fields).
- **`ServiceCollectionExtensions`** — registers `UserProvisions` table and `IUserProvisionStore`.
- **Authorize endpoint** — now injects `IUserStore` and `IProvisioningOrchestrator`, runs TCC provisioning between auth check and code issuance.
- **Admin `RegisterUser`** — no longer calls provisioning webhook; creates users with a generated GUID. Provisioning happens at authorize time.
- **Admin `DeleteUser`** — calls `IProvisioningOrchestrator.DeprovisionAllAsync` instead of `NotifyUserDeletedAsync`.
- **`Program.cs`** — refactored from inline setup to composable extension methods (`AddAuthagonal`, `UseAuthagonal`, `MapAuthagonalEndpoints`). Now 13 lines.
- **Token endpoint** — fires `IAuthHook.OnTokenIssuedAsync` on successful token issuance.
- **Auth endpoints** — fire `IAuthHook.OnUserAuthenticatedAsync` / `OnLoginFailedAsync`.
- **SAML/OIDC endpoints** — fire `IAuthHook.OnUserCreatedAsync` and `OnUserAuthenticatedAsync`.
- **Admin user endpoint** — fires `IAuthHook.OnUserCreatedAsync` on user registration; now validates password strength.
- **`PasswordValidator`** — refactored from hardcoded constants to accept a `PasswordPolicy` parameter.
- **`ResetPasswordPage`** — fetches password requirements from `GET /api/auth/password-policy` instead of hardcoding rules.
- **Scope rename** — `projects-identity-admin` renamed to `authagonal-admin`.

### Removed

- **`IUserProvisioningService`** — replaced by `IProvisioningOrchestrator`.
- **`UserProvisioningService`** — replaced by `TccProvisioningOrchestrator`.
- **Provisioning in SAML/OIDC flows** — SSO endpoints now just create users in Authagonal without external provisioning calls. Provisioning is deferred to the authorize endpoint where the client (and its required apps) are known.
- **`Provisioning:WebhookUrl` / `Provisioning:ApiKey` config** — replaced by per-app configuration under `ProvisioningApps`.

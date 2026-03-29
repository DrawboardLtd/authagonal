# Changelog

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

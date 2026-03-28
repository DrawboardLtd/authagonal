# Changelog

## [Unreleased] — 2026-03-28

### Added

- **Docker packaging** — multi-stage `Dockerfile` builds the React SPA and .NET server into a single image. SPA served as static files from `wwwroot/` on the same origin as the API.
- **`Dockerfile.migration`** — separate image for the SQL Server → Table Storage migration tool.
- **`docker-compose.yml`** — local development setup with Azurite storage emulator.
- **`.dockerignore`** — excludes build artifacts, node_modules, .git, IDE files.
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

### Changed

- **CSP header** — changed from `default-src 'none'` to `default-src 'self'; frame-ancestors 'none'; object-src 'none'` to allow the SPA to load resources from the same origin.
- **`OAuthClient` model** — added `ProvisioningApps` list field.
- **`ClientEntity`** — added `ProvisioningAppsJson` column (JSON-serialized string array, same pattern as other list fields).
- **`ServiceCollectionExtensions`** — registers `UserProvisions` table and `IUserProvisionStore`.
- **Authorize endpoint** — now injects `IUserStore` and `IProvisioningOrchestrator`, runs TCC provisioning between auth check and code issuance.
- **Admin `RegisterUser`** — no longer calls provisioning webhook; creates users with a generated GUID. Provisioning happens at authorize time.
- **Admin `DeleteUser`** — calls `IProvisioningOrchestrator.DeprovisionAllAsync` instead of `NotifyUserDeletedAsync`.

### Removed

- **`IUserProvisioningService`** — replaced by `IProvisioningOrchestrator`.
- **`UserProvisioningService`** — replaced by `TccProvisioningOrchestrator`.
- **Provisioning in SAML/OIDC flows** — SSO endpoints now just create users in Authagonal without external provisioning calls. Provisioning is deferred to the authorize endpoint where the client (and its required apps) are known.
- **`Provisioning:WebhookUrl` / `Provisioning:ApiKey` config** — replaced by per-app configuration under `ProvisioningApps`.

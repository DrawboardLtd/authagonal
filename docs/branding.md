---
layout: default
title: Branding
---

# Branding the Login UI

The login SPA is runtime-configurable via a `branding.json` file served from the web root. No rebuild is required — just mount your config and assets.

## How It Works

On startup, the SPA fetches `/branding.json`. If the file doesn't exist or is unreachable, defaults are used. (A host server can also inline the config as a `<script type="application/json" id="authagonal-boot">` boot payload; when present, the SPA reads it instead of fetching.) The config controls:

- Application name (shown in the header and page title)
- Logo image, with an optional per-mode background "chip"
- Primary color (buttons, links, focus rings), with an optional dark-mode variant
- Page and card background colors, per mode
- Forgot password and registration link visibility
- Dark mode default (light / follow OS / dark)
- Language selector options
- The "Powered by Authagonal" footer
- Custom CSS for deeper styling

## Configuration

Place a `branding.json` file in the `wwwroot/` directory (or mount it into the Docker container):

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
  "darkPrimaryColor": "#3b82f6",
  "darkMode": "auto",
  "supportEmail": "help@acme.com",
  "showForgotPassword": true,
  "customCssUrl": "/branding/custom.css"
}
```

### Options

| Property | Type | Default | Description |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Displayed in the header and browser tab title |
| `logoUrl` | `string \| null` | `null` | URL to a logo image. When set, replaces the text header. |
| `primaryColor` | `string` | `"#2563eb"` | Hex color for buttons, links, and focus indicators |
| `supportEmail` | `string \| null` | `null` | Support contact email (reserved for future use) |
| `showForgotPassword` | `boolean` | `true` | Show/hide the "Forgot password?" link on the login page |
| `showRegistration` | `boolean` | `false` | Show/hide the self-service registration link |
| `customCssUrl` | `string \| null` | `null` | URL to a custom CSS file loaded after the default styles |
| `welcomeTitle` | `LocalizedString` | `null` | Override the login page title (plain string or `{ "en": "...", "de": "..." }`) |
| `welcomeSubtitle` | `LocalizedString` | `null` | Override the login page subtitle |
| `languages` | `array \| null` | `null` | Language selector options (`[{ "code": "en", "label": "English" }, ...]`). `null` shows all shipped languages except novelty locales (see [Localization](localization)). |
| `poweredBy` | `boolean` | `true` | Show/hide the "Powered by Authagonal" footer on the auth pages |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Default theme when the visitor hasn't picked one: `"off"` (light only), `"auto"` (follow the OS preference), `"force"` (always dark). The visitor's theme toggle still wins. |
| `lightBg` | `string \| null` | `null` | Page background color in light mode |
| `lightCardBg` | `string \| null` | `null` | Card/form background color in light mode |
| `darkBg` | `string \| null` | `null` | Page background color in dark mode |
| `darkCardBg` | `string \| null` | `null` | Card/form background color in dark mode |
| `darkPrimaryColor` | `string \| null` | `null` | Overrides `primaryColor` in dark mode |
| `lightLogoBg` | `string \| null` | `null` | Logo chip background in light mode (see below) |
| `darkLogoBg` | `string \| null` | `null` | Logo chip background in dark mode (see below) |

Color values must be a hex color (`#rgb`, `#rrggbb`, `#rrggbbaa`) or an `rgb()`/`rgba()`/`hsl()`/`hsla()` expression; anything else is ignored. The per-mode colors are injected as a `<style id="branding-theme-vars">` rule after the bundled styles (light values at `:root`, dark values at `.dark`), so a dark value can differ from its light counterpart.

### Logo Background Chip

If your logo has white or transparent artwork it can disappear against the light card. Set `lightLogoBg` and/or `darkLogoBg` to render the logo inside a padded, rounded "chip" with that background color:

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

The chip (a `data-auth="logo-chip"` wrapper driven by the `--auth-logo-bg` CSS variable) only gets its padding and background when a logo background is configured, so tenants that don't set one see the logo flush on the card exactly as before. The two fields are independent: set only `lightLogoBg` to chip the logo in light mode and leave it bare in dark mode.

## Docker Example

Mount your branding files into the container:

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

Or with docker-compose:

```yaml
services:
  authagonal:
    build: .
    ports:
      - "8080:8080"
    volumes:
      - ./my-branding/branding.json:/app/wwwroot/branding.json
      - ./my-branding/assets:/app/wwwroot/branding
    environment:
      - Storage__ConnectionString=...
      - Issuer=https://auth.example.com
```

## Custom CSS

The `customCssUrl` option loads an additional stylesheet after the default styles, so your rules take precedence. Useful for changing fonts, adjusting spacing, or restyling specific elements. The URL must be same-origin (relative URLs like `/branding/custom.css` are fine); cross-origin stylesheets are silently skipped.

### CSS Custom Properties

The login UI exposes several CSS custom properties for fine-grained control:

| Property | Default | Description |
|---|---|---|
| `--brand-primary` | `#2563eb` | Primary color for buttons, links, focus rings |
| `--auth-bg` | `#f3f4f6` | Page background color |
| `--auth-card-bg` | `#ffffff` | Card/form background color |
| `--auth-logo-bg` | `transparent` | Logo chip background (chip padding only appears when a logo bg is configured) |
| `--auth-radius` | `0.5rem` | Border radius for the auth card |
| `--auth-font` | *(inherit; system font stack)* | Font family for the auth card |
| `--auth-heading` | `#111827` | Heading text color |

The color variables here map directly to config fields (`primaryColor`, `lightBg`/`darkBg`, `lightCardBg`/`darkCardBg`, `lightLogoBg`/`darkLogoBg`), so prefer the config for simple color changes and reserve custom CSS for everything else.

Override them in your custom CSS:

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

The login UI uses Tailwind CSS. Custom CSS can target standard HTML elements and Tailwind utility classes. The exported UI components (`Button`, `Input`, `Card`, `Alert`, etc.) use Tailwind internally.

## Dark Mode

The login SPA ships with light, dark, and **system** themes. The theme toggle is always visible in the layout. User selection is persisted to `localStorage` under the `auth-theme` key.

### How It Works

- **Default** — until the visitor picks a theme, the `darkMode` branding option sets the default: `"off"` (light), `"auto"` (system, the default), or `"force"` (dark). Once the visitor uses the toggle, their choice always wins.
- **Detection** — when the theme is "system", the SPA observes `window.matchMedia('(prefers-color-scheme: dark)')` and re-applies the theme automatically as the OS preference changes.
- **Application** — the SPA toggles a `.dark` class on `<html>`. Tailwind's dark variant (`&:where(.dark, .dark *)`) activates the dark styles compiled into every component.
- **Persistence** — explicit "light" / "dark" / "system" choices are stored in `localStorage`.

### CSS Variables

Light values are declared at `:root`; dark-mode overrides are scoped to `.dark`, so tenant branding in `customCssUrl` always takes precedence when supplied.

| Variable | Light | Dark |
|---|---|---|
| `--auth-bg` | `#f3f4f6` (or `lightBg`) | `#030712` (or `darkBg`) |
| `--auth-card-bg` | `#ffffff` (or `lightCardBg`) | `#111827` (or `darkCardBg`) |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent` (or `lightLogoBg`) | `transparent` (or `darkLogoBg`) |
| `--brand-primary` | `#2563eb` (or `primaryColor`) | the light value (or `darkPrimaryColor`) |

### Disabling or Overriding

Tenant branding always wins. To force a single theme, set your own values in `customCssUrl`:

```css
/* Force dark palette regardless of user choice */
:root {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
.dark {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

To remove the theme toggle entirely, use the npm package path — import `AuthLayout` and render without the toggle, or fork the SPA.

### Data Attributes

All login form elements have `data-auth` attributes for CSS targeting and test automation:

| Attribute | Element |
|---|---|
| `data-auth="page"` | Main page wrapper |
| `data-auth="header"` | Header section |
| `data-auth="logo-chip"` | Wrapper around the logo image (padded only when a logo background is set) |
| `data-auth="logo"` | Logo image |
| `data-auth="app-name"` | App name heading |
| `data-auth="content"` | Main content area |
| `data-auth="languages"` | Language selector |
| `data-auth="language-trigger"` | Language selector trigger button |
| `data-auth="theme-toggle"` | Light/system/dark theme toggle |
| `data-auth="powered-by"` | "Powered by Authagonal" footer |

Target these in your custom CSS:

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### Example: Custom Background and Font

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Customization Tiers

| Level | What You Do | Update Path |
|---|---|---|
| **Config only** | Mount `branding.json` + logo | Seamless — update the Docker image, keep your mounts |
| **Config + CSS** | Add `customCssUrl` with style overrides | Same — CSS classes are stable |
| **npm package** | `npm install @authagonal/login`, customize `branding.json`, build into `wwwroot/` | Updatable — `npm update` pulls new versions |
| **Fork the SPA** | Clone `login-app/`, modify source, build your own | You own the UI — server updates are independent |
| **Write your own** | Build a completely custom frontend against the auth API | Full control — see [Auth API](auth-api) for the contract |

See `demos/custom-server/` for a working example with custom branding (green theme, "Acme Corp").

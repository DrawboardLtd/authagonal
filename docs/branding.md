---
layout: default
title: Branding
---

# Branding the Login UI

The login SPA is runtime-configurable via a `branding.json` file served from the web root. No rebuild is required — just mount your config and assets.

## How It Works

On startup, the SPA fetches `/branding.json`. If the file doesn't exist or is unreachable, defaults are used. The config controls:

- Application name (shown in the header and page title)
- Logo image
- Primary color (buttons, links, focus rings)
- Forgot password link visibility
- Custom CSS for deeper styling

## Configuration

Place a `branding.json` file in the `wwwroot/` directory (or mount it into the Docker container):

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
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
| `languages` | `array \| null` | `null` | Language selector options (`[{ "code": "en", "label": "English" }, ...]`) |

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

The `customCssUrl` option loads an additional stylesheet after the default styles, so your rules take precedence. Useful for changing fonts, adjusting spacing, or restyling specific elements.

### CSS Custom Properties

The login UI exposes several CSS custom properties for fine-grained control:

| Property | Default | Description |
|---|---|---|
| `--brand-primary` | `#2563eb` | Primary color for buttons, links, focus rings |
| `--auth-bg` | `#f3f4f6` | Page background color |
| `--auth-card-bg` | `white` | Card/form background color |
| `--auth-radius` | `0.5rem` | Border radius for cards and inputs |
| `--auth-font` | *(system)* | Font family |
| `--auth-heading` | `#111827` | Heading text color |

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

- **Detection** — when the user picks "system", the SPA observes `window.matchMedia('(prefers-color-scheme: dark)')` and re-applies the theme automatically as the OS preference changes.
- **Application** — the SPA toggles a `.dark` class on `<html>`. Tailwind's dark variant (`&:where(.dark, .dark *)`) activates the dark styles compiled into every component.
- **Persistence** — explicit "light" / "dark" choices are stored in `localStorage`. "system" tracks the OS preference live.

### CSS Variables

Light values are declared at `:root`; dark-mode overrides are scoped to `.dark`, so tenant branding in `customCssUrl` always takes precedence when supplied.

| Variable | Light | Dark |
|---|---|---|
| `--auth-bg` | `#f3f4f6` | `#030712` |
| `--auth-card-bg` | `#ffffff` | `#111827` |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--brand-primary` | `#2563eb` | `#2563eb` (tenant branding wins) |

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
| `data-auth="logo"` | Logo image |
| `data-auth="app-name"` | App name heading |
| `data-auth="content"` | Main content area |
| `data-auth="languages"` | Language selector |

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
| **npm package** | `npm install @drawboard/authagonal-login`, customize `branding.json`, build into `wwwroot/` | Updatable — `npm update` pulls new versions |
| **Fork the SPA** | Clone `login-app/`, modify source, build your own | You own the UI — server updates are independent |
| **Write your own** | Build a completely custom frontend against the auth API | Full control — see [Auth API](auth-api) for the contract |

See `demos/custom-server/` for a working example with custom branding (green theme, "Acme Corp").

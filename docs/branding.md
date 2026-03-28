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
| `customCssUrl` | `string \| null` | `null` | URL to a custom CSS file loaded after the default styles |

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

### Available CSS Classes

| Class | Element |
|---|---|
| `.auth-container` | Full-page wrapper (flex center) |
| `.auth-card` | The login card (white box with shadow) |
| `.auth-logo` | Logo/title area |
| `.auth-logo h1` | Text header (when no logo image) |
| `.auth-logo-img` | Logo image (when `logoUrl` is set) |
| `.auth-title` | Page titles ("Sign in", "Reset your password") |
| `.auth-subtitle` | Secondary text below titles |
| `.form-group` | Form field wrapper |
| `.form-group label` | Field labels |
| `input` | Text inputs |
| `.btn-primary` | Primary action button |
| `.btn-secondary` | Secondary button (e.g., "Continue with SSO") |
| `.alert-error` | Error messages |
| `.alert-success` | Success messages |
| `.link` | Text links |
| `.sso-notice` | SSO detection notice |
| `.password-requirements` | Password strength list |

### CSS Custom Properties

The primary color is exposed as a CSS custom property. You can override it in your custom CSS instead of using `branding.json`:

```css
:root {
  --color-primary: #059669;
}
```

### Example: Custom Background and Font

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}

.auth-card {
  border-radius: 16px;
  box-shadow: 0 4px 24px rgba(0, 0, 0, 0.2);
}

.auth-logo h1 {
  font-family: 'Inter', sans-serif;
  font-weight: 800;
}
```

## Customization Tiers

| Level | What You Do | Update Path |
|---|---|---|
| **Config only** | Mount `branding.json` + logo | Seamless — update the Docker image, keep your mounts |
| **Config + CSS** | Add `customCssUrl` with style overrides | Same — CSS classes are stable |
| **Fork the SPA** | Clone `login-app/`, modify source, build your own | You own the UI — server updates are independent |
| **Write your own** | Build a completely custom frontend against the auth API | Full control — see [Auth API](auth-api) for the contract |

[← Back to home](.)

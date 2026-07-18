# @authagonal/bff

Backend-for-Frontend (BFF) for SPAs that authenticate with **Authagonal**, for Node — Express and
Next.js.

Your React/Vue/Svelte app should never hold access or refresh tokens: anything in JS-reachable storage
is exposed to XSS. This package is a confidential OIDC client you run on your own backend. It runs the
authorization-code + PKCE flow server-side, keeps the tokens in a server-side session, and gives the
browser nothing but an httpOnly session cookie — the pattern the IETF *OAuth 2.0 for Browser-Based
Apps* BCP recommends. It's the Node twin of the .NET `Authagonal.Bff` package and speaks the same
protocol.

```
npm install @authagonal/bff
```

## Express

```ts
import express from 'express';
import { authagonalBff } from '@authagonal/bff/express';

const app = express();
app.set('trust proxy', 1); // if behind a reverse proxy / ingress

app.use(authagonalBff({
  authority:     'https://acme.authagonal.io',   // your tenant auth host
  clientId:      process.env.BFF_CLIENT_ID!,
  clientSecret:  process.env.BFF_CLIENT_SECRET!,
  scope:         ['openid', 'profile', 'email', 'offline_access'], // offline_access enables refresh
  cookieSecret:  process.env.BFF_COOKIE_SECRET!, // used to encrypt the session cookie
  postLogoutRedirectUri: 'https://app.acme.com/',
}));

app.use(express.static('dist')); // your SPA
app.listen(3000);
```

## Next.js (App Router)

`app/bff/[...bff]/route.ts`:

```ts
import { createBffRoute } from '@authagonal/bff/next';

export const { GET, POST } = createBffRoute({
  authority:    'https://acme.authagonal.io',
  clientId:     process.env.BFF_CLIENT_ID!,
  clientSecret: process.env.BFF_CLIENT_SECRET!,
  scope:        ['openid', 'profile', 'email', 'offline_access'],
  cookieSecret: process.env.BFF_COOKIE_SECRET!,
  postLogoutRedirectUri: 'https://app.acme.com/',
});

export const runtime = 'nodejs'; // server-side session + client secret
```

## Endpoints (mounted under `/bff`)

| Route | Purpose |
|---|---|
| `GET /bff/login?returnUrl=/` | Start login; redirects to Authagonal. |
| `GET /bff/callback` | OIDC redirect URI (handled for you). |
| `GET /bff/user` | `{ isAuthenticated, claims, sessionExpiresAt }`. Requires the anti-forgery header. |
| `GET\|POST /bff/logout` | Ends the session locally + at Authagonal. |
| `POST /bff/backchannel-logout` | OIDC back-channel logout consumer (kills sessions). |

Register a **BFF client** in the Authagonal portal (confidential + PKCE + `offline_access`) with
redirect URI `https://app.acme.com/bff/callback` and post-logout redirect `https://app.acme.com/`.
For subject-wide "log out everywhere", register it with `BackChannelLogoutSessionRequired=false`.

## From the browser

Every non-navigation call must carry a static anti-forgery header (defends against CSRF alongside
`SameSite=Lax`):

```js
const me = await fetch('/bff/user', { headers: { 'X-Authagonal-Bff': '1' } }).then(r => r.json());
if (!me.isAuthenticated) location.href = '/bff/login?returnUrl=' + encodeURIComponent(location.pathname);
```

Log in / out by navigating (not fetching): `location.href = '/bff/login'` / `'/bff/logout'`.

## Sessions & scaling

Sessions default to an in-memory store, fine for a single instance. Pass a shared `sessionStore`
(implement `IBffSessionStore`, e.g. over Redis) to run more than one instance.

## Extension points (the hosted seam)

`sessionStore` (`IBffSessionStore`), `cookieProtector` (`ICookieProtector`), and the core `OidcClient`
are all replaceable. See `docs/bff.md` in the authagonal-cloud repo for the full protocol contract.

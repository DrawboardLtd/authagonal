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

## The proxy and forwarding metadata

`{basePath}/api/**` forwards to a configured `upstreams` entry with the session's access token
attached. Inbound `x-forwarded-*` / `forwarded` / `x-real-ip` headers are **stripped** — copied through
verbatim they would let any script on the SPA's origin tell the upstream that this BFF, a trusted
reverse proxy, had vouched for whatever client IP, host and scheme the caller chose.

They are then **re-asserted** from the proxy's own state, because a missing `X-Forwarded-For` is not a
neutral outcome: the upstream falls back to the TCP peer, so every user of the SPA is attributed to the
BFF's own address, one user's failed attempts against a per-IP-limited endpoint buy a 429 for everyone,
and every audit row names the pod instead of the actor.

- `X-Forwarded-Proto` / `X-Forwarded-Host` — from the same values the BFF derives its own origin from.
- `X-Forwarded-For` — Express uses `req.ip`, which honours the host app's own `app.set('trust proxy')`
  setting; the trust boundary stays the host's to declare. A Web `Request` (the Next adapter) has no
  socket to read, so supply it there:

```ts
authagonalBff({ /* … */ clientIp: () => headers().get('x-real-ip') ?? undefined })
```

  With nothing to assert the header is simply absent — the inbound one is never re-emitted, since that
  is the pass-through the strip exists to prevent.

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

A custom store must honour the `tenantKey` argument on `removeBySid` / `removeBySubject`: `sub` and
`sid` are unique only within an issuer, and back-channel logout accepts a validly signed token from
any tenant this BFF serves. A store that indexes on the bare value lets one tenant's IdP log out
another tenant's users.

**Running more than one instance? Implement `acquireRefreshLock` / `releaseRefreshLock` too.** The
refresh single-flight is otherwise per-process — a `Map` on one instance — while the session and its
rotating refresh token live in the store every replica shares. Two replicas can read the same session,
both see it needs refreshing, and both redeem the same refresh token. That is indistinguishable from a
stolen-token replay, and an IdP's answer to replay is to revoke the whole grant family — so this
deployment can sign a user out everywhere under nothing more than concurrent load. `SET NX PX` on Redis
is enough; all the coordinator needs is "at most one holder for a short time". Without it you are
relying on the IdP's refresh-reuse grace window, which in Authagonal's own server host defaults to **0
— strict**.

**`sessionLifetimeSeconds` is enforced by the library, not by your store.** The absolute cap is checked
on every `ensureFresh`, so a store that does not evict expired rows cannot extend a session — it should
still evict them, for retention, but the policy does not depend on it.

## Extension points (the hosted seam)

`sessionStore` (`IBffSessionStore`), `cookieProtector` (`ICookieProtector`), and the core `OidcClient`
are all replaceable. See `docs/bff.md` in the authagonal-cloud repo for the full protocol contract.

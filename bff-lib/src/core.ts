import { decodeJwt, type JWTPayload } from 'jose';
import type { AuthagonalBffOptions, ResolvedBffOptions } from './options.js';
import { resolveOptions } from './options.js';
import { type BffSession, type IBffSessionStore, MemorySessionStore } from './session.js';
import { type ICookieProtector, JoseCookieProtector } from './cookies.js';
import { OidcClient, BffTokenError, oidcClientCache, randomToken, codeChallenge } from './oidc.js';
import { RefreshCoordinator } from './refresh.js';
import { type BffTenantConfig, type IBffTenantResolver, StaticBffTenantResolver } from './tenant.js';

/** Attributes for a Set-Cookie. */
export interface CookieOptions {
  httpOnly?: boolean;
  secure?: boolean;
  sameSite?: 'lax' | 'strict' | 'none';
  path?: string;
  maxAgeSeconds?: number;
}

/** Minimal request/response abstraction the handlers run against; the Express and Next adapters implement it. */
export interface HttpCtx {
  readonly method: string;
  /** Request pathname (no query). */
  readonly path: string;
  readonly query: URLSearchParams;
  /** `scheme://host` of this request, used to build the redirect_uri. */
  readonly origin: string;
  getCookie(name: string): string | undefined;
  setCookie(name: string, value: string, opts: CookieOptions): void;
  deleteCookie(name: string, opts: CookieOptions): void;
  getHeader(name: string): string | undefined;
  setHeader(name: string, value: string): void;
  readForm(): Promise<URLSearchParams>;
  redirect(url: string): void;
  json(body: unknown, status?: number): void;
  text(body: string, status?: number, contentType?: string): void;
}

export interface BffDeps {
  o: ResolvedBffOptions;
  store: IBffSessionStore;
  protector: ICookieProtector;
  tenants: IBffTenantResolver;
  /** Memoized OIDC client per authority (one auth host per tenant in multi-tenant mode). */
  oidcFor: (authority: string) => OidcClient;
  refresher: RefreshCoordinator;
  log: (msg: string, err?: unknown) => void;
}

export function buildDeps(options: AuthagonalBffOptions): BffDeps {
  const o = resolveOptions(options);
  const store = options.sessionStore ?? new MemorySessionStore();
  const protector = options.cookieProtector ?? new JoseCookieProtector(options.cookieSecret!);
  const tenants = options.tenantResolver ?? new StaticBffTenantResolver(o);
  const oidcFor = oidcClientCache();
  const refresher = new RefreshCoordinator(tenants, oidcFor, store, o);
  const log = (msg: string, err?: unknown) =>
    err ? console.warn(`[authagonal-bff] ${msg}`, err) : console.info(`[authagonal-bff] ${msg}`);
  return { o, store, protector, tenants, oidcFor, refresher, log };
}

const CORRELATION_PURPOSE = 'agbff-correlation-v1';
const BACKCHANNEL_EVENT = 'http://schemas.openid.net/event/backchannel-logout';
const PROTOCOL_CLAIMS = new Set([
  'iss', 'aud', 'exp', 'iat', 'nbf', 'nonce', 'at_hash', 'c_hash', 's_hash',
  'azp', 'jti', 'sid', 'auth_time', 'acr', 'amr', 'typ',
]);

/** Dispatch a BFF request. Returns false if the path isn't a BFF route (so an Express host can call next()). */
export async function routeBff(ctx: HttpCtx, d: BffDeps): Promise<boolean> {
  const base = d.o.basePath;
  const p = ctx.path;
  if (ctx.method === 'GET' && p === `${base}/login`) { await handleLogin(ctx, d); return true; }
  if (ctx.method === 'GET' && p === d.o.callbackPath) { await handleCallback(ctx, d); return true; }
  if (ctx.method === 'GET' && p === `${base}/user`) { await handleUser(ctx, d); return true; }
  if ((ctx.method === 'GET' || ctx.method === 'POST') && p === `${base}/logout`) { await handleLogout(ctx, d); return true; }
  if (ctx.method === 'POST' && p === `${base}/backchannel-logout`) { await handleBackchannel(ctx, d); return true; }
  return false;
}

/** True if the path is the BFF token-injecting proxy route (`{basePath}/api/**`). */
export function isProxyPath(path: string, o: ResolvedBffOptions): boolean {
  const apiBase = `${o.basePath}/api`;
  return path === apiBase || path.startsWith(`${apiBase}/`);
}

/** A resolved proxy target + bearer token, or an HTTP error status. */
export type ProxyDecision = { targetUrl: string; accessToken: string } | { error: number };

/** Authorize + resolve a proxy request: anti-forgery header, session, single-flight refresh, upstream
 * match. The adapter performs the actual streaming forward with the returned target + token. */
export async function authorizeProxy(ctx: HttpCtx, d: BffDeps): Promise<ProxyDecision> {
  if (!hasAntiForgery(ctx, d.o)) return { error: 401 };
  const sessionId = ctx.getCookie(d.o.cookieName);
  if (!sessionId) return { error: 401 };
  const session = await d.store.get(sessionId);
  if (!session) return { error: 401 };
  const fresh = await d.refresher.ensureFresh(session);
  if (!fresh) return { error: 401 };

  const apiBase = `${d.o.basePath}/api`;
  const apiPath = ctx.path.length > apiBase.length ? ctx.path.slice(apiBase.length) : '';
  const upstream = d.o.upstreams.find((u) => apiPath.startsWith(u.prefix));
  if (!upstream) return { error: 404 };
  const qs = ctx.query.toString();
  const targetUrl = upstream.targetBaseUrl.replace(/\/+$/, '') + apiPath + (qs ? `?${qs}` : '');
  return { targetUrl, accessToken: fresh.accessToken };
}

/** Headers the proxy never forwards (hop-by-hop + ones we set/strip: cookie, authorization, host). */
export const PROXY_STRIP = new Set([
  'connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization', 'te', 'trailer',
  'transfer-encoding', 'upgrade', 'host', 'cookie', 'authorization',

  // Forwarding metadata, stripped for the same reason the .NET proxy strips it: copied through
  // verbatim, these let a caller tell the upstream that this BFF — a trusted reverse proxy — had
  // vouched for whatever client IP, host and scheme they chose. None is a forbidden header, so any
  // script in the SPA's origin can set them.
  'x-forwarded-for', 'x-forwarded-host', 'x-forwarded-proto', 'x-forwarded-port',
  'x-forwarded-prefix', 'x-real-ip', 'forwarded',
]);

async function handleLogin(ctx: HttpCtx, d: BffDeps): Promise<void> {
  // Which tenant is this login for? Single-tenant: null; multi-tenant: the configured query param (?slug=acme).
  const tenantKey = d.o.isMultiTenant ? ctx.query.get(d.o.tenantQueryParam) : null;
  const tenant = await d.tenants.resolve(tenantKey);
  if (!tenant) { ctx.text('unknown_tenant', 400); return; }

  const state = randomToken();
  const nonce = randomToken();
  const verifier = randomToken();
  const challenge = await codeChallenge(verifier);
  const returnUrl = sanitizeReturnUrl(ctx.query.get('returnUrl'), d.o);
  const redirectUri = ctx.origin + d.o.callbackPath;

  const correlation = JSON.stringify({ state, verifier, nonce, returnUrl, tenantKey: tenant.tenantKey });
  ctx.setCookie(d.o.correlationCookieName, await d.protector.protect(correlation, CORRELATION_PURPOSE), transientCookieOpts(ctx, d.o));

  const url = new URL(await d.oidcFor(tenant.authority).authorizationEndpoint());
  const q = new URLSearchParams({
    response_type: 'code', client_id: tenant.clientId, redirect_uri: redirectUri, scope: tenant.scope.join(' '),
    state, nonce, code_challenge: challenge, code_challenge_method: 'S256',
  });
  url.search = q.toString();
  ctx.redirect(url.toString());
}

async function handleCallback(ctx: HttpCtx, d: BffDeps): Promise<void> {
  const protectedCorr = ctx.getCookie(d.o.correlationCookieName);
  if (!protectedCorr) { ctx.text('invalid_correlation', 400); return; }
  const corrJson = await d.protector.unprotect(protectedCorr, CORRELATION_PURPOSE);
  ctx.deleteCookie(d.o.correlationCookieName, transientCookieOpts(ctx, d.o));
  if (!corrJson) { ctx.text('invalid_correlation', 400); return; }

  const corr = JSON.parse(corrJson) as { state: string; verifier: string; nonce: string; returnUrl: string; tenantKey?: string };
  const state = ctx.query.get('state');
  if (!state || !timingSafeEqual(state, corr.state)) { ctx.text('state_mismatch', 400); return; }
  const error = ctx.query.get('error');
  if (error) { ctx.text(error, 400); return; }
  const code = ctx.query.get('code');
  if (!code) { ctx.text('missing_code', 400); return; }

  // The correlation cookie pins which tenant this login was for — re-resolve so the exchange + id_token
  // validation use the same confidential client + issuer.
  const tenant = await d.tenants.resolve(corr.tenantKey);
  if (!tenant) { ctx.text('unknown_tenant', 400); return; }

  const redirectUri = ctx.origin + d.o.callbackPath;
  let tokens;
  try {
    tokens = await d.oidcFor(tenant.authority).exchangeCode(tenant.clientId, tenant.clientSecret, code, redirectUri, corr.verifier);
  } catch (e) {
    d.log('code exchange failed', e);
    ctx.text('code_exchange_failed', 400);
    return;
  }
  if (!tokens.idToken) { ctx.text('missing_id_token', 400); return; }

  let payload: JWTPayload;
  try {
    payload = await d.oidcFor(tenant.authority).verifyIdToken(tokens.idToken, tenant.clientId);
  } catch (e) {
    d.log('id_token validation failed', e);
    ctx.text('invalid_id_token', 400);
    return;
  }
  if (typeof payload.nonce !== 'string' || !timingSafeEqual(payload.nonce, corr.nonce)) { ctx.text('nonce_mismatch', 400); return; }

  const now = Date.now();
  const session: BffSession = {
    sessionId: randomToken(),
    tenantKey: tenant.tenantKey,
    sid: typeof payload.sid === 'string' ? payload.sid : undefined,
    subject: typeof payload.sub === 'string' ? payload.sub : '',
    idToken: tokens.idToken,
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    accessTokenExpiresAt: now + tokens.expiresIn * 1000,
    expiresAt: now + d.o.sessionLifetimeSeconds * 1000,
    claims: extractClaims(payload),
  };
  await d.store.set(session);
  ctx.setCookie(d.o.cookieName, session.sessionId, sessionCookieOpts(ctx, d.o));
  ctx.redirect(corr.returnUrl);
}

async function handleUser(ctx: HttpCtx, d: BffDeps): Promise<void> {
  if (!hasAntiForgery(ctx, d.o)) { ctx.text('', 401); return; }
  const sessionId = ctx.getCookie(d.o.cookieName);
  if (!sessionId) { ctx.json({ isAuthenticated: false }); return; }

  const session = await d.store.get(sessionId);
  if (!session) { ctx.deleteCookie(d.o.cookieName, sessionCookieOpts(ctx, d.o)); ctx.json({ isAuthenticated: false }); return; }

  const fresh = await d.refresher.ensureFresh(session);
  if (!fresh) { ctx.deleteCookie(d.o.cookieName, sessionCookieOpts(ctx, d.o)); ctx.json({ isAuthenticated: false }); return; }

  ctx.json({ isAuthenticated: true, sessionExpiresAt: new Date(fresh.expiresAt).toISOString(), claims: fresh.claims });
}

async function handleLogout(ctx: HttpCtx, d: BffDeps): Promise<void> {
  if (ctx.method === 'POST' && !hasAntiForgery(ctx, d.o)) { ctx.text('', 401); return; }

  let idTokenHint: string | undefined;
  let tenant: BffTenantConfig | null = null;
  const sessionId = ctx.getCookie(d.o.cookieName);
  if (sessionId) {
    const session = await d.store.get(sessionId);
    if (session) {
      idTokenHint = session.idToken;
      // Re-resolve the session's tenant so revoke + end_session hit the right auth host + client.
      tenant = await d.tenants.resolve(session.tenantKey);
      if (tenant && session.refreshToken) await d.oidcFor(tenant.authority).revoke(tenant.clientId, tenant.clientSecret, session.refreshToken);
      await d.store.remove(sessionId);
    }
    ctx.deleteCookie(d.o.cookieName, sessionCookieOpts(ctx, d.o));
  }

  // The RP-initiated end_session redirect needs a tenant. Without a session (or an unresolvable tenant) there's
  // nothing to sign out at the IdP; just return to the post-logout URL.
  if (tenant) {
    const endSession = await d.oidcFor(tenant.authority).endSessionEndpoint();
    if (endSession) {
      const url = new URL(endSession);
      url.searchParams.set('client_id', tenant.clientId);
      if (idTokenHint) url.searchParams.set('id_token_hint', idTokenHint);
      if (d.o.postLogoutRedirectUri) url.searchParams.set('post_logout_redirect_uri', d.o.postLogoutRedirectUri);
      ctx.redirect(url.toString());
      return;
    }
  }
  if (d.o.postLogoutRedirectUri) { ctx.redirect(d.o.postLogoutRedirectUri); return; }
  ctx.text('', 200);
}

async function handleBackchannel(ctx: HttpCtx, d: BffDeps): Promise<void> {
  ctx.setHeader('Cache-Control', 'no-store');
  const form = await ctx.readForm();
  const logoutToken = form.get('logout_token');
  if (!logoutToken) { ctx.text('missing_logout_token', 400); return; }

  // No session cookie on a back-channel call — the token's issuer is all we have to pick the tenant. Read the
  // (unvalidated) iss only to *select* it; the signature is verified below against that tenant's JWKS + client
  // id, so a forged iss can't get a token accepted.
  let issuer: string | undefined;
  try { issuer = decodeJwt(logoutToken).iss; } catch { /* not a well-formed JWT */ }
  if (!issuer) { ctx.text('invalid_logout_token', 400); return; }

  const tenant = await d.tenants.resolveByIssuer(issuer);
  if (!tenant) { ctx.text('unknown_issuer', 400); return; }

  let payload: JWTPayload;
  try {
    payload = await d.oidcFor(tenant.authority).verifyLogoutToken(logoutToken, tenant.clientId);
  } catch (e) {
    d.log('back-channel logout token validation failed', e);
    ctx.text('invalid_logout_token', 400);
    return;
  }

  if ('nonce' in payload) { ctx.text('nonce_not_allowed', 400); return; }
  const events = payload.events;
  if (!events || typeof events !== 'object' || !(BACKCHANNEL_EVENT in events)) { ctx.text('missing_logout_event', 400); return; }

  const sid = typeof payload.sid === 'string' ? payload.sid : undefined;
  const sub = typeof payload.sub === 'string' ? payload.sub : undefined;
  if (!sid && !sub) { ctx.text('missing_sub_or_sid', 400); return; }

  const removed = sid ? await d.store.removeBySid(sid) : await d.store.removeBySubject(sub!);
  d.log(`back-channel logout removed ${removed} session(s) by ${sid ? 'sid' : 'sub'}`);
  ctx.text('', 200);
}

// ---- helpers ----

function hasAntiForgery(ctx: HttpCtx, o: ResolvedBffOptions): boolean {
  return ctx.getHeader(o.antiForgeryHeaderLower) !== undefined;
}

/**
 * True when the value is a same-site relative path no browser can read as an authority.
 *
 * This check had drifted from the .NET implementations: it never rejected a backslash (WHATWG normalizes
 * '\' to '/', so "/\evil.example" navigates off-site), and none of the copies rejected control
 * characters. The URL parser strips every ASCII tab, LF and CR BEFORE parsing, so "/\t/evil.example"
 * satisfies a "starts with / and not //" test and is then parsed as "//evil.example".
 */
function isSafeLocalPath(url: string): boolean {
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001F\u007F]/.test(url)) return false;
  if (!url.startsWith('/') || url.startsWith('//') || url.includes('\\')) return false;
  return true;
}

function sanitizeReturnUrl(returnUrl: string | null, o: ResolvedBffOptions): string {
  if (!returnUrl) return '/';
  if (isSafeLocalPath(returnUrl)) return returnUrl;
  try {
    const origin = new URL(returnUrl).origin;
    if (o.returnUrlAllowlist.some((a) => a.toLowerCase() === origin.toLowerCase())) return returnUrl;
  } catch { /* not a valid absolute url */ }
  return '/';
}

function extractClaims(payload: JWTPayload): Record<string, string> {
  const claims: Record<string, string> = {};
  for (const [k, v] of Object.entries(payload)) {
    if (PROTOCOL_CLAIMS.has(k)) continue;
    if (typeof v === 'string') claims[k] = v;
    else if (typeof v === 'number' || typeof v === 'boolean') claims[k] = String(v);
  }
  return claims;
}

function isSecure(ctx: HttpCtx, cookieName: string): boolean {
  return ctx.origin.startsWith('https:') || cookieName.startsWith('__Host-') || cookieName.startsWith('__Secure-');
}

function sessionCookieOpts(ctx: HttpCtx, o: ResolvedBffOptions): CookieOptions {
  return { httpOnly: true, secure: isSecure(ctx, o.cookieName), sameSite: 'lax', path: '/' };
}

function transientCookieOpts(ctx: HttpCtx, o: ResolvedBffOptions): CookieOptions {
  return { httpOnly: true, secure: isSecure(ctx, o.correlationCookieName), sameSite: 'lax', path: '/', maxAgeSeconds: 900 };
}

function timingSafeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let r = 0;
  for (let i = 0; i < a.length; i++) r |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return r === 0;
}

/** Serialize a Set-Cookie header value. Session id + protected payloads are base64url, so no escaping needed. */
export function serializeCookie(name: string, value: string, opts: CookieOptions): string {
  let s = `${name}=${value}`;
  s += `; Path=${opts.path ?? '/'}`;
  if (opts.maxAgeSeconds !== undefined) s += `; Max-Age=${opts.maxAgeSeconds}`;
  if (opts.httpOnly) s += '; HttpOnly';
  if (opts.secure) s += '; Secure';
  if (opts.sameSite) s += `; SameSite=${opts.sameSite[0]!.toUpperCase()}${opts.sameSite.slice(1)}`;
  return s;
}

/** Parse a Cookie header into a name→value map. */
export function parseCookies(header: string | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  if (!header) return out;
  for (const part of header.split(';')) {
    const i = part.indexOf('=');
    if (i < 0) continue;
    out[part.slice(0, i).trim()] = part.slice(i + 1).trim();
  }
  return out;
}

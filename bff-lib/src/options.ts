import type { IBffSessionStore } from './session.js';
import type { ICookieProtector } from './cookies.js';

export type BffSessionMode = 'store' | 'stateless';

/** Configuration for the Authagonal BFF. */
export interface AuthagonalBffOptions {
  /** Tenant auth host, e.g. `https://acme.authagonal.io`. Metadata is discovered from
   * `{authority}/.well-known/openid-configuration`. */
  authority: string;
  /** Confidential client id registered in Authagonal for this BFF. */
  clientId: string;
  /** Client secret. The BFF is a confidential client. */
  clientSecret: string;
  /** Requested scopes. Include `offline_access` to enable server-side refresh. */
  scope?: string[];
  /** Base path the endpoints mount under. Default `/bff`. */
  basePath?: string;
  /** Absolute path of the OIDC redirect URI (must match the registered redirect URI). Default `/bff/callback`. */
  callbackPath?: string;
  /** Session cookie name. The `__Host-` prefix forces Secure + Path=/ (needs HTTPS). Default `__Host-agbff`. */
  cookieName?: string;
  /** Only `store` is implemented. */
  sessionMode?: BffSessionMode;
  /** Refresh the access token when it is within this many seconds of expiry. Default 60. */
  refreshThresholdSeconds?: number;
  /** Absolute origins a non-relative `returnUrl` may target. Relative paths are always allowed. */
  returnUrlAllowlist?: string[];
  /** Where Authagonal sends the browser after a completed logout. */
  postLogoutRedirectUri?: string;
  /** Header the SPA must send on every non-navigation call (CSRF defence). Default `x-authagonal-bff`. */
  antiForgeryHeader?: string;
  /** Maximum session lifetime regardless of token refreshes, in seconds. Default 8h. */
  sessionLifetimeSeconds?: number;
  /** Session storage. Defaults to a single-process in-memory store (fine for one instance; use a shared
   * store for more). */
  sessionStore?: IBffSessionStore;
  /** Secret used to derive cookie encryption keys. Required unless a custom `cookieProtector` is supplied. */
  cookieSecret?: string;
  /** Custom cookie protector (overrides `cookieSecret`). */
  cookieProtector?: ICookieProtector;
}

/** Options with defaults applied. */
export interface ResolvedBffOptions extends Required<Omit<AuthagonalBffOptions, 'sessionStore' | 'cookieProtector' | 'cookieSecret'>> {
  scopeString: string;
  correlationCookieName: string;
  antiForgeryHeaderLower: string;
}

export function resolveOptions(o: AuthagonalBffOptions): ResolvedBffOptions {
  if (!o.authority) throw new Error('AuthagonalBffOptions.authority is required.');
  if (!o.clientId) throw new Error('AuthagonalBffOptions.clientId is required.');
  if (!o.clientSecret) throw new Error('AuthagonalBffOptions.clientSecret is required (the BFF is a confidential client).');
  if (o.sessionMode === 'stateless') throw new Error("sessionMode 'stateless' is not implemented yet; use 'store'.");
  if (!o.cookieProtector && !o.cookieSecret) throw new Error('AuthagonalBffOptions.cookieSecret (or a cookieProtector) is required.');

  const basePath = '/' + (o.basePath ?? '/bff').replace(/^\/+|\/+$/g, '');
  const callbackPath = '/' + (o.callbackPath ?? '/bff/callback').replace(/^\/+|\/+$/g, '');
  const cookieName = o.cookieName ?? '__Host-agbff';
  const scope = o.scope ?? ['openid', 'profile', 'offline_access'];
  const antiForgeryHeader = o.antiForgeryHeader ?? 'x-authagonal-bff';

  return {
    authority: o.authority.replace(/\/+$/, ''),
    clientId: o.clientId,
    clientSecret: o.clientSecret,
    scope,
    basePath,
    callbackPath,
    cookieName,
    sessionMode: o.sessionMode ?? 'store',
    refreshThresholdSeconds: o.refreshThresholdSeconds ?? 60,
    returnUrlAllowlist: o.returnUrlAllowlist ?? [],
    postLogoutRedirectUri: o.postLogoutRedirectUri ?? '',
    antiForgeryHeader,
    sessionLifetimeSeconds: o.sessionLifetimeSeconds ?? 8 * 60 * 60,
    scopeString: scope.join(' '),
    correlationCookieName: cookieName + '.tmp',
    antiForgeryHeaderLower: antiForgeryHeader.toLowerCase(),
  };
}

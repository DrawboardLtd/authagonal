import type { IBffSessionStore } from './session.js';
import type { ICookieProtector } from './cookies.js';
import type { IBffTenantResolver } from './tenant.js';

export type BffSessionMode = 'store' | 'stateless';

/** Configuration for the Authagonal BFF. */
export interface AuthagonalBffOptions {
  /** Tenant auth host, e.g. `https://acme.authagonal.io`. Metadata is discovered from
   * `{authority}/.well-known/openid-configuration`. Required in single-tenant mode; supplied per tenant by
   * `tenantResolver` when `tenantQueryParam` is set. */
  authority?: string;
  /** Confidential client id registered in Authagonal for this BFF. Required in single-tenant mode. */
  clientId?: string;
  /** Client secret. The BFF is a confidential client. Required in single-tenant mode. */
  clientSecret?: string;
  /** Multi-tenant switch. When set, one BFF serves many tenants: `/bff/login` reads the tenant key from this
   * query parameter (e.g. `'slug'` ⇒ `/bff/login?slug=acme`), the `tenantResolver` resolves the per-tenant
   * config, and the key is persisted on the session. When absent the BFF is single-tenant. */
  tenantQueryParam?: string;
  /** Resolves per-tenant client config in multi-tenant mode. Defaults to a single-tenant resolver over
   * `authority`/`clientId`/`clientSecret`. */
  tenantResolver?: IBffTenantResolver;
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
  /** Upstream APIs the proxy (`{basePath}/api/**`) forwards to with the session's access token attached.
   * Empty/absent disables the proxy. */
  upstreams?: BffUpstream[];
}

/** An upstream API the BFF proxy forwards to. The path after `{basePath}/api` is matched against
 * {@link prefix} to select the upstream, then appended to {@link targetBaseUrl}. */
export interface BffUpstream {
  /** Path prefix (after `{basePath}/api`) this upstream handles, e.g. `/orders`. */
  prefix: string;
  /** Base URL requests are forwarded to, e.g. `https://api.internal.acme.com`. */
  targetBaseUrl: string;
}

/** Options with defaults applied. */
export interface ResolvedBffOptions extends Required<Omit<AuthagonalBffOptions, 'sessionStore' | 'cookieProtector' | 'cookieSecret' | 'tenantResolver'>> {
  scopeString: string;
  correlationCookieName: string;
  antiForgeryHeaderLower: string;
  /** True when the BFF serves many tenants (`tenantQueryParam` is set). */
  isMultiTenant: boolean;
}

export function resolveOptions(o: AuthagonalBffOptions): ResolvedBffOptions {
  const isMultiTenant = !!o.tenantQueryParam?.trim();
  // In multi-tenant mode the tenantResolver supplies authority/clientId/clientSecret per tenant, so the static
  // single-tenant fields are not required (and are ignored if set).
  if (!isMultiTenant) {
    if (!o.authority) throw new Error('AuthagonalBffOptions.authority is required (or set tenantQueryParam for multi-tenant mode).');
    if (!o.clientId) throw new Error('AuthagonalBffOptions.clientId is required.');
    if (!o.clientSecret) throw new Error('AuthagonalBffOptions.clientSecret is required (the BFF is a confidential client).');
  }
  if (o.sessionMode === 'stateless') throw new Error("sessionMode 'stateless' is not implemented yet; use 'store'.");
  if (!o.cookieProtector && !o.cookieSecret) throw new Error('AuthagonalBffOptions.cookieSecret (or a cookieProtector) is required.');

  const basePath = '/' + (o.basePath ?? '/bff').replace(/^\/+|\/+$/g, '');
  const callbackPath = '/' + (o.callbackPath ?? '/bff/callback').replace(/^\/+|\/+$/g, '');
  const cookieName = o.cookieName ?? '__Host-agbff';
  const scope = o.scope ?? ['openid', 'profile', 'offline_access'];
  const antiForgeryHeader = o.antiForgeryHeader ?? 'x-authagonal-bff';

  return {
    authority: (o.authority ?? '').replace(/\/+$/, ''),
    clientId: o.clientId ?? '',
    clientSecret: o.clientSecret ?? '',
    tenantQueryParam: o.tenantQueryParam ?? '',
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
    upstreams: o.upstreams ?? [],
    scopeString: scope.join(' '),
    correlationCookieName: cookieName + '.tmp',
    antiForgeryHeaderLower: antiForgeryHeader.toLowerCase(),
    isMultiTenant,
  };
}

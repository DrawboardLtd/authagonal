import type { ResolvedBffOptions } from './options.js';

/** The resolved OIDC client configuration the BFF runs a single request against. In single-tenant mode this
 * is just the configured options (see {@link StaticBffTenantResolver}); in multi-tenant mode a custom
 * {@link IBffTenantResolver} returns a different one per tenant — e.g. a per-tenant authority derived from a
 * slug, with a shared confidential client. Mirrors the .NET `BffTenantConfig`. */
export interface BffTenantConfig {
  /** Opaque key identifying the tenant within this BFF (e.g. a slug). Persisted on the session and used to
   * re-resolve this config on later requests. Undefined in single-tenant mode. */
  tenantKey?: string;
  /** Tenant auth host, e.g. `https://acme-admin.authagonal.io`. */
  authority: string;
  /** Confidential client id registered in this tenant for the BFF. */
  clientId: string;
  /** Client secret. */
  clientSecret: string;
  /** Requested scopes. Include `offline_access` to enable server-side refresh. */
  scope: string[];
}

/** Resolves the per-tenant OIDC client configuration a BFF request runs against. The default
 * ({@link StaticBffTenantResolver}) always returns the single configured tenant, so single-tenant hosts are
 * unaffected. Supply a custom implementation via `AuthagonalBffOptions.tenantResolver` (and set
 * `tenantQueryParam`) to serve many tenants from one BFF. Mirrors the .NET `IBffTenantResolver`. */
export interface IBffTenantResolver {
  /** Resolve by tenant key — the login query-param value (null in single-tenant mode), then the key persisted
   * on the session. Returns null if the key is unknown/invalid (login is then rejected). */
  resolve(tenantKey: string | null | undefined): Promise<BffTenantConfig | null>;
  /** Resolve by OIDC issuer, for back-channel logout (no session cookie — only a signed token whose `iss`
   * identifies the tenant). Returns null if the issuer isn't one this BFF serves. */
  resolveByIssuer(issuer: string): Promise<BffTenantConfig | null>;
}

/** The default resolver: single-tenant. Always returns the one tenant configured in the options, ignoring the
 * key. A back-channel token for any other issuer will fail signature/audience validation against this config's
 * JWKS + client id, so returning it unconditionally is safe. */
export class StaticBffTenantResolver implements IBffTenantResolver {
  private readonly config: BffTenantConfig;

  constructor(o: ResolvedBffOptions) {
    this.config = { authority: o.authority, clientId: o.clientId, clientSecret: o.clientSecret, scope: o.scope };
  }

  async resolve(): Promise<BffTenantConfig | null> {
    return this.config;
  }

  async resolveByIssuer(): Promise<BffTenantConfig | null> {
    return this.config;
  }
}

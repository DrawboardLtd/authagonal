import type { BffSession, IBffSessionStore } from './session.js';
import type { OidcClient } from './oidc.js';
import { BffTokenError } from './oidc.js';
import type { ResolvedBffOptions } from './options.js';
import type { IBffTenantResolver } from './tenant.js';

/** Keeps a session's access token fresh, refreshing single-flight per session so concurrent requests never
 * each spend the (rotating) refresh token. Re-resolves the session's tenant so each refresh hits the right
 * auth host + confidential client. */
export class RefreshCoordinator {
  private readonly inflight = new Map<string, Promise<BffSession | null>>();

  constructor(
    private readonly tenants: IBffTenantResolver,
    private readonly oidcFor: (authority: string) => OidcClient,
    private readonly store: IBffSessionStore,
    private readonly o: ResolvedBffOptions,
  ) {}

  /** Returns the session with a valid access token, refreshing if near expiry; null if it can no longer be
   * kept valid (treat as logged out). */
  async ensureFresh(session: BffSession): Promise<BffSession | null> {
    // The ABSOLUTE session cap, enforced here rather than trusted to the store.
    //
    // `sessionLifetimeSeconds` (8 hours by default) is written onto the session as `expiresAt` and was
    // checked only by MemorySessionStore.get. The README tells a host running more than one instance to
    // implement IBffSessionStore "e.g. over Redis", and the obvious implementation —
    // `const j = await redis.get(k); return j ? JSON.parse(j) : null` — satisfies every documented
    // requirement while enforcing no expiry at all. In that deployment the 8-hour cap did not exist: a
    // session cookie captured, or left behind on a shared machine, authenticated /bff/user and the
    // token-injecting proxy indefinitely, because the coordinator kept the access token fresh forever.
    //
    // A store may still evict expired rows — it should, for retention — but the policy cannot depend on it.
    if (session.expiresAt <= Date.now()) {
      await this.store.remove(session.sessionId);
      return null;
    }

    if (!this.needsRefresh(session)) return session;
    if (!session.refreshToken) return session.accessTokenExpiresAt > Date.now() ? session : null;

    let p = this.inflight.get(session.sessionId);
    if (!p) {
      p = this.doRefresh(session.sessionId);
      this.inflight.set(session.sessionId, p);
      void p.finally(() => this.inflight.delete(session.sessionId));
    }
    return p;
  }

  /** How long a replica may hold a session's refresh lock — a token round trip, not much more. */
  private static readonly LOCK_TTL_MS = 10_000;

  private async doRefresh(sessionId: string): Promise<BffSession | null> {
    // Cross-replica gate, when the store offers one. The in-process Map above stops one instance from
    // redeeming twice; only this stops two instances from doing it. See IBffSessionStore.acquireRefreshLock.
    const locking = typeof this.store.acquireRefreshLock === 'function';
    if (locking) {
      const acquired = await this.store.acquireRefreshLock!(sessionId, RefreshCoordinator.LOCK_TTL_MS);
      if (!acquired) {
        // Another replica is redeeming. Wait for its result and use the session IT stored, rather than
        // redeeming the same token — which the IdP would read as a replay.
        return await this.awaitHoldersResult(sessionId);
      }
    }

    try {
      return await this.redeemAsync(sessionId);
    } finally {
      if (locking && typeof this.store.releaseRefreshLock === 'function')
        await this.store.releaseRefreshLock(sessionId).catch(() => { /* the lock expires on its own */ });
    }
  }

  /**
   * Polls for the lock holder's freshly stored session.
   *
   * Bounded: if the holder crashes mid-redemption its lock expires and the next request retries, which is
   * better than this one redeeming concurrently and losing the whole grant family.
   */
  private async awaitHoldersResult(sessionId: string): Promise<BffSession | null> {
    const deadline = Date.now() + RefreshCoordinator.LOCK_TTL_MS;
    while (Date.now() < deadline) {
      await new Promise((r) => setTimeout(r, 100));
      const stored = await this.store.get(sessionId);
      if (!stored) return null;
      if (!this.needsRefresh(stored)) return stored;
    }

    // Holder never finished. Fail this request rather than racing it; the session is untouched, so the
    // next request tries again once the lock has expired.
    return null;
  }

  private async redeemAsync(sessionId: string): Promise<BffSession | null> {
    const current = await this.store.get(sessionId);
    if (!current) return null;
    if (!this.needsRefresh(current)) return current;
    if (!current.refreshToken) return current.accessTokenExpiresAt > Date.now() ? current : null;

    const tenant = await this.tenants.resolve(current.tenantKey);
    if (!tenant) {
      // Tenant no longer resolvable (deprovisioned / config changed); can't refresh without its client creds.
      await this.store.remove(sessionId);
      return null;
    }

    try {
      const r = await this.oidcFor(tenant.authority).refresh(tenant.clientId, tenant.clientSecret, current.refreshToken);
      current.accessToken = r.accessToken;
      if (r.refreshToken) current.refreshToken = r.refreshToken;
      if (r.idToken) current.idToken = r.idToken;
      current.accessTokenExpiresAt = Date.now() + r.expiresIn * 1000;
      await this.store.set(current);
      return current;
    } catch (e) {
      if (e instanceof BffTokenError) {
        await this.store.remove(sessionId);
        return null;
      }
      throw e;
    }
  }

  private needsRefresh(s: BffSession): boolean {
    return s.accessTokenExpiresAt - Date.now() <= this.o.refreshThresholdSeconds * 1000;
  }
}

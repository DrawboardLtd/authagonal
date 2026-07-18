import type { BffSession, IBffSessionStore } from './session.js';
import type { OidcClient } from './oidc.js';
import { BffTokenError } from './oidc.js';
import type { ResolvedBffOptions } from './options.js';

/** Keeps a session's access token fresh, refreshing single-flight per session so concurrent requests never
 * each spend the (rotating) refresh token. */
export class RefreshCoordinator {
  private readonly inflight = new Map<string, Promise<BffSession | null>>();

  constructor(
    private readonly oidc: OidcClient,
    private readonly store: IBffSessionStore,
    private readonly o: ResolvedBffOptions,
  ) {}

  /** Returns the session with a valid access token, refreshing if near expiry; null if it can no longer be
   * kept valid (treat as logged out). */
  async ensureFresh(session: BffSession): Promise<BffSession | null> {
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

  private async doRefresh(sessionId: string): Promise<BffSession | null> {
    const current = await this.store.get(sessionId);
    if (!current) return null;
    if (!this.needsRefresh(current)) return current;
    if (!current.refreshToken) return current.accessTokenExpiresAt > Date.now() ? current : null;

    try {
      const r = await this.oidc.refresh(current.refreshToken);
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

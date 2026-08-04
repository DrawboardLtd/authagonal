/** A server-side BFF session. The browser only ever sees {@link BffSession.sessionId} (in an httpOnly
 * cookie), never the tokens. Timestamps are epoch milliseconds. */
export interface BffSession {
  /** Opaque, unguessable session id. Equals the value stored in the session cookie. */
  sessionId: string;
  /** The tenant this session belongs to (see IBffTenantResolver). Undefined in single-tenant mode. */
  tenantKey?: string;
  /** OIDC session id (`sid`) from the id_token, used to match session-scoped back-channel logout. */
  sid?: string;
  /** Authenticated subject (`sub`). */
  subject: string;
  /** Raw id_token, retained for `id_token_hint` on logout. */
  idToken: string;
  /** Current access token (for downstream APIs; never sent to the browser). */
  accessToken: string;
  /** Current refresh token, if `offline_access` was granted. */
  refreshToken?: string;
  /** When the access token expires (epoch ms). */
  accessTokenExpiresAt: number;
  /** Absolute session expiry (epoch ms), independent of token refreshes. */
  expiresAt: number;
  /** Non-sensitive id_token claims surfaced to the SPA via `/bff/user`. */
  claims: Record<string, string>;
}

/** Server-side storage for BFF sessions. Replace to move sessions onto other infrastructure (one of the
 * seams that let the core run at the edge later). */
export interface IBffSessionStore {
  get(sessionId: string): Promise<BffSession | null>;
  /** Create or replace a session. Implementations must index it by `subject` and (when present) `sid`. */
  set(session: BffSession): Promise<void>;
  remove(sessionId: string): Promise<void>;
  /** Delete every session matching an OIDC `sid` (session-scoped logout). Returns the count removed.
   *
   * `tenantKey` scopes the lookup to one tenant, and implementations MUST honour it. `sub` and `sid` are
   * unique only within an issuer, so an unscoped removal lets a logout accepted from one tenant's IdP
   * terminate another tenant's sessions for a colliding value — and `/bff/backchannel-logout` accepts a
   * valid token from ANY configured tenant, which makes that a cross-tenant denial of service. */
  removeBySid(sid: string, tenantKey?: string): Promise<number>;
  /** Delete every session for a subject (subject-scoped logout — the form Authagonal emits). Returns count.
   *
   * `tenantKey` scopes the lookup exactly as in {@link removeBySid}. */
  removeBySubject(subject: string, tenantKey?: string): Promise<number>;

  /**
   * OPTIONAL cross-replica lock for one session's refresh. Return false if another holder has it.
   *
   * Without this, `RefreshCoordinator`'s single-flight is per-PROCESS — a `Map` on one instance — while the
   * session and its rotating refresh token live in a store shared by every replica. Two replicas can
   * therefore read the same session, both see it needs refreshing, and both redeem the same refresh token.
   * That is indistinguishable from a stolen-token replay, and an IdP's response to replay is to revoke the
   * whole grant family — so the multi-instance deployment the README recommends can sign a user out
   * everywhere as a matter of routine, under nothing more than concurrent load.
   *
   * Any backend works: all this needs is "at most one holder for a short time". Implement it with `SET NX PX`
   * on Redis, or a conditional write anywhere else. The .NET twin does the same thing through
   * `ILeaseProvider`.
   *
   * With it unimplemented the behaviour is unchanged, and a multi-instance BFF then depends on the IdP's
   * refresh-reuse grace window (`Auth:RefreshTokenReuseGraceSeconds`, 30 in the protocol layer but **0 —
   * strict** in the Authagonal.Server host's own default) to absorb the double redemption.
   */
  acquireRefreshLock?(sessionId: string, ttlMs: number): Promise<boolean>;

  /** Releases {@link IBffSessionStore.acquireRefreshLock}. Implement both or neither. */
  releaseRefreshLock?(sessionId: string): Promise<void>;
}

/** Single-process in-memory session store. Fine for one instance; use a shared store (e.g. Redis) for more.
 * Expired sessions are evicted lazily on read. */
export class MemorySessionStore implements IBffSessionStore {
  private readonly sessions = new Map<string, BffSession>();
  private readonly bySid = new Map<string, Set<string>>();
  private readonly bySub = new Map<string, Set<string>>();

  async get(sessionId: string): Promise<BffSession | null> {
    const s = this.sessions.get(sessionId);
    if (!s) return null;
    if (s.expiresAt <= Date.now()) {
      await this.remove(sessionId);
      return null;
    }
    return s;
  }

  async set(session: BffSession): Promise<void> {
    this.sessions.set(session.sessionId, session);
    index(this.bySub, indexKey(session.tenantKey, session.subject), session.sessionId);
    if (session.sid) index(this.bySid, indexKey(session.tenantKey, session.sid), session.sessionId);
  }

  async remove(sessionId: string): Promise<void> {
    const s = this.sessions.get(sessionId);
    this.sessions.delete(sessionId);
    if (!s) return;
    deindex(this.bySub, indexKey(s.tenantKey, s.subject), sessionId);
    if (s.sid) deindex(this.bySid, indexKey(s.tenantKey, s.sid), sessionId);
  }

  removeBySid(sid: string, tenantKey?: string): Promise<number> {
    return this.purge(this.bySid, indexKey(tenantKey, sid));
  }

  removeBySubject(subject: string, tenantKey?: string): Promise<number> {
    return this.purge(this.bySub, indexKey(tenantKey, subject));
  }

  private async purge(idx: Map<string, Set<string>>, key: string): Promise<number> {
    const ids = idx.get(key);
    if (!ids) return 0;
    const n = ids.size;
    for (const id of [...ids]) await this.remove(id);
    return n;
  }
}

/** Namespaces a secondary-index entry by tenant, mirroring the .NET `agbff:sub:{tenantKey}:{sub}` keys.
 *
 * These were keyed on the bare sid/sub, which made the index mean "this subject, at any issuer" — so a
 * back-channel logout accepted from one tenant killed a colliding subject's sessions at every other one.
 * The tenant key is percent-encoded so it can never contain the separator: without that, tenant "a:b"
 * with subject "c" and tenant "a" with subject "b:c" would collide right back into the same bug. */
function indexKey(tenantKey: string | undefined, value: string): string {
  return `${encodeURIComponent(tenantKey ?? '-')}:${value}`;
}

function index(idx: Map<string, Set<string>>, key: string, id: string): void {
  let set = idx.get(key);
  if (!set) idx.set(key, (set = new Set()));
  set.add(id);
}

function deindex(idx: Map<string, Set<string>>, key: string, id: string): void {
  const set = idx.get(key);
  if (!set) return;
  set.delete(id);
  if (set.size === 0) idx.delete(key);
}

/** A server-side BFF session. The browser only ever sees {@link BffSession.sessionId} (in an httpOnly
 * cookie), never the tokens. Timestamps are epoch milliseconds. */
export interface BffSession {
  /** Opaque, unguessable session id. Equals the value stored in the session cookie. */
  sessionId: string;
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
  /** Delete every session matching an OIDC `sid` (session-scoped logout). Returns the count removed. */
  removeBySid(sid: string): Promise<number>;
  /** Delete every session for a subject (subject-scoped logout — the form Authagonal emits). Returns count. */
  removeBySubject(subject: string): Promise<number>;
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
    index(this.bySub, session.subject, session.sessionId);
    if (session.sid) index(this.bySid, session.sid, session.sessionId);
  }

  async remove(sessionId: string): Promise<void> {
    const s = this.sessions.get(sessionId);
    this.sessions.delete(sessionId);
    if (!s) return;
    deindex(this.bySub, s.subject, sessionId);
    if (s.sid) deindex(this.bySid, s.sid, sessionId);
  }

  removeBySid(sid: string): Promise<number> {
    return this.purge(this.bySid, sid);
  }

  removeBySubject(subject: string): Promise<number> {
    return this.purge(this.bySub, subject);
  }

  private async purge(idx: Map<string, Set<string>>, key: string): Promise<number> {
    const ids = idx.get(key);
    if (!ids) return 0;
    const n = ids.size;
    for (const id of [...ids]) await this.remove(id);
    return n;
  }
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

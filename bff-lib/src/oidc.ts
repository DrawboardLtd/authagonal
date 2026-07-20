import { createRemoteJWKSet, jwtVerify, type JWTPayload, type JWTVerifyGetKey } from 'jose';

/** URL-safe base64 of raw bytes (no padding). Portable (no Buffer), so it runs at the edge too. */
export function base64url(bytes: Uint8Array): string {
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** A random URL-safe token (default 32 bytes) for state / nonce / PKCE verifier / session ids. */
export function randomToken(bytes = 32): string {
  return base64url(crypto.getRandomValues(new Uint8Array(bytes)));
}

/** PKCE S256 code challenge for a verifier. */
export async function codeChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
  return base64url(new Uint8Array(digest));
}

interface OidcMetadata {
  issuer: string;
  authorization_endpoint: string;
  token_endpoint: string;
  jwks_uri: string;
  end_session_endpoint?: string;
  revocation_endpoint?: string;
}

export interface TokenResult {
  accessToken: string;
  refreshToken?: string;
  idToken?: string;
  expiresIn: number;
}

export class BffTokenError extends Error {}

/** Talks to one tenant's OIDC endpoints: discovery + JWKS (cached, rotation-aware via jose), token exchange,
 * refresh, revocation, and id_token / logout_token verification. Bound to an authority only — the confidential
 * client credentials (and the expected audience) are passed per call, so a single-authority instance serves
 * whichever client a multi-tenant BFF resolves for it. Mirrors the .NET `BffOidcConfig` + `AuthagonalTokenClient`
 * split. Cache one per authority via {@link oidcClientCache}. */
export class OidcClient {
  private metadata?: OidcMetadata;
  private jwks?: JWTVerifyGetKey;
  private fetchedAt = 0;
  private static readonly TTL_MS = 60 * 60 * 1000;

  constructor(private readonly authority: string) {}

  private async meta(): Promise<OidcMetadata> {
    if (this.metadata && Date.now() - this.fetchedAt < OidcClient.TTL_MS) return this.metadata;
    const res = await fetch(`${this.authority}/.well-known/openid-configuration`);
    if (!res.ok) throw new BffTokenError(`OIDC discovery failed: ${res.status}`);
    const m = (await res.json()) as OidcMetadata;
    this.metadata = m;
    this.fetchedAt = Date.now();
    this.jwks = createRemoteJWKSet(new URL(m.jwks_uri));
    return m;
  }

  async authorizationEndpoint(): Promise<string> { return (await this.meta()).authorization_endpoint; }
  async endSessionEndpoint(): Promise<string | undefined> { return (await this.meta()).end_session_endpoint; }

  async exchangeCode(clientId: string, clientSecret: string, code: string, redirectUri: string, codeVerifier: string): Promise<TokenResult> {
    const m = await this.meta();
    return this.postToken(m.token_endpoint, {
      grant_type: 'authorization_code', code, redirect_uri: redirectUri, code_verifier: codeVerifier,
      client_id: clientId, client_secret: clientSecret,
    });
  }

  async refresh(clientId: string, clientSecret: string, refreshToken: string): Promise<TokenResult> {
    const m = await this.meta();
    return this.postToken(m.token_endpoint, {
      grant_type: 'refresh_token', refresh_token: refreshToken,
      client_id: clientId, client_secret: clientSecret,
    });
  }

  async revoke(clientId: string, clientSecret: string, refreshToken: string): Promise<void> {
    const m = await this.meta();
    if (!m.revocation_endpoint) return;
    await fetch(m.revocation_endpoint, {
      method: 'POST',
      headers: { 'content-type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ token: refreshToken, token_type_hint: 'refresh_token', client_id: clientId, client_secret: clientSecret }),
    }).catch(() => { /* best-effort */ });
  }

  private async postToken(endpoint: string, form: Record<string, string>): Promise<TokenResult> {
    const res = await fetch(endpoint, {
      method: 'POST',
      headers: { 'content-type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams(form),
    });
    const text = await res.text();
    if (!res.ok) throw new BffTokenError(`Token endpoint returned ${res.status}: ${text}`);
    const json = JSON.parse(text) as { access_token?: string; refresh_token?: string; id_token?: string; expires_in?: number };
    if (!json.access_token) throw new BffTokenError('Token response did not contain an access_token.');
    return { accessToken: json.access_token, refreshToken: json.refresh_token, idToken: json.id_token, expiresIn: json.expires_in ?? 3600 };
  }

  async verifyIdToken(idToken: string, clientId: string): Promise<JWTPayload> {
    const m = await this.meta();
    const { payload } = await jwtVerify(idToken, this.jwks!, { issuer: m.issuer, audience: clientId });
    return payload;
  }

  /** Verify an OIDC back-channel logout token. Logout tokens carry no `exp`; jwtVerify only enforces `exp`
   * when present, so this validates signature + issuer + audience. Caller checks `events` / no-`nonce`. */
  async verifyLogoutToken(logoutToken: string, clientId: string): Promise<JWTPayload> {
    const m = await this.meta();
    const { payload } = await jwtVerify(logoutToken, this.jwks!, { issuer: m.issuer, audience: clientId });
    return payload;
  }
}

/** Returns a memoized {@link OidcClient} per authority, so a multi-tenant BFF discovers each tenant's auth host
 * once and reuses its cached metadata + JWKS. Mirrors the .NET `BffOidcConfig` per-authority dictionary. */
export function oidcClientCache(): (authority: string) => OidcClient {
  const byAuthority = new Map<string, OidcClient>();
  return (authority: string) => {
    const key = authority.replace(/\/+$/, '');
    let c = byAuthority.get(key);
    if (!c) byAuthority.set(key, (c = new OidcClient(key)));
    return c;
  };
}

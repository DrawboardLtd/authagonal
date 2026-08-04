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

/**
 * Signature algorithms accepted on inbound tokens, shared by the callback `id_token` and the
 * back-channel logout token so the two cannot drift — the .NET BFF pins the identical set in
 * `BffEndpoints.AsymmetricSigningAlgorithms`, and parity between the two implementations is the
 * standing rule for this package.
 *
 * Symmetric algorithms are excluded because they are what makes key confusion possible: an `HS256`
 * token verified against a public key treats that public value as a shared secret. jose's
 * `createLocalJWKSet`/`createRemoteJWKSet` refuse to resolve a key for `HS*` or `none` anyway, so
 * this pin adds no protection jose does not already give — it makes the accepted set a property of
 * this code rather than of a dependency's internals, which is what the finding asked for.
 */
const ASYMMETRIC_SIGNING_ALGORITHMS = [
  'RS256', 'RS384', 'RS512',
  'PS256', 'PS384', 'PS512',
  'ES256', 'ES384', 'ES512',
];

export interface OidcMetadata {
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

/**
 * The two trust-anchor checks on a discovery document, before anything in it is believed.
 *
 * The document is the trust anchor for the whole connection: `issuer` becomes the `issuer` option passed to
 * `jwtVerify`, and `jwks_uri` supplies the keys that verification uses. Both halves of that comparison came
 * from the same document, so anyone able to answer the metadata URL could mint an id_token for any `sub` and
 * be issued a BFF session for that user. `requireHttps` in the .NET twin — and nothing at all here — bounded
 * only the metadata address.
 *
 * 1. **Issuer binding (OIDC Discovery §4.3).** The declared `issuer` MUST equal the authority the document was
 *    fetched from. This is the check that closes the bypass, and it holds whatever the scheme is.
 * 2. **No endpoint weaker than the authority.** For an https authority, every endpoint the document names must
 *    be https. An http authority is left alone on purpose: reaching an identity server on a private address is
 *    a supported topology, and that deployment has already accepted plaintext — what it has not accepted is
 *    being downgraded by a value the document chose.
 *
 * Mirrors `BffOidcConfig.Validate` in the .NET package. Exported so the check is testable on its own.
 */
export function assertTrustedMetadata(authority: string, m: OidcMetadata): void {
  const declared = (m.issuer ?? '').replace(/\/+$/, '');
  if (declared.toLowerCase() !== authority.toLowerCase()) {
    throw new BffTokenError(
      `OIDC discovery issuer mismatch: the document for authority '${authority}' declares issuer ` +
        `'${m.issuer}'. Per OIDC Discovery §4.3 they must match. Refusing rather than trusting an issuer ` +
        `the configured authority does not vouch for — the id_token is validated against this value, so ` +
        `accepting it would let whoever served the document authenticate as any user.`,
    );
  }

  if (!authority.toLowerCase().startsWith('https://')) return;

  const endpoints: [string, string | undefined][] = [
    ['jwks_uri', m.jwks_uri],
    ['token_endpoint', m.token_endpoint],
    ['authorization_endpoint', m.authorization_endpoint],
    ['end_session_endpoint', m.end_session_endpoint],
    ['revocation_endpoint', m.revocation_endpoint],
  ];

  for (const [name, endpoint] of endpoints) {
    if (!endpoint) continue;
    let parsed: URL;
    try {
      parsed = new URL(endpoint);
    } catch {
      throw new BffTokenError(`OIDC discovery document declared an unparseable ${name} ('${endpoint}').`);
    }
    if (parsed.protocol !== 'https:') {
      throw new BffTokenError(
        `OIDC discovery document for https authority '${authority}' declared a non-https ${name} ` +
          `('${endpoint}'). That would move the signing keys, the client secret, the authorization code or ` +
          `the id_token over cleartext on a connection the operator configured as https.`,
      );
    }
  }
}

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
    assertTrustedMetadata(this.authority.replace(/\/+$/, ''), m);
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
    const { payload } = await jwtVerify(idToken, this.jwks!, {
      issuer: m.issuer,
      audience: clientId,
      algorithms: ASYMMETRIC_SIGNING_ALGORITHMS,
    });
    return payload;
  }

  /** Verify an OIDC back-channel logout token. Logout tokens carry no `exp`; jwtVerify only enforces `exp`
   * when present, so this validates signature + issuer + audience. Caller checks `events` / no-`nonce`. */
  /**
   * Verifies a back-channel Logout Token.
   *
   * A Logout Token carries no `exp` (OIDC Back-Channel Logout 1.0 §2.4 forbids relying on one), so
   * signature + issuer + audience alone made a captured token valid FOREVER: anyone who obtained one
   * could log the user out at will, indefinitely. The .NET BFF has bounded `iat` since 0.20.0; this
   * implementation did not, so a TypeScript host had a permanent denial-of-service primitive against
   * every session it had ever ended.
   */
  async verifyLogoutToken(logoutToken: string, clientId: string): Promise<JWTPayload> {
    const m = await this.meta();
    const { payload } = await jwtVerify(logoutToken, this.jwks!, {
      issuer: m.issuer,
      audience: clientId,
      algorithms: ASYMMETRIC_SIGNING_ALGORITHMS,
    });

    const iat = payload.iat;
    if (typeof iat !== 'number') {
      throw new Error('logout token has no iat');
    }

    const ageSeconds = Math.abs(Date.now() / 1000 - iat);
    if (ageSeconds > LOGOUT_TOKEN_MAX_AGE_SECONDS) {
      throw new Error('logout token is outside its freshness window');
    }

    return payload;
  }
}

/**
 * How far a Logout Token's `iat` may be from now. Generous enough for ordinary clock skew between
 * the OP and this host, tight enough that a captured token stops working in minutes rather than
 * never.
 */
const LOGOUT_TOKEN_MAX_AGE_SECONDS = 300;

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

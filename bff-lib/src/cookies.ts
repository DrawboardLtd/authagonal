import { CompactEncrypt, compactDecrypt } from 'jose';

/** Protects (encrypts + authenticates) small cookie payloads. Replace to key cookies from your own KMS
 * (a hosted-seam extension point). */
export interface ICookieProtector {
  protect(plaintext: string, purpose: string): Promise<string>;
  /** Returns null (not throw) on any tamper/decrypt failure. */
  unprotect(protectedText: string, purpose: string): Promise<string | null>;
}

/** Default cookie protector: AES-256-GCM (JWE `dir`/`A256GCM`) with a per-purpose key derived from a
 * secret via SHA-256. Uses Web Crypto, so it also runs at the edge. */
export class JoseCookieProtector implements ICookieProtector {
  constructor(private readonly secret: string) {
    if (!secret) throw new Error('JoseCookieProtector requires a non-empty secret.');

    // A minimum length, because this secret is the sole input to the cookie key: there was no
    // strength requirement at all, so a two-character secret was accepted and every session and
    // correlation cookie in the deployment rested on it. 32 characters is the shortest value that
    // makes an offline search of the secret itself uninteresting.
    if (secret.length < 32) {
      throw new Error(
        'JoseCookieProtector requires a secret of at least 32 characters: it is the only input to ' +
          'the key that encrypts the session and correlation cookies.',
      );
    }
  }

  async protect(plaintext: string, purpose: string): Promise<string> {
    const key = await this.deriveKey(purpose);
    return await new CompactEncrypt(new TextEncoder().encode(plaintext))
      .setProtectedHeader({ alg: 'dir', enc: 'A256GCM' })
      .encrypt(key);
  }

  async unprotect(protectedText: string, purpose: string): Promise<string | null> {
    try {
      const key = await this.deriveKey(purpose);
      const { plaintext } = await compactDecrypt(protectedText, key);
      return new TextDecoder().decode(plaintext);
    } catch {
      return null;
    }
  }

  /**
   * Derives the per-purpose AES key with HKDF-SHA256 rather than a bare digest.
   *
   * This was `SHA-256(secret | purpose)` — one unsalted round over the raw secret. SHA-256 is fast
   * by design, so if the secret is anything short of high-entropy random (an operator-chosen
   * passphrase, a value copied from a wiki) it is directly guessable offline from one captured
   * cookie: recovering it yields the key for every purpose, which decrypts and FORGES the correlation
   * and session cookies. HKDF is the right primitive for turning key material into per-context keys,
   * and it carries the purpose as `info` instead of splicing it into the hash input.
   */
  private async deriveKey(purpose: string): Promise<Uint8Array> {
    const material = await crypto.subtle.importKey(
      'raw',
      new TextEncoder().encode(this.secret),
      'HKDF',
      false,
      ['deriveBits'],
    );

    const bits = await crypto.subtle.deriveBits(
      {
        name: 'HKDF',
        hash: 'SHA-256',
        // Fixed and non-secret, which is what HKDF's salt is for: it separates this deployment's
        // derivation from any other use of the same secret. Secrecy lives in `secret`.
        salt: new TextEncoder().encode('authagonal-bff-cookie-v1'),
        info: new TextEncoder().encode(purpose),
      },
      material,
      256,
    );

    return new Uint8Array(bits);
  }
}

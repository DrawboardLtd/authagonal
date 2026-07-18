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

  private async deriveKey(purpose: string): Promise<Uint8Array> {
    const material = new TextEncoder().encode(`${this.secret}|${purpose}`);
    const hash = await crypto.subtle.digest('SHA-256', material);
    return new Uint8Array(hash);
  }
}

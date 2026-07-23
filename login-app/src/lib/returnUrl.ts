import { getApps } from '../api';

/** A same-origin relative path — always a safe redirect target. */
export function isSameOriginPath(url: string): boolean {
  if (!url || !url.startsWith('/')) return false;
  try {
    const parsed = new URL(url, window.location.origin);
    return parsed.origin === window.location.origin;
  } catch {
    return false;
  }
}

/**
 * Resolve a post-auth redirect target from an untrusted returnUrl. Same-origin relative paths pass
 * as before. ABSOLUTE returnUrls are allowed only when their origin belongs to a REGISTERED
 * client's home URI (operator-entered, server-side data via /apps) — this is how product apps
 * (invite/register landings) return the user to their OWN pages after an auth-host round trip.
 * Anything else falls through to the caller's fallback. Never returns an attacker-controllable
 * cross-origin target: the allow-list is the tenant's registered apps.
 */
export async function resolveRedirect(
  returnUrl: string,
  fallback: () => Promise<string> | string,
): Promise<string> {
  if (returnUrl) {
    if (isSameOriginPath(returnUrl)) return returnUrl;
    try {
      const target = new URL(returnUrl);
      if (target.protocol === 'https:' || target.protocol === 'http:') {
        const apps = await getApps();
        const allowed = apps.some((a) => {
          try {
            return new URL(a.homeUri).origin === target.origin;
          } catch {
            return false;
          }
        });
        if (allowed) return returnUrl;
      }
    } catch {
      // not an absolute URL either — fall through
    }
  }
  return fallback();
}

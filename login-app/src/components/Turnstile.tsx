import { useEffect, useRef } from 'react';

// Cloudflare Turnstile ("I'm human"). Renders the managed widget and reports the
// token via onToken. Only mount this when a site key is configured (opt-in) — the
// server returns turnstileSiteKey on /api/auth/providers when Turnstile is enabled.

declare global {
  interface Window {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    turnstile?: any;
  }
}

const SCRIPT_SRC = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
let scriptPromise: Promise<void> | null = null;

function loadTurnstileScript(): Promise<void> {
  if (typeof window !== 'undefined' && window.turnstile) return Promise.resolve();
  if (scriptPromise) return scriptPromise;
  scriptPromise = new Promise<void>((resolve, reject) => {
    const s = document.createElement('script');
    s.src = SCRIPT_SRC;
    s.async = true;
    s.defer = true;
    s.onload = () => resolve();
    s.onerror = () => {
      scriptPromise = null;
      reject(new Error('Failed to load Cloudflare Turnstile'));
    };
    document.head.appendChild(s);
  });
  return scriptPromise;
}

export interface TurnstileProps {
  siteKey: string;
  /** Called with the token on success, or null when it expires / errors / is reset. */
  onToken: (token: string | null) => void;
  theme?: 'auto' | 'light' | 'dark';
}

export function Turnstile({ siteKey, onToken, theme = 'auto' }: TurnstileProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const widgetIdRef = useRef<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    loadTurnstileScript()
      .then(() => {
        if (cancelled || !containerRef.current || !window.turnstile) return;
        widgetIdRef.current = window.turnstile.render(containerRef.current, {
          sitekey: siteKey,
          theme,
          callback: (token: string) => onToken(token),
          'expired-callback': () => onToken(null),
          'error-callback': () => onToken(null),
        });
      })
      .catch(() => onToken(null));

    return () => {
      cancelled = true;
      if (widgetIdRef.current && window.turnstile) {
        try {
          window.turnstile.remove(widgetIdRef.current);
        } catch {
          /* widget already gone */
        }
        widgetIdRef.current = null;
      }
    };
    // siteKey is stable for the page lifetime; re-render only if it changes.
  }, [siteKey, theme, onToken]);

  return <div ref={containerRef} className="flex justify-center" />;
}

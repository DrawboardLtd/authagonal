// Browser-side helper (no server dependencies). Wraps fetch with the anti-forgery header the BFF
// requires on non-navigation calls, and gives ergonomic login/logout/user/apiFetch helpers.

export interface BffUser {
  isAuthenticated: boolean;
  claims?: Record<string, string>;
  sessionExpiresAt?: string;
}

export interface BffClientOptions {
  /** Must match the server's basePath. Default `/bff`. */
  basePath?: string;
  /** Must match the server's antiForgeryHeader. Default `X-Authagonal-Bff`. */
  antiForgeryHeader?: string;
}

export interface BffClient {
  /** fetch() that adds the anti-forgery header. Use for `/bff/user` and `/bff/api/*`. */
  fetch(input: string, init?: RequestInit): Promise<Response>;
  /** GET `/bff/user`. */
  getUser(): Promise<BffUser>;
  /** Navigate the browser to login (defaults returnUrl to the current path). */
  login(returnUrl?: string): void;
  /** Navigate the browser to logout. */
  logout(): void;
  /** Proxy a call through the BFF: `apiFetch('/orders/1')` → `GET {basePath}/api/orders/1` with the token. */
  apiFetch(path: string, init?: RequestInit): Promise<Response>;
}

export function createBffClient(options: BffClientOptions = {}): BffClient {
  const base = options.basePath ?? '/bff';
  const header = options.antiForgeryHeader ?? 'X-Authagonal-Bff';

  const bffFetch = (input: string, init: RequestInit = {}): Promise<Response> => {
    const headers = new Headers(init.headers);
    headers.set(header, '1');
    return fetch(input, { ...init, headers, credentials: 'same-origin' });
  };

  return {
    fetch: bffFetch,
    getUser: async () => (await bffFetch(`${base}/user`)).json() as Promise<BffUser>,
    login: (returnUrl = location.pathname + location.search) => {
      location.href = `${base}/login?returnUrl=${encodeURIComponent(returnUrl)}`;
    },
    logout: () => { location.href = `${base}/logout`; },
    apiFetch: (path, init) => bffFetch(`${base}/api${path.startsWith('/') ? path : `/${path}`}`, init),
  };
}

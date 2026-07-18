import { useCallback, useEffect, useState } from 'react';
import { createBffClient, type BffClient, type BffClientOptions, type BffUser } from './client.js';

export interface UseUser {
  /** null until the first load resolves. */
  user: BffUser | null;
  loading: boolean;
  /** Re-fetch `/bff/user`. */
  refresh: () => void;
  login: (returnUrl?: string) => void;
  logout: () => void;
  /** Anti-forgery fetch + `/bff/api/*` proxy helper (see {@link BffClient}). */
  fetch: BffClient['fetch'];
  apiFetch: BffClient['apiFetch'];
}

/** React hook over the BFF: loads `/bff/user` on mount and exposes login/logout + the CSRF fetch helpers. */
export function useUser(options?: BffClientOptions): UseUser {
  const [client] = useState<BffClient>(() => createBffClient(options));
  const [user, setUser] = useState<BffUser | null>(null);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(() => {
    setLoading(true);
    client.getUser()
      .then(setUser)
      .catch(() => setUser({ isAuthenticated: false }))
      .finally(() => setLoading(false));
  }, [client]);

  useEffect(() => { refresh(); }, [refresh]);

  return { user, loading, refresh, login: client.login, logout: client.logout, fetch: client.fetch, apiFetch: client.apiFetch };
}

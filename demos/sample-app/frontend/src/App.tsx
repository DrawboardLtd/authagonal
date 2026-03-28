import { useEffect, useState } from 'react';
import { login, logout, handleCallback, getStoredAuth, type AuthState } from './auth';

export default function App() {
  const [auth, setAuth] = useState<AuthState | null>(null);
  const [apiResult, setApiResult] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    async function init() {
      try {
        // Check if this is an OAuth callback
        if (window.location.search.includes('code=')) {
          const state = await handleCallback();
          if (state) {
            setAuth(state);
            setLoading(false);
            return;
          }
        }

        // Check for stored auth
        const stored = getStoredAuth();
        setAuth(stored);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Authentication failed');
      } finally {
        setLoading(false);
      }
    }

    init();
  }, []);

  const handleLogout = () => {
    logout();
    setAuth(null);
    setApiResult(null);
  };

  const callApi = async (endpoint: string) => {
    if (!auth) return;
    setApiResult(null);

    try {
      const res = await fetch(endpoint, {
        headers: { Authorization: `Bearer ${auth.accessToken}` },
      });
      const data = await res.json();
      setApiResult(JSON.stringify(data, null, 2));
    } catch (err) {
      setApiResult(`Error: ${err instanceof Error ? err.message : 'Request failed'}`);
    }
  };

  if (loading) {
    return <div className="container"><p>Loading...</p></div>;
  }

  return (
    <div className="container">
      <h1>Sample App</h1>
      <p className="subtitle">Demonstrates OIDC authorization code + PKCE with Authagonal</p>

      {error && <div className="error">{error}</div>}

      {!auth ? (
        <div className="card">
          <h2>Not authenticated</h2>
          <p>Click below to sign in via Authagonal using the authorization code flow with PKCE.</p>
          <div className="button-group">
            <button className="btn-primary" onClick={login}>Sign in with Authagonal</button>
          </div>
        </div>
      ) : (
        <>
          <div className="card">
            <h2>Authenticated</h2>
            <dl className="user-info">
              <dt>User ID</dt>
              <dd>{auth.user?.sub ?? 'unknown'}</dd>
              <dt>Email</dt>
              <dd>{auth.user?.email ?? 'unknown'}</dd>
              <dt>Name</dt>
              <dd>{auth.user?.name ?? 'unknown'}</dd>
              <dt>Token expires</dt>
              <dd>{new Date(auth.expiresAt).toLocaleTimeString()}</dd>
            </dl>
            <div className="button-group">
              <button className="btn-danger" onClick={handleLogout}>Sign out</button>
            </div>
          </div>

          <div className="card">
            <h2>Call the Sample API</h2>
            <p>These requests include the JWT access token from Authagonal in the Authorization header.</p>
            <div className="button-group">
              <button className="btn-secondary" onClick={() => callApi('/api/protected')}>
                GET /api/protected
              </button>
              <button className="btn-secondary" onClick={() => callApi('/api/todos')}>
                GET /api/todos
              </button>
              <button className="btn-secondary" onClick={() => callApi('/api/public')}>
                GET /api/public (no auth)
              </button>
            </div>
          </div>

          {apiResult && (
            <div className="card">
              <h2>API Response</h2>
              <pre>{apiResult}</pre>
            </div>
          )}

          <div className="card">
            <h2>Access Token</h2>
            <pre>{auth.accessToken}</pre>
          </div>
        </>
      )}
    </div>
  );
}

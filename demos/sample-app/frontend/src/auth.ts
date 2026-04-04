// Minimal OIDC client — authorization code + PKCE, no external libraries.

interface AppConfig {
  authServer: string;
  redirectUri: string;
  clientId: string;
  scopes: string;
}

const DEFAULT_CONFIG: AppConfig = {
  authServer: 'http://localhost:8080',
  redirectUri: `${window.location.origin}/callback`,
  clientId: 'sample-app',
  scopes: 'openid profile email offline_access',
};

let _config: AppConfig | null = null;

export async function getConfig(): Promise<AppConfig> {
  if (_config) return _config;
  try {
    const res = await fetch('/config.json');
    if (res.ok) {
      const json = await res.json();
      _config = {
        authServer: json.AUTH_SERVER || DEFAULT_CONFIG.authServer,
        redirectUri: json.REDIRECT_URI || DEFAULT_CONFIG.redirectUri,
        clientId: json.CLIENT_ID || DEFAULT_CONFIG.clientId,
        scopes: json.SCOPES || DEFAULT_CONFIG.scopes,
      };
    }
  } catch {
    // config.json not available (local dev) — use defaults
  }
  if (!_config) _config = DEFAULT_CONFIG;
  return _config;
}

interface TokenResponse {
  access_token: string;
  id_token?: string;
  refresh_token?: string;
  expires_in: number;
  scope: string;
}

export interface AuthState {
  accessToken: string;
  idToken?: string;
  refreshToken?: string;
  expiresAt: number;
  user?: { sub: string; email?: string; name?: string };
}

// ---------------------------------------------------------------------------
// PKCE helpers
// ---------------------------------------------------------------------------

function base64UrlEncode(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

async function generateCodeVerifier(): Promise<string> {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  return base64UrlEncode(bytes.buffer);
}

async function generateCodeChallenge(verifier: string): Promise<string> {
  const encoded = new TextEncoder().encode(verifier);
  const digest = await crypto.subtle.digest('SHA-256', encoded);
  return base64UrlEncode(digest);
}

// ---------------------------------------------------------------------------
// Auth flow
// ---------------------------------------------------------------------------

/** Redirect the user to Authagonal's authorize endpoint. */
export async function login(): Promise<void> {
  const config = await getConfig();
  const codeVerifier = await generateCodeVerifier();
  const codeChallenge = await generateCodeChallenge(codeVerifier);
  const state = base64UrlEncode(crypto.getRandomValues(new Uint8Array(16)).buffer);

  // Store PKCE verifier and state for the callback
  sessionStorage.setItem('pkce_verifier', codeVerifier);
  sessionStorage.setItem('oauth_state', state);

  const params = new URLSearchParams({
    client_id: config.clientId,
    redirect_uri: config.redirectUri,
    response_type: 'code',
    scope: config.scopes,
    state,
    code_challenge: codeChallenge,
    code_challenge_method: 'S256',
  });

  window.location.href = `${config.authServer}/connect/authorize?${params}`;
}

/** Handle the callback — exchange the authorization code for tokens. */
export async function handleCallback(): Promise<AuthState | null> {
  const params = new URLSearchParams(window.location.search);
  const code = params.get('code');
  const state = params.get('state');
  const error = params.get('error');

  if (error) {
    throw new Error(params.get('error_description') || error);
  }

  if (!code || !state) return null;

  // Validate state
  const expectedState = sessionStorage.getItem('oauth_state');
  if (state !== expectedState) {
    throw new Error('State mismatch — possible CSRF attack');
  }

  const codeVerifier = sessionStorage.getItem('pkce_verifier');
  if (!codeVerifier) {
    throw new Error('Missing PKCE verifier');
  }

  // Clean up
  sessionStorage.removeItem('oauth_state');
  sessionStorage.removeItem('pkce_verifier');

  // Exchange code for tokens
  const config = await getConfig();
  const body = new URLSearchParams({
    grant_type: 'authorization_code',
    code,
    redirect_uri: config.redirectUri,
    client_id: config.clientId,
    code_verifier: codeVerifier,
  });

  const response = await fetch(`${config.authServer}/connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body,
  });

  if (!response.ok) {
    const err = await response.json();
    throw new Error(err.error_description || err.error || 'Token exchange failed');
  }

  const tokens: TokenResponse = await response.json();
  const authState = tokensToState(tokens);

  // Persist
  localStorage.setItem('auth', JSON.stringify(authState));

  // Clean the URL
  window.history.replaceState({}, '', '/');

  return authState;
}

/** Get the current auth state from storage. Returns null if expired and no refresh token. */
export function getStoredAuth(): AuthState | null {
  const raw = localStorage.getItem('auth');
  if (!raw) return null;

  const state: AuthState = JSON.parse(raw);

  // If not expired (with 60s buffer), return as-is
  if (Date.now() <= state.expiresAt - 60_000) {
    return state;
  }

  // Expired but has refresh token — return it so the caller can refresh
  if (state.refreshToken) {
    return state;
  }

  localStorage.removeItem('auth');
  return null;
}

/** Check if the auth state needs a token refresh. */
export function needsRefresh(state: AuthState): boolean {
  return Date.now() > state.expiresAt - 60_000;
}

/** Refresh the access token using a stored refresh token. */
export async function refreshAccessToken(current: AuthState): Promise<AuthState | null> {
  if (!current.refreshToken) return null;

  const config = await getConfig();
  const body = new URLSearchParams({
    grant_type: 'refresh_token',
    refresh_token: current.refreshToken,
    client_id: config.clientId,
  });

  try {
    const response = await fetch(`${config.authServer}/connect/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body,
    });

    if (!response.ok) {
      localStorage.removeItem('auth');
      return null;
    }

    const tokens: TokenResponse = await response.json();
    const authState = tokensToState(tokens);

    // Preserve the refresh token if the server didn't issue a new one
    if (!authState.refreshToken && current.refreshToken) {
      authState.refreshToken = current.refreshToken;
    }

    localStorage.setItem('auth', JSON.stringify(authState));
    return authState;
  } catch {
    localStorage.removeItem('auth');
    return null;
  }
}

/** Log out — clear local state and end the SSO session on the auth server. */
export async function logout(): Promise<void> {
  const stored = localStorage.getItem('auth');
  localStorage.removeItem('auth');

  const config = await getConfig();
  const params = new URLSearchParams({
    post_logout_redirect_uri: window.location.origin,
  });

  if (stored) {
    try {
      const state: AuthState = JSON.parse(stored);
      if (state.idToken) {
        params.set('id_token_hint', state.idToken);
      }
    } catch { /* ignore */ }
  }

  window.location.href = `${config.authServer}/connect/endsession?${params}`;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function tokensToState(tokens: TokenResponse): AuthState {
  const state: AuthState = {
    accessToken: tokens.access_token,
    idToken: tokens.id_token,
    refreshToken: tokens.refresh_token,
    expiresAt: Date.now() + tokens.expires_in * 1000,
  };

  // Decode the ID token payload to get user info
  if (tokens.id_token) {
    try {
      const payload = tokens.id_token.split('.')[1];
      const decoded = JSON.parse(atob(payload.replace(/-/g, '+').replace(/_/g, '/')));
      state.user = {
        sub: decoded.sub,
        email: decoded.email,
        name: decoded.name,
      };
    } catch {
      // ID token parsing is best-effort for display
    }
  }

  return state;
}

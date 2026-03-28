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

async function getConfig(): Promise<AppConfig> {
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

/** Get the current auth state from storage. */
export function getStoredAuth(): AuthState | null {
  const raw = localStorage.getItem('auth');
  if (!raw) return null;

  const state: AuthState = JSON.parse(raw);

  // Check expiry (with 60s buffer)
  if (Date.now() > state.expiresAt - 60_000) {
    localStorage.removeItem('auth');
    return null;
  }

  return state;
}

/** Log out — clear local state. */
export function logout(): void {
  localStorage.removeItem('auth');
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

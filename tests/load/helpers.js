import http from "k6/http";
import { check } from "k6";
import { SharedArray } from "k6/data";

// ---------------------------------------------------------------------------
// Config — override via environment variables
// ---------------------------------------------------------------------------
// Defaults to LOCALHOST, not the public demo host. It used to default to the real deployment, so an
// unparameterised `k6 run` pointed a load generator at a live service — and, with the secret below,
// authenticated to it.
export const BASE_URL = __ENV.BASE_URL || "http://localhost:5000";
export const CLIENT_ID = __ENV.CLIENT_ID || "load-test";

// REQUIRED, with no default. It used to fall back to a literal "load-test-secret", which put a
// client_credentials secret for a real host in a public repository — and made it the credential anyone
// running this script against that host would present. A load test that cannot authenticate should say so
// rather than quietly try a guessable secret.
export const CLIENT_SECRET = __ENV.CLIENT_SECRET;
if (!CLIENT_SECRET) {
  throw new Error(
    "CLIENT_SECRET is required: pass it with -e CLIENT_SECRET=… . There is deliberately no default — " +
      "a secret with a default is a secret in the repository."
  );
}
export const ADMIN_TOKEN = __ENV.ADMIN_TOKEN || ""; // JWT with authagonal-admin scope

// ---------------------------------------------------------------------------
// Auth helpers
// ---------------------------------------------------------------------------

/** Obtain a token via client_credentials grant. */
export function clientCredentialsToken() {
  const res = http.post(`${BASE_URL}/connect/token`, {
    grant_type: "client_credentials",
    client_id: CLIENT_ID,
    client_secret: CLIENT_SECRET,
    scope: "openid profile email",
  });
  check(res, { "token 200": (r) => r.status === 200 });
  return res;
}

/** Register a throwaway test user and return { email, password }. */
export function registerUser(tag) {
  const email = `loadtest+${tag}-${Date.now()}@example.com`;
  const password = "LoadTest1!Aa";
  const res = http.post(
    `${BASE_URL}/api/auth/register`,
    JSON.stringify({ email, password, firstName: "Load", lastName: "Test" }),
    { headers: { "Content-Type": "application/json" } }
  );
  check(res, { "register 2xx": (r) => r.status >= 200 && r.status < 300 });
  return { email, password, res };
}

/** Login with email/password (cookie-based). */
export function login(email, password) {
  const res = http.post(
    `${BASE_URL}/api/auth/login`,
    JSON.stringify({ email, password }),
    { headers: { "Content-Type": "application/json" } }
  );
  return res;
}

/** Check session (uses cookies from jar). */
export function getSession() {
  return http.get(`${BASE_URL}/api/auth/session`);
}

/** Fetch OIDC discovery document. */
export function getDiscovery() {
  return http.get(`${BASE_URL}/.well-known/openid-configuration`);
}

/** Fetch JWKS. */
export function getJwks() {
  return http.get(`${BASE_URL}/jwks`);
}

/** Fetch password policy. */
export function getPasswordPolicy() {
  return http.get(`${BASE_URL}/api/auth/password-policy`);
}

/** SSO check for a domain. */
export function ssoCheck(email) {
  return http.get(`${BASE_URL}/api/auth/sso-check?email=${encodeURIComponent(email)}`);
}

/** Admin: list users (requires ADMIN_TOKEN). */
export function adminGetUser(userId) {
  return http.get(`${BASE_URL}/api/v1/profile/${userId}`, {
    headers: { Authorization: `Bearer ${ADMIN_TOKEN}` },
  });
}

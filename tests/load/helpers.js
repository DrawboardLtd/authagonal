import http from "k6/http";
import { check } from "k6";
import { SharedArray } from "k6/data";

// ---------------------------------------------------------------------------
// Config — override via environment variables
// ---------------------------------------------------------------------------
export const BASE_URL =
  __ENV.BASE_URL || "https://sso.demo.authagonal.drawboard.com";
export const CLIENT_ID = __ENV.CLIENT_ID || "load-test";
export const CLIENT_SECRET = __ENV.CLIENT_SECRET || "load-test-secret";
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

/**
 * Smoke test — quick sanity check that core endpoints are alive.
 *
 * Run after every deploy or as a health check.
 *
 *   k6 run tests/load/smoke.js
 *   k6 run tests/load/smoke.js -e BASE_URL=https://sso.demo.authagonal.drawboard.com
 */

import { check, sleep } from "k6";
import {
  BASE_URL,
  clientCredentialsToken,
  getDiscovery,
  getJwks,
  getPasswordPolicy,
  ssoCheck,
} from "./helpers.js";

export const options = {
  vus: 1,
  iterations: 1,
  thresholds: {
    checks: ["rate==1.0"],
    http_req_failed: ["rate==0.0"],
  },
};

export default function () {
  // 1. Discovery
  const disco = getDiscovery();
  check(disco, {
    "discovery 200": (r) => r.status === 200,
    "has issuer": (r) => r.json().issuer !== undefined,
    "has token_endpoint": (r) => r.json().token_endpoint !== undefined,
  });

  // 2. JWKS
  const jwks = getJwks();
  check(jwks, {
    "jwks 200": (r) => r.status === 200,
    "has at least one key": (r) => r.json().keys && r.json().keys.length > 0,
  });

  // 3. client_credentials token
  const tokenRes = clientCredentialsToken();
  check(tokenRes, {
    "token 200": (r) => r.status === 200,
    "has access_token": (r) => r.json().access_token !== undefined,
  });

  // 4. Password policy
  const policy = getPasswordPolicy();
  check(policy, {
    "policy 200": (r) => r.status === 200,
    "has rules": (r) => r.json().rules && r.json().rules.length > 0,
  });

  // 5. SSO check
  const sso = ssoCheck("nobody@example.com");
  check(sso, {
    "sso-check 200": (r) => r.status === 200,
    "sso not required": (r) => r.json().ssoRequired === false,
  });

  console.log(`Smoke test passed against ${BASE_URL}`);
}

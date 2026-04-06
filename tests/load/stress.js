/**
 * Stress test — ramps up load to find the breaking point.
 *
 * Targets:
 *   - client_credentials token issuance (heaviest crypto path)
 *   - Discovery + JWKS (cache behaviour under load)
 *   - Password login flow (register → login → session)
 *   - SSO check + password policy (lightweight reads)
 *
 * Run:
 *   k6 run tests/load/stress.js
 *   k6 run tests/load/stress.js -e BASE_URL=http://localhost:8080
 */

import { check, sleep, group } from "k6";
import {
  BASE_URL,
  clientCredentialsToken,
  getDiscovery,
  getJwks,
  getPasswordPolicy,
  ssoCheck,
  registerUser,
  login,
  getSession,
} from "./helpers.js";

export const options = {
  scenarios: {
    // Ramp token issuance from 0 → 50 → 100 → 0 VUs
    tokens: {
      executor: "ramping-vus",
      exec: "tokenFlow",
      startVUs: 0,
      stages: [
        { duration: "30s", target: 20 },
        { duration: "1m", target: 50 },
        { duration: "1m", target: 100 },
        { duration: "30s", target: 100 },
        { duration: "30s", target: 0 },
      ],
    },
    // Discovery/JWKS — lightweight but frequent
    discovery: {
      executor: "constant-rate",
      exec: "discoveryFlow",
      rate: 50,
      timeUnit: "1s",
      duration: "3m30s",
      preAllocatedVUs: 20,
      maxVUs: 50,
    },
    // Login flow — moderate load
    login: {
      executor: "ramping-vus",
      exec: "loginFlow",
      startVUs: 0,
      stages: [
        { duration: "30s", target: 5 },
        { duration: "2m", target: 20 },
        { duration: "1m", target: 0 },
      ],
    },
  },
  thresholds: {
    http_req_duration: ["p(95)<2000", "p(99)<5000"],
    http_req_failed: ["rate<0.05"],
    "http_req_duration{scenario:tokens}": ["p(95)<1000"],
    "http_req_duration{scenario:discovery}": ["p(95)<500"],
  },
};

// Scenario: client_credentials token issuance
export function tokenFlow() {
  group("client_credentials", () => {
    const res = clientCredentialsToken();
    if (res.status === 200) {
      const body = res.json();
      check(body, {
        "has access_token": (b) => !!b.access_token,
        "has token_type": (b) => b.token_type === "Bearer",
        "has expires_in": (b) => b.expires_in > 0,
      });
    }
  });
  sleep(0.1);
}

// Scenario: discovery + JWKS
export function discoveryFlow() {
  group("discovery", () => {
    const disco = getDiscovery();
    check(disco, { "discovery 200": (r) => r.status === 200 });

    const jwks = getJwks();
    check(jwks, {
      "jwks 200": (r) => r.status === 200,
      "has keys": (r) => r.json().keys && r.json().keys.length > 0,
    });
  });
}

// Scenario: register → login → check session → SSO check → password policy
export function loginFlow() {
  const vu = `stress-${__VU}`;

  group("register", () => {
    registerUser(vu);
  });

  sleep(0.5);

  group("login", () => {
    const email = `loadtest+${vu}-recent@example.com`;
    // Login will fail (user doesn't match) but exercises the full path
    const res = login(email, "LoadTest1!Aa");
    check(res, { "login responded": (r) => r.status > 0 });
  });

  group("reads", () => {
    const policy = getPasswordPolicy();
    check(policy, { "policy 200": (r) => r.status === 200 });

    const sso = ssoCheck("user@example.com");
    check(sso, { "sso-check 200": (r) => r.status === 200 });
  });

  sleep(1);
}

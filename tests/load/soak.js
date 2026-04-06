/**
 * Soak test — sustained moderate load over an extended period.
 *
 * Looks for: memory leaks, connection pool exhaustion, Table Storage
 * throttling, signing key cache drift, rate limiter state bloat.
 *
 * Run:
 *   k6 run tests/load/soak.js
 *   k6 run tests/load/soak.js -e BASE_URL=http://localhost:8080 -e DURATION=10m
 */

import { check, sleep, group } from "k6";
import {
  clientCredentialsToken,
  getDiscovery,
  getJwks,
  getPasswordPolicy,
  ssoCheck,
  registerUser,
  login,
  getSession,
} from "./helpers.js";

const DURATION = __ENV.DURATION || "30m";

export const options = {
  scenarios: {
    // Steady token issuance — mimics service-to-service traffic
    tokens: {
      executor: "constant-rate",
      exec: "tokenFlow",
      rate: 10,
      timeUnit: "1s",
      duration: DURATION,
      preAllocatedVUs: 10,
      maxVUs: 30,
    },
    // Periodic discovery/JWKS — mimics downstream services refreshing keys
    discovery: {
      executor: "constant-rate",
      exec: "discoveryFlow",
      rate: 2,
      timeUnit: "1s",
      duration: DURATION,
      preAllocatedVUs: 5,
      maxVUs: 10,
    },
    // Simulated user login traffic — low but constant
    users: {
      executor: "constant-rate",
      exec: "userFlow",
      rate: 1,
      timeUnit: "1s",
      duration: DURATION,
      preAllocatedVUs: 5,
      maxVUs: 15,
    },
  },
  thresholds: {
    http_req_duration: ["p(95)<2000", "p(99)<5000"],
    http_req_failed: ["rate<0.01"],
    // Token issuance should stay consistent over time (no degradation)
    "http_req_duration{scenario:tokens}": ["p(95)<1000", "med<500"],
    // Discovery is cached — should be fast
    "http_req_duration{scenario:discovery}": ["p(95)<300"],
  },
};

// Steady client_credentials token flow
export function tokenFlow() {
  const res = clientCredentialsToken();
  if (res.status === 200) {
    check(res.json(), {
      "has access_token": (b) => !!b.access_token,
    });
  }
}

// Periodic discovery + JWKS check
export function discoveryFlow() {
  const disco = getDiscovery();
  check(disco, { "discovery 200": (r) => r.status === 200 });

  const jwks = getJwks();
  check(jwks, { "jwks 200": (r) => r.status === 200 });
}

// User activity: register, login attempt, read endpoints
export function userFlow() {
  const tag = `soak-${__VU}-${__ITER}`;

  group("register+login", () => {
    const { email, password } = registerUser(tag);
    sleep(0.2);
    const loginRes = login(email, password);
    check(loginRes, { "login responded": (r) => r.status > 0 });
  });

  group("reads", () => {
    getPasswordPolicy();
    ssoCheck("nobody@example.com");
    getSession();
  });

  sleep(0.5);
}

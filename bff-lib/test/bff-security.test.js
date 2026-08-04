// Security coverage for the TypeScript BFF, the twin of Authagonal.Bff. Each of these guards exists
// in the .NET proxy and store and had drifted out of this package; the tests are here so the two
// implementations cannot silently diverge again.
//
// Plain JS against the compiled output (`npm test` builds first) — the package ships no test
// framework, and node:test + node:assert need none.
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { prefixMatches, composeTarget, authorizeProxy, routeBff } from '../dist/core.js';
import { MemorySessionStore } from '../dist/session.js';
import { resolveOptions } from '../dist/options.js';
import { assertTrustedMetadata } from '../dist/oidc.js';

// ---- #214: upstream selection must respect segment boundaries ----

test('prefixMatches only matches on a segment boundary', () => {
  // Same table as tests/Authagonal.Tests/BffSecurityTests.PrefixMatches_respects_segment_boundary.
  const cases = [
    ['/identity/x', '/id', false],
    ['/id/x', '/id', true],
    ['/id', '/id', true],
    ['/orders/1', '/orders', true],
    ['/ordersX', '/orders', false],
    ['/api/v1/x', '/api/', true],
    ['/userdata/secrets', '/user', false],
    ['/user', '/user', true],
    ['/user/me', '/user', true],
  ];
  for (const [path, prefix, expected] of cases) {
    assert.equal(prefixMatches(path, prefix), expected, `${path} vs ${prefix}`);
  }
});

test('a request under a lookalike path does not reach an unrelated upstream with the bearer token', async () => {
  const d = deps({
    upstreams: [
      { prefix: '/user', targetBaseUrl: 'https://profiles.internal.example' },
      { prefix: '/orders', targetBaseUrl: 'https://orders.internal.example' },
    ],
  });
  await d.store.set(session());

  const signedIn = { cookies: { 'agbff-test': 's-1' } };

  // "/userdata" is a different first segment; nothing is configured for it.
  const miss = await authorizeProxy(ctx({ path: '/bff/api/userdata/secrets', ...signedIn }), d);
  assert.deepEqual(miss, { error: 404 });

  const hit = await authorizeProxy(ctx({ path: '/bff/api/user/me', ...signedIn }), d);
  assert.equal(hit.targetUrl, 'https://profiles.internal.example/user/me');
  assert.equal(hit.accessToken, 'at-1');
});

// ---- #214: composed target must still address the configured upstream ----

test('composeTarget keeps the upstream authority', () => {
  assert.equal(
    composeTarget('https://api.internal.example', '/orders/1', '?page=2'),
    'https://api.internal.example/orders/1?page=2',
  );
  // A base path is preserved, with exactly one slash at the join.
  assert.equal(
    composeTarget('https://api.internal.example/v1/', '/orders/1', ''),
    'https://api.internal.example/v1/orders/1',
  );
  // A relative path is forced absolute so it can never be read as a host label.
  assert.equal(
    composeTarget('https://api.internal.example/v1', 'orders', ''),
    'https://api.internal.example/v1/orders',
  );
  // Escapes: protocol-relative, and a backslash the WHATWG parser normalizes into one.
  assert.equal(composeTarget('https://api.internal.example', '//evil.example/x', ''), null);
  assert.equal(composeTarget('https://api.internal.example', '/\\evil.example/x', ''), null);
  // An absolute URL is contained rather than followed — forcing the leading '/' makes it a path.
  assert.equal(
    composeTarget('https://api.internal.example', 'https://evil.example/x', ''),
    'https://api.internal.example/https://evil.example/x',
  );
  // A misconfigured upstream is refused rather than fetched.
  assert.equal(composeTarget('not-a-url', '/x', ''), null);
});

// ---- #158: the session store's secondary indexes are per tenant ----

test('a subject-scoped purge only touches the tenant it was issued for', async () => {
  const store = new MemorySessionStore();
  await store.set(session({ sessionId: 's-a', tenantKey: 'tenant-a', subject: 'user-1', sid: 'sid-1' }));
  await store.set(session({ sessionId: 's-b', tenantKey: 'tenant-b', subject: 'user-1', sid: 'sid-1' }));

  assert.equal(await store.removeBySubject('user-1', 'tenant-a'), 1);
  assert.equal(await store.get('s-a'), null);
  assert.notEqual(await store.get('s-b'), null, "tenant B's session survived a tenant A logout");

  assert.equal(await store.removeBySid('sid-1', 'tenant-b'), 1);
  assert.equal(await store.get('s-b'), null);
});

test('a purge for the wrong tenant removes nothing', async () => {
  const store = new MemorySessionStore();
  await store.set(session({ sessionId: 's-a', tenantKey: 'tenant-a', subject: 'user-1' }));

  assert.equal(await store.removeBySubject('user-1', 'tenant-b'), 0);
  assert.notEqual(await store.get('s-a'), null);
});

test('the tenant key cannot be smuggled through the index separator', async () => {
  // Without percent-encoding the tenant key, "a:b" + "c" and "a" + "b:c" produce the same index key.
  const store = new MemorySessionStore();
  await store.set(session({ sessionId: 's-1', tenantKey: 'a:b', subject: 'c' }));

  assert.equal(await store.removeBySubject('b:c', 'a'), 0);
  assert.notEqual(await store.get('s-1'), null);
});

// ---- #231: back-channel logout purges in the signing tenant's namespace ----

test('back-channel logout scopes the purge to the tenant that signed the token', async () => {
  const calls = [];
  const store = new MemorySessionStore();
  store.removeBySubject = async (subject, tenantKey) => { calls.push(['sub', subject, tenantKey]); return 0; };
  store.removeBySid = async (sid, tenantKey) => { calls.push(['sid', sid, tenantKey]); return 0; };

  const d = deps({}, {
    store,
    tenants: {
      resolve: async () => null,
      resolveByIssuer: async (iss) =>
        iss === 'https://b.example' ? { tenantKey: 'tenant-b', authority: iss, clientId: 'c', clientSecret: 's', scope: [] } : null,
    },
    oidcFor: () => ({
      verifyLogoutToken: async () => ({
        iss: 'https://b.example',
        sub: 'user-1',
        events: { 'http://schemas.openid.net/event/backchannel-logout': {} },
      }),
    }),
  });

  const c = ctx({
    method: 'POST',
    path: '/bff/backchannel-logout',
    form: new URLSearchParams({ logout_token: unsignedJwt({ iss: 'https://b.example', sub: 'user-1' }) }),
  });
  assert.equal(await routeBff(c, d), true);
  assert.equal(c.status, 200);
  assert.deepEqual(calls, [['sub', 'user-1', 'tenant-b']]);
});

// ---- #303: /bff/user must not be cached ----

test('/bff/user is uncacheable on every path', async () => {
  const d = deps();
  await d.store.set(session());

  const anon = ctx({ path: '/bff/user' });
  await routeBff(anon, d);
  assert.equal(anon.headers['Cache-Control'], 'no-store');
  assert.equal(anon.headers['Vary'], 'Cookie');
  assert.deepEqual(anon.body, { isAuthenticated: false });

  const signedIn = ctx({ path: '/bff/user', cookies: { 'agbff-test': 's-1' } });
  await routeBff(signedIn, d);
  assert.equal(signedIn.headers['Cache-Control'], 'no-store');
  assert.equal(signedIn.headers['Vary'], 'Cookie');
  assert.equal(signedIn.body.isAuthenticated, true);
});

// ---- helpers ----

function deps(optionOverrides = {}, depOverrides = {}) {
  const o = resolveOptions({
    authority: 'https://auth.example',
    clientId: 'bff',
    clientSecret: 'secret',
    cookieSecret: 'x'.repeat(32),
    cookieName: 'agbff-test',
    ...optionOverrides,
  });
  return {
    o,
    store: new MemorySessionStore(),
    protector: { protect: async (v) => v, unprotect: async (v) => v },
    tenants: { resolve: async () => null, resolveByIssuer: async () => null },
    oidcFor: () => { throw new Error('no OIDC client expected in this test'); },
    // The sessions below are minted with a distant expiry, so the real coordinator would return them
    // unchanged; stubbing keeps the test off the network either way.
    refresher: { ensureFresh: async (s) => s },
    log: () => {},
    ...depOverrides,
  };
}

function session(overrides = {}) {
  const hour = 60 * 60 * 1000;
  return {
    sessionId: 's-1',
    subject: 'user-1',
    idToken: 'id',
    accessToken: 'at-1',
    accessTokenExpiresAt: Date.now() + hour,
    expiresAt: Date.now() + hour,
    claims: { email: 'a@example.com' },
    ...overrides,
  };
}

function ctx({ method = 'GET', path = '/', query = '', cookies = {}, form = new URLSearchParams() } = {}) {
  const headers = {};
  const c = {
    method,
    path,
    query: new URLSearchParams(query),
    origin: 'https://app.example',
    headers,
    status: 200,
    body: undefined,
    getCookie: (name) => cookies[name],
    setCookie: () => {},
    deleteCookie: () => {},
    // Every handler checks the anti-forgery header by presence only.
    getHeader: (name) => (name === 'x-authagonal-bff' ? '1' : undefined),
    setHeader: (name, value) => { headers[name] = value; },
    readForm: async () => form,
    redirect: (url) => { c.status = 302; c.body = url; },
    json: (b, s = 200) => { c.status = s; c.body = b; },
    text: (b, s = 200) => { c.status = s; c.body = b; },
  };
  return c;
}

/** A well-formed but unsigned JWT. handleBackchannel decodes `iss` without verifying it (only to pick
 * the tenant), and this test stubs the verification that follows. */
function unsignedJwt(payload) {
  const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64url');
  return `${b64({ alg: 'none', typ: 'JWT' })}.${b64(payload)}.`;
}

// ---- Discovery is the trust anchor: neither implementation checked it ----
//
// `issuer` out of the document becomes the `issuer` option jwtVerify is given, and `jwks_uri` out of the
// same document supplies the keys it verifies against — so anyone able to answer the metadata URL could
// mint an id_token for any `sub` and be handed a BFF session for that user. Mirrors
// tests/Authagonal.Tests/BffDiscoveryTrustTests.cs.

test('a discovery document declaring someone else as issuer is refused', () => {
  assert.throws(
    () => assertTrustedMetadata('https://auth.example', {
      issuer: 'https://evil.example',
      authorization_endpoint: 'https://auth.example/connect/authorize',
      token_endpoint: 'https://auth.example/connect/token',
      jwks_uri: 'https://auth.example/.well-known/jwks',
    }),
    /issuer mismatch/,
  );
});

test('a matching issuer is accepted, trailing slash and case notwithstanding', () => {
  assertTrustedMetadata('https://auth.example', {
    issuer: 'https://auth.example/',
    authorization_endpoint: 'https://auth.example/connect/authorize',
    token_endpoint: 'https://auth.example/connect/token',
    jwks_uri: 'https://auth.example/.well-known/jwks',
  });
});

test('an https authority will not accept a plaintext endpoint the document names', () => {
  // Each of these is a full compromise on its own: the keys, the client secret plus authorization code,
  // and the id_token handed to an attacker-named host by GET /bff/logout.
  for (const field of ['jwks_uri', 'token_endpoint', 'end_session_endpoint']) {
    const m = {
      issuer: 'https://auth.example',
      authorization_endpoint: 'https://auth.example/connect/authorize',
      token_endpoint: 'https://auth.example/connect/token',
      jwks_uri: 'https://auth.example/.well-known/jwks',
    };
    m[field] = 'http://evil.example/x';
    assert.throws(() => assertTrustedMetadata('https://auth.example', m), /non-https/, field);
  }
});

test('a private-network http authority stays supported', () => {
  // AuthagonalBffExtensions calls this a supported topology, and requireHttps exists to permit it. Such a
  // deployment already accepted plaintext on that path; the issuer binding still applies.
  assertTrustedMetadata('http://auth.internal:8080', {
    issuer: 'http://auth.internal:8080',
    authorization_endpoint: 'http://auth.internal:8080/connect/authorize',
    token_endpoint: 'http://auth.internal:8080/connect/token',
    jwks_uri: 'http://auth.internal:8080/.well-known/jwks',
  });

  assert.throws(
    () => assertTrustedMetadata('http://auth.internal:8080', {
      issuer: 'https://evil.example',
      authorization_endpoint: 'http://auth.internal:8080/connect/authorize',
      token_endpoint: 'http://auth.internal:8080/connect/token',
      jwks_uri: 'https://evil.example/jwks',
    }),
    /issuer mismatch/,
  );
});

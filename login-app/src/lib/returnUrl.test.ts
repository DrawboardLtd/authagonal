import { describe, expect, it, vi, beforeEach } from 'vitest';

import { isSameOriginPath, resolveRedirect } from './returnUrl';
import { getApps } from '../api';

vi.mock('../api', () => ({ getApps: vi.fn() }));

const mockedGetApps = vi.mocked(getApps);

/**
 * The SPA's open-redirect control, which had no tests at all while both of its twins
 * (`Authagonal.Core/Services/LocalRedirect` and the BFF's return-URL check) are table-tested. The login app
 * shipped no test runner, so the third implementation of a control the other two treat as security-critical was
 * the one nobody could assert on.
 *
 * `resolveRedirect`'s absolute-URL branch is the one with consequences: it permits any origin matching a
 * registered client's `homeUri`, resolved from a live `/api/auth/apps` call. A refactor that reordered the
 * `isSameOriginPath` short-circuit, dropped the http/https guard, or compared by prefix instead of by origin
 * would turn it into an open redirect on the auth host — the one origin a phishing target most wants.
 */
describe('isSameOriginPath', () => {
  it.each([
    // [input, expected]
    ['/account', true],
    ['/', true],
    ['/a/b?c=d#e', true],

    // Not relative at all.
    ['https://evil.example/x', false],
    ['http://evil.example/x', false],

    // The scheme-relative form, which a naive startsWith('/') check accepts and the browser reads as a
    // cross-origin URL. This is the classic bypass and the reason the parse is not optional.
    ['//evil.example', false],
    ['//evil.example/path', false],
    ['///evil.example', false],

    // Backslashes: some parsers normalise these to forward slashes.
    ['/\\evil.example', false],
    ['\\/evil.example', false],

    // Neither empty nor absolute-path.
    ['', false],
    ['account', false],
    ['javascript:alert(1)', false],
    ['data:text/html,x', false],
  ])('%j → %s', (input, expected) => {
    expect(isSameOriginPath(input)).toBe(expected);
  });
});

describe('resolveRedirect', () => {
  const fallback = () => '/fallback';

  beforeEach(() => {
    mockedGetApps.mockReset();
  });

  it('returns a same-origin path without consulting the registered apps', async () => {
    expect(await resolveRedirect('/account', fallback)).toBe('/account');
    expect(mockedGetApps).not.toHaveBeenCalled();
  });

  it('permits an absolute URL whose origin matches a registered app home URI', async () => {
    mockedGetApps.mockResolvedValue([{ homeUri: 'https://app.acme.example/dashboard' }] as never);

    expect(await resolveRedirect('https://app.acme.example/welcome', fallback))
      .toBe('https://app.acme.example/welcome');
  });

  it('refuses an absolute URL that matches no registered app', async () => {
    mockedGetApps.mockResolvedValue([{ homeUri: 'https://app.acme.example' }] as never);

    expect(await resolveRedirect('https://evil.example/x', fallback)).toBe('/fallback');
  });

  /**
   * Origin comparison, not prefix or suffix matching — the two mistakes that turn an allow-list into an open
   * redirect. `https://app.acme.example.evil.example` shares a prefix; `https://evil-app.acme.example` shares a
   * suffix; a different port or scheme is a different origin.
   */
  it.each([
    'https://app.acme.example.evil.example/x',
    'https://evil.example/?next=https://app.acme.example',
    'https://app.acme.example:8443/x',
    'http://app.acme.example/x',
  ])('refuses %s, which is not the same origin', async (returnUrl) => {
    mockedGetApps.mockResolvedValue([{ homeUri: 'https://app.acme.example' }] as never);

    expect(await resolveRedirect(returnUrl, fallback)).toBe('/fallback');
  });

  it('refuses a non-http(s) scheme even if it somehow parses', async () => {
    mockedGetApps.mockResolvedValue([{ homeUri: 'https://app.acme.example' }] as never);

    expect(await resolveRedirect('javascript:alert(1)', fallback)).toBe('/fallback');
    expect(await resolveRedirect('data:text/html,<script>x</script>', fallback)).toBe('/fallback');
  });

  /**
   * Fail-closed: an app list that cannot be read means the target is not known to be permitted, so it is not
   * permitted. The `try/catch` around the absolute-URL branch swallows the rejection and falls through, which
   * is the correct direction — the alternative would be an error page on a transient fetch failure, or worse,
   * treating "could not check" as "allowed".
   */
  it('falls back when the app list cannot be read, rather than permitting the target', async () => {
    mockedGetApps.mockRejectedValue(new Error('offline'));

    expect(await resolveRedirect('https://app.acme.example/x', fallback)).toBe('/fallback');
  });

  it('falls back on an empty returnUrl', async () => {
    expect(await resolveRedirect('', fallback)).toBe('/fallback');
    expect(mockedGetApps).not.toHaveBeenCalled();
  });

  it('ignores a registered app whose home URI is unparseable', async () => {
    mockedGetApps.mockResolvedValue([{ homeUri: 'not a url' }, { homeUri: 'https://ok.example' }] as never);

    expect(await resolveRedirect('https://ok.example/x', fallback)).toBe('https://ok.example/x');
    expect(await resolveRedirect('https://evil.example/x', fallback)).toBe('/fallback');
  });
});

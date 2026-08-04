import { describe, expect, it, vi, afterEach } from 'vitest';

import { exportMyData, ApiRequestError } from './api';

/**
 * The GDPR data export's content-type guard.
 *
 * `exportMyData` checked `response.ok` and nothing else. `Authagonal.Server` ends its pipeline with
 * `MapFallbackToFile("index.html")`, so a deployment that has not implemented
 * `GET /api/v1/account/export` — every self-hosted one, since the endpoint is served by Authagonal Cloud —
 * answered that request with **HTTP 200 and this SPA's own HTML**. The client accepted it, returned it as a
 * blob, and the browser saved it as `authagonal-data-export.json`. Someone exercising their Art. 15 right to a
 * copy of their personal data received a page of markup, with no error anywhere in the flow.
 *
 * This is the control that turns that silent success into a refusal, and it is the reason the login app needed a
 * test runner at all: it had none, so a security-relevant client control shipped with nowhere to assert on it.
 */
describe('exportMyData', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const respondWith = (body: string, init: ResponseInit) =>
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(body, init)));

  it('returns the blob when the response is JSON', async () => {
    respondWith('{"email":"a@x.example"}', {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });

    const blob = await exportMyData();
    expect(await blob.text()).toBe('{"email":"a@x.example"}');
  });

  it('accepts a JSON content type carrying a charset', async () => {
    respondWith('{}', { status: 200, headers: { 'content-type': 'application/json; charset=utf-8' } });

    expect(await (await exportMyData()).text()).toBe('{}');
  });

  /** The defect: the SPA shell, 200, text/html — saved as the user's data. */
  it('refuses a 200 that is the SPA fallback rather than JSON', async () => {
    respondWith('<!doctype html><title>Sign in</title>', {
      status: 200,
      headers: { 'content-type': 'text/html; charset=utf-8' },
    });

    await expect(exportMyData()).rejects.toThrow(ApiRequestError);
    await expect(exportMyData()).rejects.toThrow(/not available on this deployment/);
  });

  it('refuses a 200 with no content type at all', async () => {
    respondWith('whatever', { status: 200 });

    await expect(exportMyData()).rejects.toThrow(ApiRequestError);
  });

  it('surfaces a structured API error on a non-2xx JSON response', async () => {
    respondWith('{"error":"forbidden","message":"nope"}', {
      status: 403,
      headers: { 'content-type': 'application/json' },
    });

    await expect(exportMyData()).rejects.toThrow(/nope/);
  });

  it('surfaces the status when a non-2xx response is not JSON', async () => {
    respondWith('<html>502</html>', { status: 502, headers: { 'content-type': 'text/html' } });

    await expect(exportMyData()).rejects.toThrow(/502/);
  });

  it('asks for JSON, so a content-negotiating host has no excuse for returning HTML', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await exportMyData();

    const init = fetchMock.mock.calls[0][1] as RequestInit;
    expect((init.headers as Record<string, string>).accept).toBe('application/json');
    expect(init.credentials).toBe('include');
  });
});

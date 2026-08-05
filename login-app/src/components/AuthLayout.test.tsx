import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import AuthLayout from './AuthLayout';
import { BrandingContext, brandingDefaults, type BrandingConfig } from '../branding';

// -------------------------------------------------------------------------------------------------
// AuthLayout was documented as two things it was not.
//
// The published @authagonal/login README opens its quick start with
// `<Route element={<AuthLayout />}>` wrapping nested page routes, and its exports table describes the
// component as "Layout wrapper, loads branding, renders language selector, wraps <Outlet />".
// `Outlet` appeared nowhere in login-app/src, and `children` was a REQUIRED prop — so the snippet did
// not even typecheck, and rendered (with the prop dropped) an auth card with an empty content area:
// no login form on any route.
//
// It did not load branding either. It read `useBranding()`, which reads context, and `loadBranding()`
// was called only by main.tsx — which an npm consumer does not use. docs/branding.md repeated the
// claim as "The AuthLayout component loads it automatically". That half fails silently: the page
// renders correctly with the default name and colours, and nothing indicates branding.json was never
// requested.
// -------------------------------------------------------------------------------------------------

function branding(overrides: Partial<BrandingConfig> = {}): BrandingConfig {
  return { ...brandingDefaults, ...overrides };
}

describe('AuthLayout as a react-router layout route', () => {
  it('renders the matched child route through Outlet', () => {
    render(
      <MemoryRouter initialEntries={['/login']}>
        <Routes>
          <Route element={<AuthLayout />}>
            <Route path="/login" element={<p>the login form</p>} />
          </Route>
        </Routes>
      </MemoryRouter>,
    );

    // The exact failure the README's quick start produced: the card rendered, the content area was empty.
    expect(screen.getByText('the login form')).toBeTruthy();
  });

  it('still renders children when used as a wrapper, which is how the app itself mounts it', () => {
    render(
      <MemoryRouter>
        <AuthLayout><p>wrapped content</p></AuthLayout>
      </MemoryRouter>,
    );

    expect(screen.getByText('wrapped content')).toBeTruthy();
  });
});

describe('AuthLayout branding', () => {
  it('loads branding itself when no provider is above it', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ appName: 'Acme Corp' }), {
        status: 200, headers: { 'content-type': 'application/json' },
      }),
    );

    render(
      <MemoryRouter>
        <AuthLayout><p>content</p></AuthLayout>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByText('Acme Corp')).toBeTruthy());
    expect(fetchSpy).toHaveBeenCalledWith('/branding.json');
  });

  it('does not fetch when a provider already supplied branding', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    render(
      <MemoryRouter>
        <BrandingContext.Provider value={branding({ appName: 'Provided Inc' })}>
          <AuthLayout><p>content</p></AuthLayout>
        </BrandingContext.Provider>
      </MemoryRouter>,
    );

    expect(screen.getByText('Provided Inc')).toBeTruthy();
    // The reason the context default is `undefined` rather than the defaults object: without that
    // distinction this component cannot tell "nobody provided branding" from "somebody provided the
    // defaults", and self-loading would mean a second fetch in the app that already did it.
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('falls back to the defaults when the fetch fails, rather than rendering nothing', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValue(new Error('offline'));

    render(
      <MemoryRouter>
        <AuthLayout><p>content</p></AuthLayout>
      </MemoryRouter>,
    );

    expect(screen.getByText('content')).toBeTruthy();
    // By the app-name element, not by text: the "Powered by Authagonal" footer carries the same word.
    await waitFor(() => expect(
      document.querySelector('[data-auth="app-name"]')?.textContent,
    ).toBe(brandingDefaults.appName));
  });
});

describe('welcomeTitle and welcomeSubtitle', () => {
  // Typed on BrandingConfig, defaulted, and documented in seven locales as "Override the login page
  // title/subtitle" — and read by no component, which also left `resolveLocalized` an exported no-op.

  it('renders a plain-string welcome title and subtitle', () => {
    render(
      <MemoryRouter>
        <BrandingContext.Provider value={branding({
          welcomeTitle: 'Welcome to Acme',
          welcomeSubtitle: 'Sign in to continue',
        })}>
          <AuthLayout><p>content</p></AuthLayout>
        </BrandingContext.Provider>
      </MemoryRouter>,
    );

    expect(screen.getByText('Welcome to Acme')).toBeTruthy();
    expect(screen.getByText('Sign in to continue')).toBeTruthy();
  });

  it('resolves a per-language welcome title for the active language', () => {
    // The whole point of the LocalizedString type: a tenant supplies their own translations, which is
    // also why there are no shipped ones to fall back to.
    render(
      <MemoryRouter>
        <BrandingContext.Provider value={branding({
          welcomeTitle: { en: 'Welcome to Acme', de: 'Willkommen bei Acme' },
        })}>
          <AuthLayout><p>content</p></AuthLayout>
        </BrandingContext.Provider>
      </MemoryRouter>,
    );

    expect(screen.getByText('Welcome to Acme')).toBeTruthy();
  });

  it('renders no welcome heading at all when branding supplies none', () => {
    render(
      <MemoryRouter>
        <BrandingContext.Provider value={branding()}>
          <AuthLayout><p>content</p></AuthLayout>
        </BrandingContext.Provider>
      </MemoryRouter>,
    );

    // Deliberate: the pages carry their own <CardTitle>, so closing a documentation gap must not add
    // a heading to every existing deployment's login page.
    expect(document.querySelector('[data-auth="welcome-title"]')).toBeNull();
    expect(document.querySelector('[data-auth="welcome-subtitle"]')).toBeNull();
  });
});

import { createContext, useContext } from 'react';

/** A localizable string — either a plain string or an object mapping language codes to strings. */
export type LocalizedString = string | Record<string, string> | null;

export interface BrandingConfig {
  appName: string;
  logoUrl: string | null;
  primaryColor: string;
  supportEmail: string | null;
  showForgotPassword: boolean;
  showRegistration: boolean;
  customCssUrl: string | null;
  welcomeTitle: LocalizedString;
  welcomeSubtitle: LocalizedString;
  languages: { code: string; label: string }[] | null;
  /** When true, show the "Powered by Authagonal" footer on the auth pages. */
  poweredBy: boolean;
  /** Default login-page theme when the visitor hasn't picked one: "off" (light only),
   * "auto" (follow the OS preference), "force" (always dark). The visitor's toggle still wins. */
  darkMode: 'off' | 'auto' | 'force';
  /** Optional per-mode surface-colour overrides for the login page (CSS colours); null = use the
   *  app's built-in theme defaults. darkPrimaryColor overrides primaryColor in dark mode. */
  lightBg: string | null;
  lightCardBg: string | null;
  darkBg: string | null;
  darkCardBg: string | null;
  darkPrimaryColor: string | null;
  /** Optional background for the logo "chip" per mode, so a logo with white/transparent artwork stays
   *  visible against a light card. null = no chip (logo sits directly on the card, current behaviour). */
  lightLogoBg: string | null;
  darkLogoBg: string | null;
}

const defaults: BrandingConfig = {
  appName: 'Authagonal',
  logoUrl: null,
  primaryColor: '#2563eb',
  supportEmail: null,
  showForgotPassword: true,
  showRegistration: false,
  customCssUrl: null,
  welcomeTitle: null,
  welcomeSubtitle: null,
  languages: null,
  poweredBy: true,
  darkMode: 'auto',
  lightBg: null,
  lightCardBg: null,
  darkBg: null,
  darkCardBg: null,
  darkPrimaryColor: null,
  lightLogoBg: null,
  darkLogoBg: null,
};

/**
 * Boot payload a host server may inline into the login document — a
 * `<script type="application/json" id="authagonal-boot">` element — so the SPA
 * renders without waiting on the branding/providers fetches, each of which is a
 * full client→origin round trip that serializes first paint for far-from-origin
 * visitors.
 *
 * A JSON script element rather than an executable `window.__AUTHAGONAL_BOOT__`
 * assignment, and that is not a detail: the server's own CSP is
 * `default-src 'self'` with no `unsafe-inline` and no nonce, so it would block
 * the assignment form on the page that needs it. See `getBoot` below.
 */
export interface AuthagonalBoot {
  branding?: Partial<BrandingConfig>;
  providers?: unknown;
}

export function getBoot(): AuthagonalBoot | undefined {
  // Read the server-inlined boot payload from a non-executable <script type="application/json">
  // tag. A JSON script block is not subject to CSP script-src, so a host with a strict script-src
  // (no 'unsafe-inline'/nonce) can serve it. Absent or malformed → undefined (fetch fallback).
  const el = document.getElementById('authagonal-boot');
  if (!el?.textContent) return undefined;
  try {
    return JSON.parse(el.textContent) as AuthagonalBoot;
  } catch {
    return undefined;
  }
}

export async function loadBranding(): Promise<BrandingConfig> {
  // Server-inlined payload wins: zero round trips. Fetch remains the fallback
  // for hosts that don't inject (dev server, the support SPA on nginx).
  const boot = getBoot();
  if (boot?.branding) return { ...defaults, ...boot.branding };
  try {
    const response = await fetch('/branding.json');
    if (!response.ok) return defaults;
    const json = await response.json();
    return { ...defaults, ...json };
  } catch {
    return defaults;
  }
}

/**
 * The resolved branding for the tree below, or `undefined` when nothing has provided it.
 *
 * The default is deliberately `undefined` rather than `defaults`, so a component can tell "no provider
 * above me" from "a provider that happens to hold the defaults". `AuthLayout` needs that distinction to
 * load branding itself when it is mounted outside a provider — which is what the published README and
 * `docs/branding.md` ("The `AuthLayout` component loads it automatically") describe — without fetching
 * a second time in the app that already provided it.
 *
 * `useBranding()` still always returns a `BrandingConfig`, so nothing that consumes it changes.
 */
export const BrandingContext = createContext<BrandingConfig | undefined>(undefined);

export function useBranding(): BrandingConfig {
  return useContext(BrandingContext) ?? defaults;
}

/** The built-in branding, used until a `branding.json` (or a boot payload) says otherwise. */
export const brandingDefaults: BrandingConfig = defaults;

/** Resolve a LocalizedString to a concrete string for the given language, or null if not set. */
export function resolveLocalized(value: LocalizedString, language: string): string | null {
  if (value == null) return null;
  if (typeof value === 'string') return value;
  // Try exact match, then base language (e.g. "en" from "en-US"), then first available
  return value[language] ?? value[language.split('-')[0]] ?? Object.values(value)[0] ?? null;
}

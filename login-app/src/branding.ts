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
};

/**
 * Boot payload a host server may inline into the login document (a
 * `window.__AUTHAGONAL_BOOT__` script tag) so the SPA renders without waiting
 * on the branding/providers fetches — each one is a full client→origin round
 * trip that serializes first paint for far-from-origin visitors.
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

export const BrandingContext = createContext<BrandingConfig>(defaults);

export function useBranding(): BrandingConfig {
  return useContext(BrandingContext);
}

/** Resolve a LocalizedString to a concrete string for the given language, or null if not set. */
export function resolveLocalized(value: LocalizedString, language: string): string | null {
  if (value == null) return null;
  if (typeof value === 'string') return value;
  // Try exact match, then base language (e.g. "en" from "en-US"), then first available
  return value[language] ?? value[language.split('-')[0]] ?? Object.values(value)[0] ?? null;
}

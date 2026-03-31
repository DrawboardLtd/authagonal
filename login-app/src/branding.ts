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
};

export async function loadBranding(): Promise<BrandingConfig> {
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

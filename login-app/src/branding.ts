import { createContext, useContext } from 'react';

export interface BrandingConfig {
  appName: string;
  logoUrl: string | null;
  primaryColor: string;
  supportEmail: string | null;
  showForgotPassword: boolean;
  customCssUrl: string | null;
}

const defaults: BrandingConfig = {
  appName: 'Authagonal',
  logoUrl: null,
  primaryColor: '#2563eb',
  supportEmail: null,
  showForgotPassword: true,
  customCssUrl: null,
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

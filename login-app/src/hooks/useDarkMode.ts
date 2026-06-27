import { useState, useEffect } from 'react';
import { useBranding } from '../branding';

type Theme = 'light' | 'dark' | 'system';

const STORAGE_KEY = 'auth-theme';

function getSystemPreference(): boolean {
  return window.matchMedia('(prefers-color-scheme: dark)').matches;
}

function applyTheme(theme: Theme) {
  const isDark = theme === 'dark' || (theme === 'system' && getSystemPreference());
  document.documentElement.classList.toggle('dark', isDark);
}

// The tenant's branding.darkMode sets the DEFAULT theme; the visitor's toggle (persisted to
// localStorage) always wins over it.
function brandingDefault(darkMode: string | undefined): Theme {
  switch (darkMode) {
    case 'force': return 'dark';
    case 'off': return 'light';
    case 'auto':
    default: return 'system';
  }
}

export function useDarkMode() {
  const branding = useBranding();
  // null = the visitor hasn't chosen a theme yet → fall back to the tenant's branding default
  // (which may arrive asynchronously, so it's resolved on every render, not just at mount).
  const [override, setOverride] = useState<Theme | null>(
    () => localStorage.getItem(STORAGE_KEY) as Theme | null,
  );
  const theme = override ?? brandingDefault(branding.darkMode);

  useEffect(() => {
    applyTheme(theme);

    if (theme === 'system') {
      const mq = window.matchMedia('(prefers-color-scheme: dark)');
      const handler = () => applyTheme('system');
      mq.addEventListener('change', handler);
      return () => mq.removeEventListener('change', handler);
    }
  }, [theme]);

  const setTheme = (t: Theme) => {
    localStorage.setItem(STORAGE_KEY, t);
    setOverride(t);
  };

  return { theme, setTheme };
}

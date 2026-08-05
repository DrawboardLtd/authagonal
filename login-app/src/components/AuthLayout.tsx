import { useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { Outlet } from 'react-router';
import { Trans, useTranslation } from 'react-i18next';
import { BrandingContext, brandingDefaults, loadBranding, resolveLocalized, type BrandingConfig } from '../branding';
import { useDarkMode } from '../hooks/useDarkMode';
import { Card } from './ui/card';
import { cn } from '@/lib/utils';
import { ChevronDown, Globe, Sun, Moon, Monitor } from 'lucide-react';

// Ensure i18n is initialized when AuthLayout is used (including by npm consumers)
import '../i18n';
import { DEFAULT_LANGUAGES } from '../i18n';

interface AuthLayoutProps {
  /**
   * The page to render inside the card. Omit it to use `AuthLayout` as a react-router layout route,
   * in which case the matched child route renders through `<Outlet />` — which is the shape the
   * published README's quick start uses (`<Route element={<AuthLayout />}>` wrapping nested routes).
   */
  children?: ReactNode;
}

// Accept only hex (#rgb/#rrggbb/#rrggbbaa) or rgb()/rgba()/hsl()/hsla() forms.
function isSafeCssColor(color: string): boolean {
  return /^#(?:[0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$/i.test(color)
    || /^(?:rgb|rgba|hsl|hsla)\([0-9.,%\s/]+\)$/i.test(color);
}

// The picker's default list is the shipped-locale registry (i18n/index.ts DEFAULT_LANGUAGES) — a
// single source of truth, so registering a real locale automatically surfaces it here. Tenants can
// still narrow or extend the offered set via branding.languages.

function ThemeToggle() {
  const { theme, setTheme } = useDarkMode();
  const themes = [
    { value: 'light' as const, icon: Sun },
    { value: 'system' as const, icon: Monitor },
    { value: 'dark' as const, icon: Moon },
  ];
  return (
    <div className="flex items-center justify-center gap-0.5 mt-3" data-auth="theme-toggle">
      {themes.map(({ value, icon: Icon }) => (
        <button
          key={value}
          type="button"
          onClick={() => setTheme(value)}
          className={cn(
            'p-2 rounded cursor-pointer border-none bg-transparent transition-colors',
            theme === value
              ? 'text-gray-700 dark:text-gray-200'
              : 'text-gray-600 dark:text-gray-300 hover:text-gray-900 dark:hover:text-gray-100'
          )}
          title={value}
          aria-label={`${value} theme`}
        >
          <Icon className="h-4 w-4" />
        </button>
      ))}
    </div>
  );
}

// Compact language picker — a single trigger that opens a scrollable popover, so the auth card stays
// tidy no matter how many languages are offered (the old inline row wrapped once past ~8). Opens
// upward since it sits near the bottom of the card. RTL-aware via logical utilities.
function LanguagePicker({ languages }: { languages: { code: string; label: string }[] }) {
  const { i18n } = useTranslation();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const isActive = (code: string) => i18n.language === code || !!i18n.language?.startsWith(code);
  const active = languages.find((l) => isActive(l.code)) ?? languages[0];

  useEffect(() => {
    if (!open) return;
    function handleClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [open]);

  return (
    <div className="relative mt-6 pt-4 border-t border-gray-200 dark:border-gray-800 flex justify-center" data-auth="languages" ref={ref}>
      <button
        type="button"
        data-auth="language-trigger"
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
        className="inline-flex items-center gap-1.5 min-h-[36px] px-3 py-2 text-xs rounded cursor-pointer border-none bg-transparent text-gray-600 dark:text-gray-300 hover:text-gray-900 dark:hover:text-gray-100 hover:bg-gray-100 dark:hover:bg-gray-800 transition-colors"
      >
        <Globe className="h-3.5 w-3.5" />
        {active?.label}
        <ChevronDown className={cn('h-3.5 w-3.5 transition-transform', open && 'rotate-180')} />
      </button>
      {open && (
        <div
          role="listbox"
          className="absolute bottom-full left-1/2 mb-1 -translate-x-1/2 max-h-60 min-w-[10rem] overflow-y-auto rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 py-1 shadow-lg z-50"
        >
          {languages.map((lang) => (
            <button
              key={lang.code}
              type="button"
              role="option"
              aria-selected={active?.code === lang.code}
              data-auth-lang={lang.code}
              onClick={() => { i18n.changeLanguage(lang.code); setOpen(false); }}
              className={cn(
                'w-full text-start px-3 py-1.5 text-xs cursor-pointer border-none transition-colors',
                active?.code === lang.code
                  ? 'bg-primary/10 text-primary font-semibold'
                  : 'bg-transparent text-gray-600 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 hover:text-gray-900 dark:hover:text-gray-100'
              )}
            >
              {lang.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

export default function AuthLayout({ children }: AuthLayoutProps) {
  // Branding: use what a provider supplied, and load it here when nothing did. Both the published
  // README ("loads branding") and docs/branding.md ("The AuthLayout component loads it automatically")
  // describe the second case, and it is the one an npm consumer following the quick start lands in —
  // no BrandingContext.Provider, so branding.json was never requested and the card rendered with the
  // default name and colours while the docs promised otherwise. Reading the context directly rather
  // than through useBranding() is what distinguishes "nothing provided" from "provided the defaults",
  // so the app that already loaded branding does not fetch it a second time.
  const provided = useContext(BrandingContext);
  const [selfLoaded, setSelfLoaded] = useState<BrandingConfig>();
  useEffect(() => {
    if (provided) return;
    let cancelled = false;
    void loadBranding().then((config) => { if (!cancelled) setSelfLoaded(config); });
    return () => { cancelled = true; };
  }, [provided]);
  const branding = provided ?? selfLoaded ?? brandingDefaults;

  // welcomeTitle / welcomeSubtitle: typed on BrandingConfig, defaulted, and documented in seven
  // locales as "Override the login page title/subtitle" — and read by no component, which made
  // resolveLocalized (the function that exists to resolve exactly these) an exported no-op.
  //
  // Rendered only when a tenant sets one. There is deliberately no default greeting: the pages carry
  // their own <CardTitle>, and inventing a heading above them would change every existing
  // deployment's login page to close a documentation gap.
  const { i18n } = useTranslation();
  const welcomeTitle = resolveLocalized(branding.welcomeTitle, i18n.language);
  const welcomeSubtitle = resolveLocalized(branding.welcomeSubtitle, i18n.language);

  useDarkMode();

  useEffect(() => {
    // Per-theme brand colours as CSS vars: :root for light, .dark for dark (toggled by useDarkMode).
    // Injected after the bundled CSS so it wins by source order — and because it's a stylesheet rule
    // (not an inline set), the .dark block can override the light value, so a dark-mode primary /
    // background / card colour can differ from its light counterpart.
    const safe = (v?: string | null) => (v && isSafeCssColor(v) ? v : null);
    const light: string[] = [];
    const dark: string[] = [];
    const add = (arr: string[], name: string, v?: string | null) => { const s = safe(v); if (s) arr.push(`${name}:${s}`); };
    add(light, '--brand-primary', branding.primaryColor);
    add(dark, '--brand-primary', branding.darkPrimaryColor);
    add(light, '--auth-bg', branding.lightBg);
    add(light, '--auth-card-bg', branding.lightCardBg);
    add(dark, '--auth-bg', branding.darkBg);
    add(dark, '--auth-card-bg', branding.darkCardBg);
    add(light, '--auth-logo-bg', branding.lightLogoBg);
    add(dark, '--auth-logo-bg', branding.darkLogoBg);

    let styleEl: HTMLStyleElement | undefined;
    if (light.length || dark.length) {
      styleEl = document.createElement('style');
      styleEl.id = 'branding-theme-vars';
      styleEl.textContent =
        (light.length ? `:root{${light.join(';')}}` : '') + (dark.length ? `.dark{${dark.join(';')}}` : '');
      document.head.appendChild(styleEl);
    }

    let linkEl: HTMLLinkElement | undefined;
    if (branding.customCssUrl) {
      try {
        const parsed = new URL(branding.customCssUrl, window.location.origin);
        if (parsed.origin === window.location.origin) {
          linkEl = document.createElement('link');
          linkEl.rel = 'stylesheet';
          linkEl.href = branding.customCssUrl;
          linkEl.id = 'branding-css';
          document.head.appendChild(linkEl);
        }
      } catch {
        // Invalid URL — skip injecting custom CSS.
      }
    }

    return () => { styleEl?.remove(); linkEl?.remove(); };
  }, [branding]);

  return (
    <main className="min-h-screen min-w-[20rem] flex items-center justify-center p-4" data-auth="page" style={{ background: 'var(--auth-bg)' }}>
      <Card style={{ background: 'var(--auth-card-bg)', borderRadius: 'var(--auth-radius, 0.5rem)', fontFamily: 'var(--auth-font, inherit)' }}>
        <div className="text-center mb-6" data-auth="header">
          {branding.logoUrl ? (
            // Optional per-mode logo "chip": padding + background only when a logo bg is configured, so
            // tenants that don't set one keep the logo flush on the card (unchanged). The background var
            // is emitted per theme by the effect above, so it can differ light vs dark.
            <span
              className={`inline-block max-w-full${(branding.lightLogoBg || branding.darkLogoBg) ? ' rounded-lg p-3' : ''}`}
              style={{ background: 'var(--auth-logo-bg)' }}
              data-auth="logo-chip"
            >
              <img src={branding.logoUrl} alt={branding.appName} className="max-h-12 max-w-full object-contain block" data-auth="logo" />
            </span>
          ) : (
            <h1 className="text-2xl font-bold tracking-tight" data-auth="app-name" style={{ color: 'var(--auth-heading)' }}>{branding.appName}</h1>
          )}
          {welcomeTitle && (
            <h2 className="mt-3 text-lg font-semibold tracking-tight" data-auth="welcome-title" style={{ color: 'var(--auth-heading)' }}>
              {welcomeTitle}
            </h2>
          )}
          {welcomeSubtitle && (
            <p className="mt-1 text-sm text-gray-500 dark:text-gray-400" data-auth="welcome-subtitle">
              {welcomeSubtitle}
            </p>
          )}
        </div>
        {/* Children when used as a wrapper, the matched route when used as a layout route. */}
        <div data-auth="content">{children ?? <Outlet />}</div>
        <LanguagePicker languages={branding.languages ?? DEFAULT_LANGUAGES} />
        <ThemeToggle />
        {branding.poweredBy && (
          <p className="mt-3 text-center text-xs text-gray-400 dark:text-gray-500" data-auth="powered-by">
            <Trans
              i18nKey="poweredBy"
              components={{
                brand: (
                  <a
                    href="https://authagonal.io"
                    target="_blank"
                    rel="noopener noreferrer"
                    className="font-medium text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200"
                  />
                ),
              }}
            />
          </p>
        )}
      </Card>
    </main>
  );
}

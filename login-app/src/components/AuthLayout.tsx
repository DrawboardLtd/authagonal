import { useEffect, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useBranding } from '../branding';
import { useDarkMode } from '../hooks/useDarkMode';
import { Card } from './ui/card';
import { cn } from '@/lib/utils';
import { Sun, Moon, Monitor } from 'lucide-react';

// Ensure i18n is initialized when AuthLayout is used (including by npm consumers)
import '../i18n';

interface AuthLayoutProps {
  children: ReactNode;
}

// Accept only hex (#rgb/#rrggbb/#rrggbbaa) or rgb()/rgba()/hsl()/hsla() forms.
function isSafeCssColor(color: string): boolean {
  return /^#(?:[0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$/i.test(color)
    || /^(?:rgb|rgba|hsl|hsla)\([0-9.,%\s/]+\)$/i.test(color);
}

const ALL_LANGUAGES: { code: string; label: string }[] = [
  { code: 'en', label: 'English' },
  { code: 'zh-Hans', label: '中文' },
  { code: 'de', label: 'Deutsch' },
  { code: 'fr', label: 'Français' },
  { code: 'es', label: 'Español' },
  { code: 'vi', label: 'Tiếng Việt' },
  { code: 'pt', label: 'Português' },
  { code: 'tlh', label: 'tlhIngan' },
];

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

export default function AuthLayout({ children }: AuthLayoutProps) {
  const branding = useBranding();
  const { i18n } = useTranslation();
  useDarkMode();

  useEffect(() => {
    if (branding.primaryColor && isSafeCssColor(branding.primaryColor)) {
      document.documentElement.style.setProperty('--brand-primary', branding.primaryColor);
    }

    if (branding.customCssUrl) {
      try {
        const parsed = new URL(branding.customCssUrl, window.location.origin);
        if (parsed.origin === window.location.origin) {
          const link = document.createElement('link');
          link.rel = 'stylesheet';
          link.href = branding.customCssUrl;
          link.id = 'branding-css';
          document.head.appendChild(link);
          return () => { link.remove(); };
        }
      } catch {
        // Invalid URL — skip injecting custom CSS.
      }
    }
  }, [branding]);

  return (
    <main className="min-h-screen flex items-center justify-center p-4" data-auth="page" style={{ background: 'var(--auth-bg)' }}>
      <Card style={{ background: 'var(--auth-card-bg)', borderRadius: 'var(--auth-radius, 0.5rem)', fontFamily: 'var(--auth-font, inherit)' }}>
        <div className="text-center mb-6" data-auth="header">
          {branding.logoUrl ? (
            <img src={branding.logoUrl} alt={branding.appName} className="max-h-12 max-w-full object-contain mx-auto" data-auth="logo" />
          ) : (
            <h1 className="text-2xl font-bold tracking-tight" data-auth="app-name" style={{ color: 'var(--auth-heading)' }}>{branding.appName}</h1>
          )}
        </div>
        <div data-auth="content">{children}</div>
        <div className="flex flex-wrap justify-center gap-2 mt-6 pt-4 border-t border-gray-200 dark:border-gray-800" data-auth="languages">
          {(branding.languages ?? ALL_LANGUAGES).map((lang) => (
            <button
              key={lang.code}
              type="button"
              className={cn(
                'inline-flex items-center min-h-[36px] bg-transparent border-none px-2.5 py-2 text-xs rounded cursor-pointer transition-colors',
                i18n.language === lang.code || i18n.language?.startsWith(lang.code)
                  ? 'text-primary font-semibold'
                  : 'text-gray-600 dark:text-gray-300 hover:text-gray-900 dark:hover:text-gray-100 hover:bg-gray-100 dark:hover:bg-gray-800'
              )}
              onClick={() => i18n.changeLanguage(lang.code)}
            >
              {lang.label}
            </button>
          ))}
        </div>
        <ThemeToggle />
      </Card>
    </main>
  );
}

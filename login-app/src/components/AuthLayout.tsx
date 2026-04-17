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
            'p-1 rounded cursor-pointer border-none bg-transparent transition-colors',
            theme === value
              ? 'text-gray-700 dark:text-gray-200'
              : 'text-gray-400 dark:text-gray-500 hover:text-gray-600 dark:hover:text-gray-300'
          )}
          title={value}
          aria-label={`${value} theme`}
        >
          <Icon className="h-3.5 w-3.5" />
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
    document.documentElement.style.setProperty('--brand-primary', branding.primaryColor);

    if (branding.customCssUrl) {
      const link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = branding.customCssUrl;
      link.id = 'branding-css';
      document.head.appendChild(link);
      return () => { link.remove(); };
    }
  }, [branding]);

  return (
    <div className="min-h-screen flex items-center justify-center p-4" data-auth="page" style={{ background: 'var(--auth-bg)' }}>
      <Card style={{ background: 'var(--auth-card-bg)', borderRadius: 'var(--auth-radius, 0.5rem)', fontFamily: 'var(--auth-font, inherit)' }}>
        <div className="text-center mb-6" data-auth="header">
          {branding.logoUrl ? (
            <img src={branding.logoUrl} alt={branding.appName} className="max-h-12 max-w-full object-contain mx-auto" data-auth="logo" />
          ) : (
            <h1 className="text-2xl font-bold tracking-tight" data-auth="app-name" style={{ color: 'var(--auth-heading)' }}>{branding.appName}</h1>
          )}
        </div>
        <div data-auth="content">{children}</div>
        <div className="flex flex-wrap justify-center gap-1 mt-6 pt-4 border-t border-gray-200 dark:border-gray-800" data-auth="languages">
          {(branding.languages ?? ALL_LANGUAGES).map((lang) => (
            <button
              key={lang.code}
              type="button"
              className={cn(
                'bg-transparent border-none px-2 py-1 text-xs rounded cursor-pointer transition-colors',
                i18n.language === lang.code || i18n.language?.startsWith(lang.code)
                  ? 'text-primary font-semibold'
                  : 'text-gray-400 dark:text-gray-500 hover:text-gray-700 dark:hover:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-800'
              )}
              onClick={() => i18n.changeLanguage(lang.code)}
            >
              {lang.label}
            </button>
          ))}
        </div>
        <ThemeToggle />
      </Card>
    </div>
  );
}

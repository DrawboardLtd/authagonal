import { useEffect, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useBranding } from '../branding';
import { Card } from './ui/card';
import { cn } from '@/lib/utils';

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

export default function AuthLayout({ children }: AuthLayoutProps) {
  const branding = useBranding();
  const { i18n } = useTranslation();

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
    <div className="min-h-screen flex items-center justify-center p-4" data-auth="page" style={{ background: 'var(--auth-bg, #f3f4f6)' }}>
      <Card style={{ background: 'var(--auth-card-bg, white)', borderRadius: 'var(--auth-radius, 0.5rem)', fontFamily: 'var(--auth-font, inherit)' }}>
        <div className="text-center mb-6" data-auth="header">
          {branding.logoUrl ? (
            <img src={branding.logoUrl} alt={branding.appName} className="max-h-12 max-w-full object-contain mx-auto" data-auth="logo" />
          ) : (
            <h1 className="text-2xl font-bold tracking-tight" data-auth="app-name" style={{ color: 'var(--auth-heading, #111827)' }}>{branding.appName}</h1>
          )}
        </div>
        <div data-auth="content">{children}</div>
        <div className="flex flex-wrap justify-center gap-1 mt-6 pt-4 border-t border-gray-200" data-auth="languages">
          {(branding.languages ?? ALL_LANGUAGES).map((lang) => (
            <button
              key={lang.code}
              type="button"
              className={cn(
                'bg-transparent border-none px-2 py-1 text-xs rounded cursor-pointer transition-colors',
                i18n.language === lang.code || i18n.language?.startsWith(lang.code)
                  ? 'text-primary font-semibold'
                  : 'text-gray-400 hover:text-gray-700 hover:bg-gray-100'
              )}
              onClick={() => i18n.changeLanguage(lang.code)}
            >
              {lang.label}
            </button>
          ))}
        </div>
      </Card>
    </div>
  );
}

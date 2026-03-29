import { useEffect, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useBranding } from '../branding';

interface AuthLayoutProps {
  children: ReactNode;
}

const LANGUAGES: { code: string; label: string }[] = [
  { code: 'en', label: 'English' },
  { code: 'zh-Hans', label: '中文' },
  { code: 'de', label: 'Deutsch' },
  { code: 'fr', label: 'Français' },
  { code: 'es', label: 'Español' },
  { code: 'vi', label: 'Tiếng Việt' },
  { code: 'pt', label: 'Português' },
];

export default function AuthLayout({ children }: AuthLayoutProps) {
  const branding = useBranding();
  const { i18n } = useTranslation();

  useEffect(() => {
    document.documentElement.style.setProperty('--color-primary', branding.primaryColor);

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
    <div className="auth-container">
      <div className="auth-card">
        <div className="auth-logo">
          {branding.logoUrl ? (
            <img src={branding.logoUrl} alt={branding.appName} className="auth-logo-img" />
          ) : (
            <h1>{branding.appName}</h1>
          )}
        </div>
        {children}
        <div className="language-picker">
          {LANGUAGES.map((lang) => (
            <button
              key={lang.code}
              type="button"
              className={`language-btn${i18n.language === lang.code || i18n.language?.startsWith(lang.code) ? ' active' : ''}`}
              onClick={() => i18n.changeLanguage(lang.code)}
            >
              {lang.label}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}

import { useEffect, type ReactNode } from 'react';
import { useBranding } from '../branding';

interface AuthLayoutProps {
  children: ReactNode;
}

export default function AuthLayout({ children }: AuthLayoutProps) {
  const branding = useBranding();

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
      </div>
    </div>
  );
}

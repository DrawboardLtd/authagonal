import { useState, useEffect, useRef, useCallback } from 'react';
import { useSearchParams, Link, useNavigate } from 'react-router-dom';
import { login, logout, ssoCheck, getProviders, getSession, ApiRequestError, useBranding, useTranslation, resolveLocalized, Button, Input, Label, Alert, CardTitle, CardFooter, Separator } from '@authagonal/login';
import type { ExternalProvider } from '@authagonal/login';

// Custom login page that adds a Terms of Service checkbox.
// Built using the base package's API client, branding hooks, and UI components —
// the form logic is the same, but the UI has Acme-specific requirements.

const API_URL = import.meta.env.VITE_API_URL || '';

function isSafeReturnUrl(url: string): boolean {
  if (!url) return false;
  try {
    const parsed = new URL(url, window.location.origin);
    return parsed.origin === window.location.origin && url.startsWith('/');
  } catch {
    return false;
  }
}

export default function AcmeLoginPage() {
  const { t, i18n } = useTranslation();
  const branding = useBranding();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const returnUrl = searchParams.get('returnUrl') || '';
  const loginHint = searchParams.get('login_hint') || '';
  const oidcError = searchParams.get('error_description') || searchParams.get('error') || '';

  const [email, setEmail] = useState(loginHint);
  const [password, setPassword] = useState('');
  const [error, setError] = useState(oidcError);
  const [loading, setLoading] = useState(false);
  const [ssoInfo, setSsoInfo] = useState<{ redirectUrl: string } | null>(null);
  const [ssoChecked, setSsoChecked] = useState(false);
  const [ssoChecking, setSsoChecking] = useState(false);
  const [providers, setProviders] = useState<ExternalProvider[]>([]);
  const [session, setSession] = useState<{ name: string; email: string } | null>(null);
  const [mfaPrompt, setMfaPrompt] = useState<{ returnUrl: string; userId: string; clientId: string } | null>(null);

  const debounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastCheckedEmailRef = useRef('');

  const performSsoCheck = useCallback(async (emailToCheck: string) => {
    if (!emailToCheck.includes('@') || emailToCheck === lastCheckedEmailRef.current) return;
    lastCheckedEmailRef.current = emailToCheck;
    setSsoChecking(true);
    setError('');
    try {
      const result = await ssoCheck(emailToCheck);
      setSsoInfo(result.ssoRequired && result.redirectUrl ? { redirectUrl: result.redirectUrl } : null);
      setSsoChecked(true);
    } catch {
      setSsoInfo(null);
      setSsoChecked(true);
    } finally {
      setSsoChecking(false);
    }
  }, []);

  useEffect(() => {
    if (returnUrl && isSafeReturnUrl(returnUrl)) return;
    getSession()
      .then((s: any) => {
        if (s.authenticated) setSession({ name: s.name, email: s.email });
      })
      .catch(() => {});
  }, [returnUrl]);

  useEffect(() => {
    getProviders()
      .then((res: any) => setProviders(res.providers ?? []))
      .catch(() => {});
  }, []);

  useEffect(() => {
    if (loginHint && loginHint.includes('@')) performSsoCheck(loginHint);
  }, [loginHint, performSsoCheck]);

  function handleEmailBlur() {
    if (debounceTimerRef.current) clearTimeout(debounceTimerRef.current);
    debounceTimerRef.current = setTimeout(() => performSsoCheck(email), 300);
  }

  function handleEmailChange(value: string) {
    setEmail(value);
    if (value !== lastCheckedEmailRef.current) {
      setSsoChecked(false);
      setSsoInfo(null);
    }
  }

  function handleProviderLogin(provider: ExternalProvider) {
    const url = new URL(`${API_URL}${provider.loginUrl}`, window.location.origin);
    if (returnUrl && isSafeReturnUrl(returnUrl)) url.searchParams.set('returnUrl', returnUrl);
    window.location.href = url.toString();
  }

  function handleSsoRedirect() {
    if (!ssoInfo) return;
    const ssoUrl = new URL(`${API_URL}${ssoInfo.redirectUrl}`, window.location.origin);
    if (returnUrl && isSafeReturnUrl(returnUrl)) ssoUrl.searchParams.set('returnUrl', returnUrl);
    window.location.href = ssoUrl.toString();
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setLoading(true);
    try {
      const result = await login(email, password, returnUrl || undefined);

      if (result.mfaRequired && result.challengeId) {
        const params = new URLSearchParams({
          challengeId: result.challengeId,
          ...(returnUrl ? { returnUrl } : {}),
          ...(result.methods ? { methods: result.methods.join(',') } : {}),
          ...(result.webAuthn ? { webAuthn: JSON.stringify(result.webAuthn) } : {}),
        });
        navigate(`/mfa-challenge?${params.toString()}`);
        return;
      }

      if (result.mfaSetupRequired) {
        const params = new URLSearchParams({
          ...(returnUrl ? { returnUrl } : {}),
          ...(result.setupToken ? { setupToken: result.setupToken } : {}),
        });
        navigate(`/mfa-setup?${params.toString()}`);
        return;
      }

      if (result.mfaAvailable && result.userId) {
        const dismissKey = `mfa-prompt-dismissed:${result.userId}:${result.clientId || 'default'}`;
        if (!localStorage.getItem(dismissKey)) {
          setMfaPrompt({ returnUrl, userId: result.userId, clientId: result.clientId || 'default' });
          return;
        }
      }

      window.location.href = returnUrl && isSafeReturnUrl(returnUrl) ? returnUrl : '/';
    } catch (err) {
      if (err instanceof ApiRequestError) {
        switch (err.error) {
          case 'invalid_credentials': setError('Invalid email or password'); break;
          case 'locked_out': setError(`Account locked. Try again in ${err.retryAfter ?? '?'} seconds`); break;
          case 'email_not_confirmed': setError('Please check your email to verify your account'); break;
          case 'sso_required':
            if (err.redirectUrl) {
              const ssoUrl = new URL(`${API_URL}${err.redirectUrl}`, window.location.origin);
              if (returnUrl && isSafeReturnUrl(returnUrl)) ssoUrl.searchParams.set('returnUrl', returnUrl);
              window.location.href = ssoUrl.toString();
              return;
            }
            setError('Single sign-on is required for this account');
            break;
          default: setError(err.message || 'An unexpected error occurred');
        }
      } else {
        setError('An unexpected error occurred. Please try again.');
      }
    } finally {
      setLoading(false);
    }
  }

  const forgotPasswordLink = returnUrl && isSafeReturnUrl(returnUrl)
    ? `/forgot-password?returnUrl=${encodeURIComponent(returnUrl)}`
    : '/forgot-password';

  const showPasswordField = ssoChecked && !ssoInfo;

  if (mfaPrompt) {
    const skipMfa = () => {
      localStorage.setItem(`mfa-prompt-dismissed:${mfaPrompt.userId}:${mfaPrompt.clientId}`, '1');
      const dest = mfaPrompt.returnUrl && isSafeReturnUrl(mfaPrompt.returnUrl) ? mfaPrompt.returnUrl : '/';
      window.location.href = dest;
    };

    return (
      <div>
        <CardTitle>{t('mfaPromptTitle')}</CardTitle>
        <p className="text-center text-gray-500 mb-6">
          {t('mfaPromptMessage')}
        </p>
        <Button
          type="button"
          className="mb-3"
          onClick={() => navigate(`/mfa-setup?returnUrl=${encodeURIComponent(mfaPrompt.returnUrl || '/')}`)}
        >
          {t('mfaPromptSetup')}
        </Button>
        <Button
          type="button"
          variant="secondary"
          onClick={skipMfa}
        >
          {t('mfaPromptSkip')}
        </Button>
      </div>
    );
  }

  if (session) {
    return (
      <div>
        <CardTitle>{t('signedInAs', { name: session.name || session.email })}</CardTitle>
        <p className="text-center text-gray-500">{t('signedInMessage')}</p>
        <CardFooter>
          <Button
            type="button"
            variant="secondary"
            onClick={() => {
              logout().then(() => setSession(null)).catch(() => setSession(null));
            }}
          >
            {t('signOut')}
          </Button>
        </CardFooter>
      </div>
    );
  }

  return (
    <div>
      <CardTitle>{resolveLocalized(branding.welcomeTitle, i18n.language) ?? t('welcomeTitle', { appName: branding.appName })}</CardTitle>
      <p className="text-center text-sm text-gray-500 mb-4">{resolveLocalized(branding.welcomeSubtitle, i18n.language) ?? t('welcomeSubtitle')}</p>

      {providers.length > 0 && !showPasswordField && (
        <div className="mb-2">
          {providers.map((p) => (
            <Button
              key={p.connectionId}
              type="button"
              variant="secondary"
              className="mb-2"
              onClick={() => handleProviderLogin(p)}
            >
              {p.connectionId === 'google' && (
                <svg className="shrink-0" viewBox="0 0 24 24" width="20" height="20">
                  <path d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92a5.06 5.06 0 0 1-2.2 3.32v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.1z" fill="#4285F4"/>
                  <path d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" fill="#34A853"/>
                  <path d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z" fill="#FBBC05"/>
                  <path d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z" fill="#EA4335"/>
                </svg>
              )}
              {t('continueWith', { provider: p.name })}
            </Button>
          ))}
          <Separator label={t('or')} />
        </div>
      )}

      {providers.length > 0 && showPasswordField && (
        <div className="flex items-center gap-3 mb-4 text-gray-400 text-[13px]">
          <div className="flex-1 h-px bg-gray-200" />
          <button
            type="button"
            onClick={() => { setSsoChecked(false); setSsoInfo(null); lastCheckedEmailRef.current = ''; }}
            className="bg-transparent border-none cursor-pointer text-[13px] text-primary hover:underline"
          >
            {t('orSignInWith', { provider: providers.map(p => p.name).join(', ') })}
          </button>
          <div className="flex-1 h-px bg-gray-200" />
        </div>
      )}

      {error && <Alert variant="error">{error}</Alert>}

      <form onSubmit={handleSubmit}>
        <div className="mb-4">
          <Label htmlFor="email">{t('email')}</Label>
          <Input
            id="email"
            type="email"
            value={email}
            onChange={(e) => handleEmailChange(e.target.value)}
            onBlur={handleEmailBlur}
            placeholder={t('emailPlaceholder')}
            autoComplete="email"
            autoFocus={!loginHint}
            maxLength={256}
            required
          />
        </div>

        {!ssoChecked && !ssoChecking && (
          <Button
            type="button"
            onClick={() => performSsoCheck(email)}
            disabled={!email.includes('@')}
          >
            {t('continue')}
          </Button>
        )}

        {ssoChecking && <p className="text-sm text-gray-500 mb-4">{t('ssoChecking')}</p>}

        {ssoInfo && (
          <div className="mb-4">
            <p className="text-sm text-gray-500 mb-3">{t('ssoNotice')}</p>
            <Button variant="secondary" type="button" onClick={handleSsoRedirect}>
              {t('continueWithSso')}
            </Button>
          </div>
        )}

        {showPasswordField && (
          <>
            <div className="mb-4">
              <Label htmlFor="password">{t('password')}</Label>
              <Input
                id="password"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                placeholder={t('passwordPlaceholder')}
                autoComplete="current-password"
                autoFocus
                maxLength={256}
                required
              />
            </div>

            <Button type="submit" loading={loading}>
              {loading ? t('signingIn') : t('signIn')}
            </Button>

            {branding.showForgotPassword && (
              <CardFooter>
                <Link to={forgotPasswordLink} className="text-sm font-medium text-primary hover:underline no-underline">{t('forgotPassword')}</Link>
              </CardFooter>
            )}
          </>
        )}
      </form>

      <CardFooter className="mt-4">
        <span className="text-sm text-gray-500">
          {t('noAccount')}{' '}
          <Link to={returnUrl ? `/register?returnUrl=${encodeURIComponent(returnUrl)}` : '/register'} className="text-sm font-medium text-primary hover:underline no-underline">
            {t('createAccount')}
          </Link>
        </span>
      </CardFooter>
    </div>
  );
}

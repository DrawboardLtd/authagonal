import { useState, useEffect, useRef, useCallback } from 'react';
import { useSearchParams, Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { login, logout, ssoCheck, getProviders, getSession, getApps, passkeyLoginBegin, passkeyLoginComplete, ApiRequestError } from '../api';
import { toRequestOptions, serializeAssertion } from '../webauthn';
import { Turnstile } from '../components/Turnstile';
import { useBranding } from '../branding';
import type { ExternalProvider } from '../types';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { Separator } from '@/components/ui/separator';
import { LogIn } from 'lucide-react';
import { CardTitle, CardFooter } from '@/components/ui/card';
import { resolveRedirect } from '@/lib/returnUrl';

const API_URL = import.meta.env.VITE_API_URL || '';

function isSafeReturnUrl(url: string): boolean {
  if (!url) return false;
  // Only allow relative paths (starting with /) that don't escape to another host
  try {
    const parsed = new URL(url, window.location.origin);
    return parsed.origin === window.location.origin && url.startsWith('/');
  } catch {
    return false;
  }
}

export default function LoginPage() {
  const { t } = useTranslation();
  const branding = useBranding();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const returnUrl = searchParams.get('returnUrl') || '';
  // Stamped by the confirm-email redirect: the client whose flow triggered the verification email.
  // With no authorize returnUrl, a successful sign-in continues to that app instead of the account
  // page (resolved via the now-authenticated /apps call; falls back to the tenant default, then /).
  const continueClient = searchParams.get('continue_client') || '';
  async function continueDestination(): Promise<string> {
    if (!continueClient) return '/';
    try {
      const apps = await getApps();
      const match = apps.find((a) => a.clientId === continueClient) ?? apps.find((a) => a.isDefault);
      return match?.homeUri || '/';
    } catch {
      return '/';
    }
  }
  const loginHint = searchParams.get('login_hint') || '';
  const oidcError = searchParams.get('error_description') || searchParams.get('error') || '';
  const messageParam = searchParams.get('message') || '';

  const [email, setEmail] = useState(loginHint);
  const [password, setPassword] = useState('');
  const [error, setError] = useState(oidcError);
  const emailConfirmedParam = searchParams.get('email_confirmed') === '1';
  const [successMessage] = useState(() =>
    emailConfirmedParam ? t('emailVerified')
    : messageParam === 'account_created' ? t('accountCreated')
    : messageParam === 'registration_success' ? t('registrationSuccess') : ''
  );
  const [loading, setLoading] = useState(false);
  const [ssoInfo, setSsoInfo] = useState<{ redirectUrl: string } | null>(null);
  const [ssoChecked, setSsoChecked] = useState(false);
  const [ssoChecking, setSsoChecking] = useState(false);
  const [providers, setProviders] = useState<ExternalProvider[]>([]);
  const [turnstileSiteKey, setTurnstileSiteKey] = useState<string | undefined>(undefined);
  const [turnstileToken, setTurnstileToken] = useState<string | null>(null);
  const [turnstileKey, setTurnstileKey] = useState(0); // bump to re-mount the widget for a fresh challenge
  const [session, setSession] = useState<{ name: string; email: string } | null>(null);
  const [sessionApp, setSessionApp] = useState<{ clientName: string; homeUri: string } | null>(null);
  const [mfaPrompt, setMfaPrompt] = useState<{ returnUrl: string; userId: string; clientId: string } | null>(null);

  const debounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastCheckedEmailRef = useRef('');

  const performSsoCheck = useCallback(async (emailToCheck: string) => {
    if (!emailToCheck.includes('@') || emailToCheck === lastCheckedEmailRef.current) {
      return;
    }

    lastCheckedEmailRef.current = emailToCheck;
    setSsoChecking(true);
    setError('');

    try {
      const result = await ssoCheck(emailToCheck);
      if (result.ssoRequired && result.redirectUrl) {
        setSsoInfo({ redirectUrl: result.redirectUrl });
      } else {
        setSsoInfo(null);
      }
      setSsoChecked(true);
    } catch {
      // If SSO check fails, allow normal login
      setSsoInfo(null);
      setSsoChecked(true);
    } finally {
      setSsoChecking(false);
    }
  }, []);

  // Check for existing session (e.g. after OIDC callback with no returnUrl)
  useEffect(() => {
    if (returnUrl && isSafeReturnUrl(returnUrl)) return; // OAuth flow — don't check session
    getSession()
      .then((s) => {
        if (s.authenticated) {
          setSession({ name: s.name, email: s.email });
          // The signed-in card must never be a dead end: resolve where "into the app" leads
          // (the flow's default application) and offer it as the primary action.
          getApps()
            .then((apps) => {
              const app = apps.find((a) => a.isDefault) ?? apps[0];
              if (app?.homeUri) setSessionApp({ clientName: app.clientName, homeUri: app.homeUri });
            })
            .catch(() => {});
        }
      })
      .catch(() => {});
  }, [returnUrl]);

  // Fetch available external providers
  useEffect(() => {
    getProviders()
      .then((res) => {
        setProviders(res.providers ?? []);
        setTurnstileSiteKey(res.turnstileSiteKey);
      })
      .catch(() => {});
  }, []);

  // Auto-trigger SSO check if login_hint is provided
  useEffect(() => {
    if (loginHint && loginHint.includes('@')) {
      performSsoCheck(loginHint);
    }
  }, [loginHint, performSsoCheck]);

  function handleEmailBlur() {
    if (debounceTimerRef.current) {
      clearTimeout(debounceTimerRef.current);
    }
    debounceTimerRef.current = setTimeout(() => {
      performSsoCheck(email);
    }, 300);
  }

  function handleEmailChange(value: string) {
    setEmail(value);
    // Reset SSO state when email changes
    if (value !== lastCheckedEmailRef.current) {
      setSsoChecked(false);
      setSsoInfo(null);
    }
  }

  function handleProviderLogin(provider: ExternalProvider) {
    const url = new URL(`${API_URL}${provider.loginUrl}`, window.location.origin);
    if (returnUrl && isSafeReturnUrl(returnUrl)) {
      url.searchParams.set('returnUrl', returnUrl);
    }
    window.location.href = url.toString();
  }

  function handleSsoRedirect() {
    if (ssoInfo) {
      const ssoUrl = new URL(`${API_URL}${ssoInfo.redirectUrl}`, window.location.origin);
      if (returnUrl && isSafeReturnUrl(returnUrl)) {
        ssoUrl.searchParams.set('returnUrl', returnUrl);
      }
      if (email) {
        ssoUrl.searchParams.set('loginHint', email);
      }
      window.location.href = ssoUrl.toString();
    }
  }

  // Passkey autofill (conditional mediation): if the browser supports it and a passkey exists for this
  // site, it's offered in the email field's autofill dropdown. Selecting it logs in passwordless. Fully
  // best-effort — no passkey / user ignores it / aborted / SSO-routed all fall back silently to password.
  useEffect(() => {
    const ac = new AbortController();
    (async () => {
      try {
        const pkc = (window as unknown as { PublicKeyCredential?: { isConditionalMediationAvailable?: () => Promise<boolean> } }).PublicKeyCredential;
        if (!pkc?.isConditionalMediationAvailable || !(await pkc.isConditionalMediationAvailable())) return;

        const { challengeId, options } = await passkeyLoginBegin();
        const getOptions: CredentialRequestOptions = { publicKey: toRequestOptions(options), signal: ac.signal };
        (getOptions as { mediation?: string }).mediation = 'conditional';
        const credential = await navigator.credentials.get(getOptions) as PublicKeyCredential | null;
        if (!credential || ac.signal.aborted) return;

        await passkeyLoginComplete(challengeId, serializeAssertion(credential));
        window.location.href = await resolveRedirect(returnUrl, continueDestination);
      } catch {
        // No passkey, user ignored the autofill, aborted, or SSO-routed — normal password login continues.
      }
    })();
    return () => ac.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [returnUrl]);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      const result = await login(email, password, returnUrl || undefined, turnstileToken || undefined);

      if (result.mfaRequired && result.challengeId) {
        // Redirect to MFA challenge page
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
        // Redirect to MFA setup page with setup token
        const params = new URLSearchParams({
          ...(returnUrl ? { returnUrl } : {}),
          ...(result.setupToken ? { setupToken: result.setupToken } : {}),
        });
        navigate(`/mfa-setup?${params.toString()}`);
        return;
      }

      // If MFA is available but not enrolled, offer to set it up (once per client)
      if (result.mfaAvailable && result.userId) {
        const dismissKey = `mfa-prompt-dismissed:${result.userId}:${result.clientId || 'default'}`;
        if (!localStorage.getItem(dismissKey)) {
          setMfaPrompt({ returnUrl, userId: result.userId, clientId: result.clientId || 'default' });
          return;
        }
      }

      // On success, redirect to returnUrl — same-origin paths as before; ABSOLUTE URLs only when
      // their origin matches a registered client's home URI (product apps returning the user to
      // their own pages, e.g. invite landings). Else the continue destination.
      window.location.href = await resolveRedirect(returnUrl, continueDestination);
    } catch (err) {
      if (err instanceof ApiRequestError) {
        switch (err.error) {
          case 'invalid_credentials':
            setError(t('errorInvalidCredentials'));
            break;
          case 'locked_out':
            setError(t('errorLockedOut', { seconds: err.retryAfter ?? '?' }));
            break;
          case 'email_not_confirmed':
            setError(t('errorEmailNotConfirmed'));
            break;
          case 'sso_required':
            if (err.redirectUrl) {
              const ssoUrl = new URL(`${API_URL}${err.redirectUrl}`, window.location.origin);
              if (returnUrl && isSafeReturnUrl(returnUrl)) {
                ssoUrl.searchParams.set('returnUrl', returnUrl);
              }
              window.location.href = ssoUrl.toString();
              return;
            }
            setError(t('errorSsoRequired'));
            break;
          case 'email_required':
            setError(t('errorEmailRequired'));
            break;
          case 'password_required':
            setError(t('errorPasswordRequired'));
            break;
          case 'captcha_failed':
            setError(t('captchaFailed'));
            break;
          default:
            setError(err.message || t('errorUnexpected'));
        }
      } else {
        setError(t('errorUnexpected'));
      }
      // Turnstile tokens are single-use — reset so a retry gets a fresh challenge.
      if (turnstileSiteKey) {
        setTurnstileToken(null);
        setTurnstileKey((k) => k + 1);
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
      const dest = mfaPrompt.returnUrl && isSafeReturnUrl(mfaPrompt.returnUrl)
        ? mfaPrompt.returnUrl
        : '/';
      window.location.href = dest;
    };

    return (
      <div>
        <CardTitle>{t('mfaPromptTitle')}</CardTitle>
        <p className="text-center text-gray-500 dark:text-gray-400 mb-6">
          {t('mfaPromptMessage')}
        </p>
        <Button
          className="mb-3"
          onClick={() => navigate(`/mfa-setup?returnUrl=${encodeURIComponent(mfaPrompt.returnUrl || '/')}`)}
        >
          {t('mfaPromptSetup')}
        </Button>
        <Button variant="secondary" onClick={skipMfa}>
          {t('mfaPromptSkip')}
        </Button>
      </div>
    );
  }

  if (session) {
    return (
      <div>
        <CardTitle>{t('signedInAs', { name: session.name || session.email })}</CardTitle>
        <p className="text-center text-gray-500 dark:text-gray-400">{t('signedInMessage')}</p>
        <CardFooter className="flex flex-col gap-2">
          {sessionApp && (
            <a href={sessionApp.homeUri} className="block no-underline" data-testid="continue-to-app">
              <Button className="w-full">{t('continueToApp', { app: sessionApp.clientName })}</Button>
            </a>
          )}
          <Link to="/account" className="block">
            <Button className="w-full">{t('manageAccount', 'Manage account')}</Button>
          </Link>
          <Button
            variant="secondary"
            onClick={() => {
              logout().then(() => {
                setSession(null);
              }).catch(() => {
                setSession(null);
              });
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
      <CardTitle>{t('signIn')}</CardTitle>

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
              {p.iconUrl
                ? <img src={p.iconUrl} alt="" width={20} height={20} className="shrink-0 rounded-sm" />
                : <LogIn className="h-5 w-5 shrink-0" />}
              {t('continueWith', { provider: p.name })}
            </Button>
          ))}
          <Separator label={t('or')} />
        </div>
      )}

      {providers.length > 0 && showPasswordField && (
        <div className="flex items-center gap-3 mb-4 text-gray-600 dark:text-gray-300 text-[13px]">
          <div className="flex-1 h-px bg-gray-200 dark:bg-gray-800" />
          <button
            type="button"
            onClick={() => { setSsoChecked(false); setSsoInfo(null); lastCheckedEmailRef.current = ''; }}
            className="bg-transparent border-none cursor-pointer text-[13px] text-primary hover:underline"
          >
            {t('orSignInWith', { provider: providers.map(p => p.name).join(', ') })}
          </button>
          <div className="flex-1 h-px bg-gray-200 dark:bg-gray-800" />
        </div>
      )}

      {successMessage && <Alert variant="success">{successMessage}</Alert>}
      {error && <Alert variant="error">{error}</Alert>}

      <form onSubmit={handleSubmit} data-auth="login-form">
        <div className="mb-4" data-auth="email-field">
          <Label htmlFor="email">{t('email')}</Label>
          <Input
            id="email"
            type="email"
            value={email}
            onChange={(e) => handleEmailChange(e.target.value)}
            onBlur={handleEmailBlur}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !ssoChecked && !ssoChecking && email.includes('@')) {
                e.preventDefault();
                performSsoCheck(email);
              }
            }}
            placeholder={t('emailPlaceholder')}
            autoComplete="username webauthn"
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

        {ssoChecking && (
          <p className="text-sm text-gray-500 dark:text-gray-400 mb-4">{t('ssoChecking')}</p>
        )}

        {ssoInfo && (
          <div className="mb-4">
            <p className="text-sm text-gray-500 dark:text-gray-400 mb-3">{t('ssoNotice')}</p>
            <Button variant="secondary" type="button" onClick={handleSsoRedirect}>
              {t('continueWithSso')}
            </Button>
          </div>
        )}

        {showPasswordField && (
          <>
            <div className="mb-4" data-auth="password-field">
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

            {turnstileSiteKey && (
              <div className="mb-4">
                <Turnstile key={turnstileKey} siteKey={turnstileSiteKey} onToken={setTurnstileToken} />
              </div>
            )}

            <Button type="submit" loading={loading} disabled={!!turnstileSiteKey && !turnstileToken} data-auth="submit-button">
              {loading ? t('signingIn') : t('signIn')}
            </Button>

            {branding.showForgotPassword && (
              <CardFooter>
                <Link to={forgotPasswordLink} className="text-sm font-medium text-primary hover:underline no-underline">
                  {t('forgotPassword')}
                </Link>
              </CardFooter>
            )}
          </>
        )}
      </form>

      {branding.showRegistration && (
        <CardFooter className="mt-4">
          <span className="text-sm text-gray-500 dark:text-gray-400">
            {t('noAccount')}{' '}
            <Link to={returnUrl ? `/register?returnUrl=${encodeURIComponent(returnUrl)}` : '/register'} className="text-sm font-medium text-primary hover:underline no-underline">
              {t('createAccount')}
            </Link>
          </span>
        </CardFooter>
      )}
    </div>
  );
}

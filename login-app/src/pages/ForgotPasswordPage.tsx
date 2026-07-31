import { useState, useEffect } from 'react';
import { useSearchParams, Link } from 'react-router';
import { useTranslation } from 'react-i18next';
import { forgotPassword, getProviders, ApiRequestError } from '../api';
import { Turnstile } from '../components/Turnstile';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { CardTitle, CardDescription, CardFooter } from '@/components/ui/card';

export default function ForgotPasswordPage() {
  const { t } = useTranslation();
  const [searchParams] = useSearchParams();
  const returnUrl = searchParams.get('returnUrl') || '';

  const [email, setEmail] = useState('');
  const [loading, setLoading] = useState(false);
  const [submitted, setSubmitted] = useState(false);
  const [error, setError] = useState('');
  const [turnstileSiteKey, setTurnstileSiteKey] = useState<string | undefined>(undefined);
  const [turnstileToken, setTurnstileToken] = useState<string | null>(null);
  const [turnstileKey, setTurnstileKey] = useState(0); // bump to re-mount the widget for a fresh challenge

  // Surface the Turnstile site key (opt-in; empty when not configured for the tenant).
  useEffect(() => {
    getProviders()
      .then((res) => setTurnstileSiteKey(res.turnstileSiteKey))
      .catch(() => {});
  }, []);

  // basename is /login, so the login page is app-relative "/" — NOT "/login", which resolves to
  // /login/login, matches no route, and falls through to the catch-all Navigate that DROPS returnUrl.
  const loginLink = returnUrl
    ? `/?returnUrl=${encodeURIComponent(returnUrl)}`
    : '/';

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      await forgotPassword(email, turnstileToken || undefined, returnUrl || undefined);
      setSubmitted(true);
    } catch (err) {
      // The API always returns 200 for anti-enumeration; the only expected error is a
      // failed captcha (handled explicitly), otherwise a generic message.
      if (err instanceof ApiRequestError && err.error === 'captcha_failed') {
        setError(t('captchaFailed'));
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

  if (submitted) {
    return (
      <div>
        <CardTitle>{t('checkYourEmail')}</CardTitle>
        <Alert variant="success">{t('resetEmailSent')}</Alert>
        <CardFooter>
          <Link to={loginLink} className="text-sm font-medium text-primary hover:underline no-underline">
            {t('backToSignIn')}
          </Link>
        </CardFooter>
      </div>
    );
  }

  return (
    <div>
      <CardTitle>{t('resetYourPassword')}</CardTitle>
      <CardDescription className="mb-5">{t('resetSubtitle')}</CardDescription>

      {error && <Alert variant="error">{error}</Alert>}

      <form onSubmit={handleSubmit}>
        <div className="mb-4">
          <Label htmlFor="email">{t('email')}</Label>
          <Input
            id="email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder={t('emailPlaceholder')}
            autoComplete="email"
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

        <Button type="submit" loading={loading} disabled={!!turnstileSiteKey && !turnstileToken}>
          {loading ? t('sending') : t('sendResetLink')}
        </Button>

        <CardFooter>
          <Link to={loginLink} className="text-sm font-medium text-primary hover:underline no-underline">
            {t('backToSignIn')}
          </Link>
        </CardFooter>
      </form>
    </div>
  );
}

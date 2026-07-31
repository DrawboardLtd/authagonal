import { useState, useEffect } from 'react';
import { useSearchParams, Link, useNavigate } from 'react-router';
import { useTranslation } from 'react-i18next';
import { register, getPasswordPolicy, getProviders, ApiRequestError } from '../api';
import type { PasswordPolicyRule } from '../types';
import { localizePasswordRules, evaluatePasswordRules } from '../lib/passwordRules';
import { Check, X } from 'lucide-react';
import { Turnstile } from '../components/Turnstile';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { CardTitle, CardFooter } from '@/components/ui/card';

export default function RegisterPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const returnUrl = searchParams.get('returnUrl') || '';

  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [policyRules, setPolicyRules] = useState<PasswordPolicyRule[]>([]);
  const [policyLoaded, setPolicyLoaded] = useState(false);
  const [turnstileSiteKey, setTurnstileSiteKey] = useState<string | undefined>(undefined);
  const [turnstileToken, setTurnstileToken] = useState<string | null>(null);
  const [turnstileKey, setTurnstileKey] = useState(0);

  // Turnstile site key is surfaced on /providers; absent = Turnstile disabled.
  useEffect(() => {
    getProviders()
      .then((res) => setTurnstileSiteKey(res.turnstileSiteKey))
      .catch(() => {});
  }, []);

  function loadPolicy() {
    if (policyLoaded) return;
    getPasswordPolicy()
      .then((res) => setPolicyRules(res.rules))
      .catch(() => {})
      .finally(() => setPolicyLoaded(true));
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      const result = await register(email, password, firstName || undefined, lastName || undefined, turnstileToken || undefined, returnUrl || undefined);

      // Redirect to login. Pre-verified accounts (invite redemptions, auto-confirmed domains)
      // skip the check-your-email notice — they can sign straight in.
      const params = new URLSearchParams();
      if (returnUrl) params.set('returnUrl', returnUrl);
      params.set('login_hint', email);
      if (!result.emailVerified) params.set('message', 'registration_success');
      navigate(`/?${params.toString()}`);
    } catch (err) {
      if (err instanceof ApiRequestError) {
        switch (err.error) {
          case 'email_already_registered':
            setError(t('errorEmailAlreadyRegistered'));
            break;
          case 'weak_password':
            setError(err.message || t('errorWeakPassword'));
            break;
          case 'email_and_password_required':
            setError(t('errorEmailAndPasswordRequired'));
            break;
          case 'captcha_failed':
            setError(t('captchaFailed'));
            break;
          default:
            setError(err.message || t('errorRegistrationFailed'));
        }
      } else {
        setError(t('errorRegistrationFailed'));
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

  // basename is /login, so the login page is app-relative "/" — NOT "/login", which resolves to
  // /login/login, matches no route, and falls through to the catch-all Navigate that DROPS returnUrl.
  const loginLink = returnUrl
    ? `/?returnUrl=${encodeURIComponent(returnUrl)}`
    : '/';

  return (
    <div>
      <CardTitle>{t('registerTitle')}</CardTitle>

      {error && <Alert variant="error">{error}</Alert>}

      <form onSubmit={handleSubmit}>
        <div className="flex gap-3">
          <div className="mb-4 flex-1">
            <Label htmlFor="firstName">{t('firstName')}</Label>
            <Input
              id="firstName"
              type="text"
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              placeholder={t('firstNamePlaceholder')}
              autoComplete="given-name"
              maxLength={100}
            />
          </div>
          <div className="mb-4 flex-1">
            <Label htmlFor="lastName">{t('lastName')}</Label>
            <Input
              id="lastName"
              type="text"
              value={lastName}
              onChange={(e) => setLastName(e.target.value)}
              placeholder={t('lastNamePlaceholder')}
              autoComplete="family-name"
              maxLength={100}
            />
          </div>
        </div>

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

        <div className="mb-4">
          <Label htmlFor="password">{t('password')}</Label>
          <Input
            id="password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            onFocus={loadPolicy}
            placeholder={t('passwordPlaceholder')}
            autoComplete="new-password"
            maxLength={256}
            required
          />
        </div>

        {(() => {
          if (policyRules.length === 0) return null;
          // Live checklist (same look as the reset page): each rule flips as the user types, and
          // the whole list disappears once every rule is satisfied — it's guidance, not furniture.
          const requirements = evaluatePasswordRules(password, localizePasswordRules(t, policyRules));
          if (requirements.every((r) => r.met)) return null;
          return (
            <ul className="list-none mb-4 p-3 bg-gray-50 dark:bg-gray-800/60 rounded-md">
              {requirements.map((req) => (
                <li key={req.rule} className={`text-[13px] py-0.5 flex items-center gap-1.5 ${req.met ? 'text-green-800 dark:text-green-400' : 'text-gray-500 dark:text-gray-400'}`}>
                  {req.met ? <Check className="h-3.5 w-3.5 shrink-0" /> : <X className="h-3.5 w-3.5 shrink-0" />}
                  {req.label}
                </li>
              ))}
            </ul>
          );
        })()}

        {turnstileSiteKey && (
          <div className="mb-4">
            <Turnstile key={turnstileKey} siteKey={turnstileSiteKey} onToken={setTurnstileToken} />
          </div>
        )}

        <Button type="submit" loading={loading} disabled={!!turnstileSiteKey && !turnstileToken}>
          {loading ? t('registering') : t('registerButton')}
        </Button>

        <CardFooter>
          <span className="text-sm text-gray-500 dark:text-gray-400">
            {t('alreadyHaveAccount')}{' '}
            <Link to={loginLink} className="text-sm font-medium text-primary hover:underline no-underline">{t('signIn')}</Link>
          </span>
        </CardFooter>
      </form>
    </div>
  );
}

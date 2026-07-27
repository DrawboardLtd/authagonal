import { useState, useEffect } from 'react';
import { useSearchParams, Link } from 'react-router';
import { useTranslation } from 'react-i18next';
import { resetPassword, getProviders, ApiRequestError } from '../api';
import type { PasswordPolicyRule } from '../types';
import { localizePasswordRules, evaluatePasswordRules } from '../lib/passwordRules';
import { Turnstile } from '../components/Turnstile';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { CardTitle, CardFooter } from '@/components/ui/card';
import { Check, X } from 'lucide-react';

const API_URL = import.meta.env.VITE_API_URL || '';

const defaultRules: PasswordPolicyRule[] = [
  { rule: 'minLength', value: 8, label: 'At least 8 characters' },
  { rule: 'uppercase', value: null, label: 'Uppercase letter' },
  { rule: 'lowercase', value: null, label: 'Lowercase letter' },
  { rule: 'digit', value: null, label: 'Number' },
  { rule: 'specialChar', value: null, label: 'Special character' },
];

export default function ResetPasswordPage() {
  const { t } = useTranslation();
  const [searchParams] = useSearchParams();
  const token = searchParams.get('p') || '';

  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState(false);
  // "Continue to {app}" target from the reset response: the flow's originating client, else the
  // tenant's default application; null keeps the plain sign-in link.
  const [appLink, setAppLink] = useState<{ clientName: string; homeUri: string } | null>(null);
  const [validationError, setValidationError] = useState('');
  const [rules, setRules] = useState<PasswordPolicyRule[]>(defaultRules);
  const [turnstileSiteKey, setTurnstileSiteKey] = useState<string | undefined>(undefined);
  const [turnstileToken, setTurnstileToken] = useState<string | null>(null);
  const [turnstileKey, setTurnstileKey] = useState(0); // bump to re-mount the widget for a fresh challenge

  useEffect(() => {
    fetch(`${API_URL}/api/auth/password-policy`)
      .then((r) => r.ok ? r.json() : null)
      .then((data) => { if (data?.rules) setRules(data.rules); })
      .catch(() => { /* use defaults */ });
  }, []);

  // Surface the Turnstile site key (opt-in; empty when not configured for the tenant).
  useEffect(() => {
    getProviders()
      .then((res) => setTurnstileSiteKey(res.turnstileSiteKey))
      .catch(() => {});
  }, []);

  const requirements = evaluatePasswordRules(newPassword, localizePasswordRules(t, rules));
  const allRequirementsMet = requirements.every((r) => r.met);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setValidationError('');

    if (!allRequirementsMet) {
      setValidationError(t('passwordNotMeetRequirements'));
      return;
    }

    if (newPassword !== confirmPassword) {
      setValidationError(t('passwordsDoNotMatch'));
      return;
    }

    setLoading(true);

    try {
      const result = await resetPassword(token, newPassword, turnstileToken || undefined);
      if (result.appLink?.homeUri) setAppLink({ clientName: result.appLink.clientName, homeUri: result.appLink.homeUri });
      setSuccess(true);
    } catch (err) {
      if (err instanceof ApiRequestError) {
        switch (err.error) {
          case 'weak_password':
            setError(err.message || t('passwordWeakError'));
            break;
          case 'invalid_token':
          case 'token_expired':
            setError(t('invalidOrExpiredLink'));
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

  if (success) {
    return (
      <div>
        <CardTitle>{t('passwordResetSuccess')}</CardTitle>
        <Alert variant="success">{t('passwordResetSuccessMessage')}</Alert>
        {appLink && (
          <a href={appLink.homeUri} data-testid="continue-to-app" className="mt-4 block no-underline">
            <Button className="w-full">{t('continueToApp', { app: appLink.clientName })}</Button>
          </a>
        )}
        <CardFooter>
          <Link to="/" className="text-sm font-medium text-primary hover:underline no-underline">
            {t('signIn')}
          </Link>
        </CardFooter>
      </div>
    );
  }

  if (!token) {
    return (
      <div>
        <CardTitle>{t('invalidLink')}</CardTitle>
        <Alert variant="error">{t('invalidOrExpiredLink')}</Alert>
        <CardFooter>
          <Link to="/forgot-password" className="text-sm font-medium text-primary hover:underline no-underline">
            {t('requestNewResetLink')}
          </Link>
        </CardFooter>
      </div>
    );
  }

  return (
    <div>
      <CardTitle>{t('setNewPassword')}</CardTitle>

      {error && <Alert variant="error">{error}</Alert>}
      {validationError && <Alert variant="error">{validationError}</Alert>}

      <form onSubmit={handleSubmit}>
        <div className="mb-4">
          <Label htmlFor="newPassword">{t('newPassword')}</Label>
          <Input
            id="newPassword"
            type="password"
            value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)}
            placeholder={t('newPasswordPlaceholder')}
            autoComplete="new-password"
            autoFocus
            maxLength={256}
            required
          />
        </div>

        {newPassword.length > 0 && (
          <ul className="list-none mb-4 p-3 bg-gray-50 dark:bg-gray-800/60 rounded-md">
            {requirements.map((req) => (
              <li key={req.label} className={`text-[13px] py-0.5 flex items-center gap-1.5 ${req.met ? 'text-green-800 dark:text-green-400' : 'text-red-800 dark:text-red-400'}`}>
                {req.met ? <Check className="h-3.5 w-3.5 shrink-0" /> : <X className="h-3.5 w-3.5 shrink-0" />}
                {req.label}
              </li>
            ))}
          </ul>
        )}

        <div className="mb-4">
          <Label htmlFor="confirmPassword">{t('confirmPassword')}</Label>
          <Input
            id="confirmPassword"
            type="password"
            value={confirmPassword}
            onChange={(e) => setConfirmPassword(e.target.value)}
            placeholder={t('confirmPasswordPlaceholder')}
            autoComplete="new-password"
            maxLength={256}
            required
          />
        </div>

        {turnstileSiteKey && (
          <div className="mb-4">
            <Turnstile key={turnstileKey} siteKey={turnstileSiteKey} onToken={setTurnstileToken} />
          </div>
        )}

        <Button type="submit" loading={loading} disabled={!allRequirementsMet || (!!turnstileSiteKey && !turnstileToken)}>
          {loading ? t('resetting') : t('resetPassword')}
        </Button>

        <CardFooter>
          <Link to="/" className="text-sm font-medium text-primary hover:underline no-underline">
            {t('backToSignIn')}
          </Link>
        </CardFooter>
      </form>
    </div>
  );
}

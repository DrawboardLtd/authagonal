import { useState } from 'react';
import { useSearchParams, Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { register, getPasswordPolicy, ApiRequestError } from '../api';
import type { PasswordPolicyRule } from '../types';
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
      await register(email, password, firstName || undefined, lastName || undefined);

      // Redirect to login with success message
      const params = new URLSearchParams();
      if (returnUrl) params.set('returnUrl', returnUrl);
      params.set('login_hint', email);
      params.set('message', 'registration_success');
      navigate(`/login?${params.toString()}`);
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
          default:
            setError(err.message || t('errorRegistrationFailed'));
        }
      } else {
        setError(t('errorRegistrationFailed'));
      }
    } finally {
      setLoading(false);
    }
  }

  const loginLink = returnUrl
    ? `/login?returnUrl=${encodeURIComponent(returnUrl)}`
    : '/login';

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

        {policyRules.length > 0 && (
          <ul className="text-[13px] text-gray-500 mb-4 pl-5 list-disc">
            {policyRules.map((rule) => (
              <li key={rule.rule}>{rule.label}</li>
            ))}
          </ul>
        )}

        <Button type="submit" loading={loading}>
          {loading ? t('registering') : t('registerButton')}
        </Button>

        <CardFooter>
          <span className="text-sm text-gray-500">
            {t('alreadyHaveAccount')}{' '}
            <Link to={loginLink} className="text-sm font-medium text-primary hover:underline no-underline">{t('signIn')}</Link>
          </span>
        </CardFooter>
      </form>
    </div>
  );
}

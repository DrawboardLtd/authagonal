import { useState } from 'react';
import { useSearchParams, Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { register, getPasswordPolicy, ApiRequestError } from '../api';
import type { PasswordPolicyRule } from '../types';

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
      <h2 className="auth-title">{t('registerTitle')}</h2>

      {error && <div className="alert-error">{error}</div>}

      <form onSubmit={handleSubmit}>
        <div style={{ display: 'flex', gap: '12px' }}>
          <div className="form-group" style={{ flex: 1 }}>
            <label htmlFor="firstName">{t('firstName')}</label>
            <input
              id="firstName"
              type="text"
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              placeholder={t('firstNamePlaceholder')}
              autoComplete="given-name"
              maxLength={100}
            />
          </div>
          <div className="form-group" style={{ flex: 1 }}>
            <label htmlFor="lastName">{t('lastName')}</label>
            <input
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

        <div className="form-group">
          <label htmlFor="email">{t('email')}</label>
          <input
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

        <div className="form-group">
          <label htmlFor="password">{t('password')}</label>
          <input
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
          <ul style={{ fontSize: '13px', color: '#6b7280', margin: '0 0 16px 0', paddingLeft: '20px' }}>
            {policyRules.map((rule) => (
              <li key={rule.rule}>{rule.label}</li>
            ))}
          </ul>
        )}

        <button type="submit" className="btn-primary" disabled={loading}>
          {loading ? (
            <span className="btn-loading">
              <span className="spinner" />
              {t('registering')}
            </span>
          ) : (
            t('registerButton')
          )}
        </button>

        <div className="form-footer">
          <span style={{ color: '#6b7280', fontSize: '14px' }}>
            {t('alreadyHaveAccount')}{' '}
            <Link to={loginLink} className="link">{t('signIn')}</Link>
          </span>
        </div>
      </form>
    </div>
  );
}

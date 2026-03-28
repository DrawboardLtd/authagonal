import { useState } from 'react';
import { useSearchParams, Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { forgotPassword } from '../api';

export default function ForgotPasswordPage() {
  const { t } = useTranslation();
  const [searchParams] = useSearchParams();
  const returnUrl = searchParams.get('returnUrl') || '';

  const [email, setEmail] = useState('');
  const [loading, setLoading] = useState(false);
  const [submitted, setSubmitted] = useState(false);
  const [error, setError] = useState('');

  const loginLink = returnUrl
    ? `/login?returnUrl=${encodeURIComponent(returnUrl)}`
    : '/login';

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      await forgotPassword(email);
      setSubmitted(true);
    } catch {
      // The API always returns 200 for anti-enumeration, but handle errors just in case
      setError(t('errorUnexpected'));
    } finally {
      setLoading(false);
    }
  }

  if (submitted) {
    return (
      <div>
        <h2 className="auth-title">{t('checkYourEmail')}</h2>
        <div className="alert-success">
          {t('resetEmailSent')}
        </div>
        <div className="form-footer">
          <Link to={loginLink} className="link">
            {t('backToSignIn')}
          </Link>
        </div>
      </div>
    );
  }

  return (
    <div>
      <h2 className="auth-title">{t('resetYourPassword')}</h2>
      <p className="auth-subtitle">
        {t('resetSubtitle')}
      </p>

      {error && <div className="alert-error">{error}</div>}

      <form onSubmit={handleSubmit}>
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

        <button
          type="submit"
          className="btn-primary"
          disabled={loading}
        >
          {loading ? (
            <span className="btn-loading">
              <span className="spinner" />
              {t('sending')}
            </span>
          ) : (
            t('sendResetLink')
          )}
        </button>

        <div className="form-footer">
          <Link to={loginLink} className="link">
            {t('backToSignIn')}
          </Link>
        </div>
      </form>
    </div>
  );
}

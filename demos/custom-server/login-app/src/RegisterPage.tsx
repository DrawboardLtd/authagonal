import { useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from '@drawboard/authagonal-login';

const API_URL = import.meta.env.VITE_API_URL || '';

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

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      const response = await fetch(`${API_URL}/api/auth/register`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, password, firstName, lastName }),
      });

      if (!response.ok) {
        const body = await response.json().catch(() => null);
        switch (body?.error) {
          case 'email_already_registered':
            setError('An account with this email already exists.');
            break;
          case 'password_too_short':
            setError(body.message || 'Password must be at least 8 characters.');
            break;
          case 'email_and_password_required':
            setError('Email and password are required.');
            break;
          default:
            setError(body?.message || 'Registration failed. Please try again.');
        }
        return;
      }

      // Registration succeeded — navigate to login
      const loginUrl = returnUrl
        ? `/login?returnUrl=${encodeURIComponent(returnUrl)}&login_hint=${encodeURIComponent(email)}`
        : `/login?login_hint=${encodeURIComponent(email)}`;
      navigate(loginUrl);
    } catch {
      setError('An unexpected error occurred. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  const loginLink = returnUrl
    ? `/login?returnUrl=${encodeURIComponent(returnUrl)}`
    : '/login';

  return (
    <div>
      <h2 className="auth-title">Create Account</h2>

      {error && <div className="alert-error">{error}</div>}

      <form onSubmit={handleSubmit}>
        <div style={{ display: 'flex', gap: '12px' }}>
          <div className="form-group" style={{ flex: 1 }}>
            <label htmlFor="firstName">First name</label>
            <input
              id="firstName"
              type="text"
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              placeholder="Jane"
              autoComplete="given-name"
              maxLength={100}
            />
          </div>
          <div className="form-group" style={{ flex: 1 }}>
            <label htmlFor="lastName">Last name</label>
            <input
              id="lastName"
              type="text"
              value={lastName}
              onChange={(e) => setLastName(e.target.value)}
              placeholder="Doe"
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
            placeholder="At least 8 characters"
            autoComplete="new-password"
            minLength={8}
            maxLength={256}
            required
          />
        </div>

        <button type="submit" className="btn-primary" disabled={loading}>
          {loading ? (
            <span className="btn-loading">
              <span className="spinner" />
              Creating account...
            </span>
          ) : (
            'Create Account'
          )}
        </button>

        <div className="form-footer">
          <span style={{ color: '#6b7280', fontSize: '14px' }}>
            Already have an account?{' '}
            <Link to={loginLink} className="link">{t('signIn')}</Link>
          </span>
        </div>
      </form>
    </div>
  );
}

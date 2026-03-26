import { useState } from 'react';
import { useSearchParams, Link } from 'react-router-dom';
import { forgotPassword } from '../api';

export default function ForgotPasswordPage() {
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
      setError('An unexpected error occurred. Please try again.');
    } finally {
      setLoading(false);
    }
  }

  if (submitted) {
    return (
      <div>
        <h2 className="auth-title">Check your email</h2>
        <div className="alert-success">
          If an account exists with that email, you'll receive a reset link shortly.
        </div>
        <div className="form-footer">
          <Link to={loginLink} className="link">
            Back to sign in
          </Link>
        </div>
      </div>
    );
  }

  return (
    <div>
      <h2 className="auth-title">Reset your password</h2>
      <p className="auth-subtitle">
        Enter your email address and we'll send you a link to reset your password.
      </p>

      {error && <div className="alert-error">{error}</div>}

      <form onSubmit={handleSubmit}>
        <div className="form-group">
          <label htmlFor="email">Email</label>
          <input
            id="email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="you@example.com"
            autoComplete="email"
            autoFocus
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
              Sending...
            </span>
          ) : (
            'Send reset link'
          )}
        </button>

        <div className="form-footer">
          <Link to={loginLink} className="link">
            Back to sign in
          </Link>
        </div>
      </form>
    </div>
  );
}

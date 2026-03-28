import { useState } from 'react';
import { useSearchParams, Link } from 'react-router-dom';
import { resetPassword, ApiRequestError } from '../api';

interface PasswordRequirement {
  label: string;
  met: boolean;
}

function getPasswordRequirements(password: string): PasswordRequirement[] {
  return [
    { label: 'At least 8 characters', met: password.length >= 8 },
    { label: 'Uppercase letter', met: /[A-Z]/.test(password) },
    { label: 'Lowercase letter', met: /[a-z]/.test(password) },
    { label: 'Number', met: /[0-9]/.test(password) },
    { label: 'Special character', met: /[^A-Za-z0-9]/.test(password) },
  ];
}

export default function ResetPasswordPage() {
  const [searchParams] = useSearchParams();
  const token = searchParams.get('p') || '';

  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState(false);
  const [validationError, setValidationError] = useState('');

  const requirements = getPasswordRequirements(newPassword);
  const allRequirementsMet = requirements.every((r) => r.met);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setValidationError('');

    if (newPassword.length < 8) {
      setValidationError('Password must be at least 8 characters');
      return;
    }

    if (newPassword !== confirmPassword) {
      setValidationError('Passwords do not match');
      return;
    }

    setLoading(true);

    try {
      await resetPassword(token, newPassword);
      setSuccess(true);
    } catch (err) {
      if (err instanceof ApiRequestError) {
        switch (err.error) {
          case 'weak_password':
            setError(err.message || 'Password does not meet strength requirements');
            break;
          case 'invalid_token':
          case 'token_expired':
            setError('This reset link is invalid or has expired');
            break;
          case 'password_required':
            setError('Password is required');
            break;
          default:
            setError(err.message || 'An unexpected error occurred');
        }
      } else {
        setError('An unexpected error occurred. Please try again.');
      }
    } finally {
      setLoading(false);
    }
  }

  if (success) {
    return (
      <div>
        <h2 className="auth-title">Password reset successfully</h2>
        <div className="alert-success">
          Your password has been reset. You can now sign in with your new password.
        </div>
        <div className="form-footer">
          <Link to="/login" className="link">
            Sign in
          </Link>
        </div>
      </div>
    );
  }

  if (!token) {
    return (
      <div>
        <h2 className="auth-title">Invalid link</h2>
        <div className="alert-error">
          This reset link is invalid or has expired.
        </div>
        <div className="form-footer">
          <Link to="/forgot-password" className="link">
            Request a new reset link
          </Link>
        </div>
      </div>
    );
  }

  return (
    <div>
      <h2 className="auth-title">Set new password</h2>

      {error && <div className="alert-error">{error}</div>}
      {validationError && <div className="alert-error">{validationError}</div>}

      <form onSubmit={handleSubmit}>
        <div className="form-group">
          <label htmlFor="newPassword">New password</label>
          <input
            id="newPassword"
            type="password"
            value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)}
            placeholder="Enter new password"
            autoComplete="new-password"
            autoFocus
            maxLength={256}
            required
          />
        </div>

        {newPassword.length > 0 && (
          <ul className="password-requirements">
            {requirements.map((req) => (
              <li key={req.label} className={req.met ? 'met' : 'unmet'}>
                <span className="req-icon">{req.met ? '\u2713' : '\u2717'}</span>
                {req.label}
              </li>
            ))}
          </ul>
        )}

        <div className="form-group">
          <label htmlFor="confirmPassword">Confirm password</label>
          <input
            id="confirmPassword"
            type="password"
            value={confirmPassword}
            onChange={(e) => setConfirmPassword(e.target.value)}
            placeholder="Confirm new password"
            autoComplete="new-password"
            maxLength={256}
            required
          />
        </div>

        <button
          type="submit"
          className="btn-primary"
          disabled={loading || !allRequirementsMet}
        >
          {loading ? (
            <span className="btn-loading">
              <span className="spinner" />
              Resetting...
            </span>
          ) : (
            'Reset password'
          )}
        </button>

        <div className="form-footer">
          <Link to="/login" className="link">
            Back to sign in
          </Link>
        </div>
      </form>
    </div>
  );
}

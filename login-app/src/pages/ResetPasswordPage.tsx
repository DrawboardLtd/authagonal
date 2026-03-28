import { useState, useEffect } from 'react';
import { useSearchParams, Link } from 'react-router-dom';
import { resetPassword, ApiRequestError } from '../api';

interface PasswordRule {
  rule: string;
  value: number | null;
  label: string;
}

interface PasswordRequirement {
  label: string;
  met: boolean;
}

const API_URL = import.meta.env.VITE_API_URL || '';

const defaultRules: PasswordRule[] = [
  { rule: 'minLength', value: 8, label: 'At least 8 characters' },
  { rule: 'uppercase', value: null, label: 'Uppercase letter' },
  { rule: 'lowercase', value: null, label: 'Lowercase letter' },
  { rule: 'digit', value: null, label: 'Number' },
  { rule: 'specialChar', value: null, label: 'Special character' },
];

function evaluateRequirements(password: string, rules: PasswordRule[]): PasswordRequirement[] {
  return rules.map((r) => {
    let met = false;
    switch (r.rule) {
      case 'minLength': met = password.length >= (r.value ?? 8); break;
      case 'uppercase': met = /[A-Z]/.test(password); break;
      case 'lowercase': met = /[a-z]/.test(password); break;
      case 'digit': met = /[0-9]/.test(password); break;
      case 'specialChar': met = /[^A-Za-z0-9]/.test(password); break;
      default: met = true;
    }
    return { label: r.label, met };
  });
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
  const [rules, setRules] = useState<PasswordRule[]>(defaultRules);

  useEffect(() => {
    fetch(`${API_URL}/api/auth/password-policy`)
      .then((r) => r.ok ? r.json() : null)
      .then((data) => { if (data?.rules) setRules(data.rules); })
      .catch(() => { /* use defaults */ });
  }, []);

  const requirements = evaluateRequirements(newPassword, rules);
  const allRequirementsMet = requirements.every((r) => r.met);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setValidationError('');

    if (!allRequirementsMet) {
      setValidationError('Password does not meet the requirements');
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

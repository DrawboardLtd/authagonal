import { useState, useEffect, useRef, useCallback } from 'react';
import { useSearchParams, Link } from 'react-router-dom';
import { login, ssoCheck, ApiRequestError } from '../api';
import { useBranding } from '../branding';

const API_URL = import.meta.env.VITE_API_URL || '';

function isSafeReturnUrl(url: string): boolean {
  if (!url) return false;
  // Only allow relative paths (starting with /) that don't escape to another host
  try {
    const parsed = new URL(url, window.location.origin);
    return parsed.origin === window.location.origin && url.startsWith('/');
  } catch {
    return false;
  }
}

export default function LoginPage() {
  const branding = useBranding();
  const [searchParams] = useSearchParams();
  const returnUrl = searchParams.get('returnUrl') || '';
  const loginHint = searchParams.get('login_hint') || '';

  const [email, setEmail] = useState(loginHint);
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [ssoInfo, setSsoInfo] = useState<{ redirectUrl: string } | null>(null);
  const [ssoChecked, setSsoChecked] = useState(false);
  const [ssoChecking, setSsoChecking] = useState(false);

  const debounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastCheckedEmailRef = useRef('');

  const performSsoCheck = useCallback(async (emailToCheck: string) => {
    if (!emailToCheck.includes('@') || emailToCheck === lastCheckedEmailRef.current) {
      return;
    }

    lastCheckedEmailRef.current = emailToCheck;
    setSsoChecking(true);
    setError('');

    try {
      const result = await ssoCheck(emailToCheck);
      if (result.ssoRequired && result.redirectUrl) {
        setSsoInfo({ redirectUrl: result.redirectUrl });
      } else {
        setSsoInfo(null);
      }
      setSsoChecked(true);
    } catch {
      // If SSO check fails, allow normal login
      setSsoInfo(null);
      setSsoChecked(true);
    } finally {
      setSsoChecking(false);
    }
  }, []);

  // Auto-trigger SSO check if login_hint is provided
  useEffect(() => {
    if (loginHint && loginHint.includes('@')) {
      performSsoCheck(loginHint);
    }
  }, [loginHint, performSsoCheck]);

  function handleEmailBlur() {
    if (debounceTimerRef.current) {
      clearTimeout(debounceTimerRef.current);
    }
    debounceTimerRef.current = setTimeout(() => {
      performSsoCheck(email);
    }, 300);
  }

  function handleEmailChange(value: string) {
    setEmail(value);
    // Reset SSO state when email changes
    if (value !== lastCheckedEmailRef.current) {
      setSsoChecked(false);
      setSsoInfo(null);
    }
  }

  function handleSsoRedirect() {
    if (ssoInfo) {
      const ssoUrl = new URL(`${API_URL}${ssoInfo.redirectUrl}`, window.location.origin);
      if (returnUrl && isSafeReturnUrl(returnUrl)) {
        ssoUrl.searchParams.set('returnUrl', returnUrl);
      }
      window.location.href = ssoUrl.toString();
    }
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      await login(email, password);
      // On success, redirect to returnUrl (validated) using window.location.href
      if (returnUrl && isSafeReturnUrl(returnUrl)) {
        window.location.href = returnUrl;
      } else {
        window.location.href = '/';
      }
    } catch (err) {
      if (err instanceof ApiRequestError) {
        switch (err.error) {
          case 'invalid_credentials':
            setError('Invalid email or password');
            break;
          case 'locked_out':
            setError(`Account locked. Try again in ${err.retryAfter ?? '?'} seconds`);
            break;
          case 'email_not_confirmed':
            setError('Please check your email to verify your account');
            break;
          case 'sso_required':
            if (err.redirectUrl) {
              const ssoUrl = new URL(`${API_URL}${err.redirectUrl}`, window.location.origin);
              if (returnUrl && isSafeReturnUrl(returnUrl)) {
                ssoUrl.searchParams.set('returnUrl', returnUrl);
              }
              window.location.href = ssoUrl.toString();
              return;
            }
            setError('Single sign-on is required for this account');
            break;
          case 'email_required':
            setError('Email is required');
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

  const forgotPasswordLink = returnUrl && isSafeReturnUrl(returnUrl)
    ? `/forgot-password?returnUrl=${encodeURIComponent(returnUrl)}`
    : '/forgot-password';

  const showPasswordField = ssoChecked && !ssoInfo;

  return (
    <div>
      <h2 className="auth-title">Sign in</h2>

      {error && <div className="alert-error">{error}</div>}

      <form onSubmit={handleSubmit}>
        <div className="form-group">
          <label htmlFor="email">Email</label>
          <input
            id="email"
            type="email"
            value={email}
            onChange={(e) => handleEmailChange(e.target.value)}
            onBlur={handleEmailBlur}
            placeholder="you@example.com"
            autoComplete="email"
            autoFocus={!loginHint}
            maxLength={256}
            required
          />
        </div>

        {ssoChecking && (
          <div className="sso-checking">Checking sign-in options...</div>
        )}

        {ssoInfo && (
          <div className="sso-notice">
            <p>Your organization uses single sign-on</p>
            <button
              type="button"
              className="btn-secondary"
              onClick={handleSsoRedirect}
            >
              Continue with SSO
            </button>
          </div>
        )}

        {showPasswordField && (
          <>
            <div className="form-group">
              <label htmlFor="password">Password</label>
              <input
                id="password"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                placeholder="Enter your password"
                autoComplete="current-password"
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
                  Signing in...
                </span>
              ) : (
                'Sign in'
              )}
            </button>

            {branding.showForgotPassword && (
              <div className="form-footer">
                <Link to={forgotPasswordLink} className="link">
                  Forgot password?
                </Link>
              </div>
            )}
          </>
        )}
      </form>
    </div>
  );
}

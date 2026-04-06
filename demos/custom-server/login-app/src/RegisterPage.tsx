import { useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation, Button, Input, Label, Alert, CardTitle, CardFooter } from '@drawboard/authagonal-login';

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
  const [agreedToTerms, setAgreedToTerms] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!agreedToTerms) {
      setError('You must agree to the Terms of Service to continue.');
      return;
    }
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
      <CardTitle>Create Account</CardTitle>

      {error && <Alert variant="error">{error}</Alert>}

      <form onSubmit={handleSubmit}>
        <div className="flex gap-3 mb-4">
          <div className="flex-1">
            <Label htmlFor="firstName">First name</Label>
            <Input
              id="firstName"
              type="text"
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              placeholder="Jane"
              autoComplete="given-name"
              maxLength={100}
            />
          </div>
          <div className="flex-1">
            <Label htmlFor="lastName">Last name</Label>
            <Input
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
            placeholder="At least 8 characters"
            autoComplete="new-password"
            minLength={8}
            maxLength={256}
            required
          />
        </div>

        <label htmlFor="terms" className="flex items-start gap-2 text-[13px] text-gray-600 cursor-pointer my-3">
          <input
            id="terms"
            type="checkbox"
            checked={agreedToTerms}
            onChange={(e) => setAgreedToTerms(e.target.checked)}
            className="shrink-0 mt-0.5 accent-primary"
          />
          <span>
            I agree to the Acme Corp{' '}
            <a href="https://acme.example.com/terms" target="_blank" rel="noopener noreferrer" className="text-primary hover:underline">
              Terms of Service
            </a>{' '}
            and{' '}
            <a href="https://acme.example.com/privacy" target="_blank" rel="noopener noreferrer" className="text-primary hover:underline">
              Privacy Policy
            </a>
          </span>
        </label>

        <Button type="submit" loading={loading}>
          {loading ? 'Creating account...' : 'Create Account'}
        </Button>

        <CardFooter>
          <span className="text-sm text-gray-500">
            Already have an account?{' '}
            <Link to={loginLink} className="text-sm font-medium text-primary hover:underline no-underline">{t('signIn')}</Link>
          </span>
        </CardFooter>
      </form>
    </div>
  );
}

import { useState, useEffect } from 'react';
import { useSearchParams, Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { resetPassword, ApiRequestError } from '../api';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { CardTitle, CardFooter } from '@/components/ui/card';
import { Check, X } from 'lucide-react';

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
  const { t } = useTranslation();
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

  function getRuleLabel(rule: PasswordRule): string {
    switch (rule.rule) {
      case 'minLength': return t('ruleMinLength', { count: rule.value ?? 8 });
      case 'uppercase': return t('ruleUppercase');
      case 'lowercase': return t('ruleLowercase');
      case 'digit': return t('ruleDigit');
      case 'specialChar': return t('ruleSpecialChar');
      default: return rule.label;
    }
  }

  const localizedRules: PasswordRule[] = rules.map(r => ({
    ...r,
    label: getRuleLabel(r),
  }));

  const requirements = evaluateRequirements(newPassword, localizedRules);
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
      await resetPassword(token, newPassword);
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
          default:
            setError(err.message || t('errorUnexpected'));
        }
      } else {
        setError(t('errorUnexpected'));
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
        <CardFooter>
          <Link to="/login" className="text-sm font-medium text-primary hover:underline no-underline">
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

        <Button type="submit" loading={loading} disabled={!allRequirementsMet}>
          {loading ? t('resetting') : t('resetPassword')}
        </Button>

        <CardFooter>
          <Link to="/login" className="text-sm font-medium text-primary hover:underline no-underline">
            {t('backToSignIn')}
          </Link>
        </CardFooter>
      </form>
    </div>
  );
}

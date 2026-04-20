import { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import AuthLayout from '../components/AuthLayout';
import { CardTitle, CardFooter } from '../components/ui/card';
import { Button } from '../components/ui/button';
import { Alert } from '../components/ui/alert';

const SCOPE_LABELS: Record<string, string> = {
  openid: 'consent.scopeOpenid',
  profile: 'consent.scopeProfile',
  email: 'consent.scopeEmail',
  offline_access: 'consent.scopeOfflineAccess',
  address: 'consent.scopeAddress',
  phone: 'consent.scopePhone',
};

interface ConsentInfo {
  clientId: string;
  clientName: string;
  description?: string;
  clientUri?: string;
  logoUri?: string;
  scopes: string[];
}

export default function ConsentPage() {
  const { t } = useTranslation();
  const [searchParams] = useSearchParams();
  const clientId = searchParams.get('client_id') ?? '';
  const scope = searchParams.get('scope') ?? 'openid';
  const returnUrl = searchParams.get('returnUrl') ?? '/';

  const [info, setInfo] = useState<ConsentInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    fetch(`/consent/info?client_id=${encodeURIComponent(clientId)}&scope=${encodeURIComponent(scope)}`)
      .then(async (res) => {
        if (!res.ok) throw new Error('Failed to load');
        setInfo(await res.json());
      })
      .catch(() => setError(t('consent.loadError')))
      .finally(() => setLoading(false));
  }, [clientId, scope, t]);

  async function handleDecision(decision: 'allow' | 'deny') {
    setSubmitting(true);
    setError('');
    try {
      const res = await fetch('/consent', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          clientId,
          decision,
          scopes: info?.scopes ?? scope.split(' '),
          returnUrl,
        }),
      });
      const data = await res.json();
      if (data.redirect) {
        window.location.href = data.redirect;
      }
    } catch {
      setError(t('consent.submitError'));
      setSubmitting(false);
    }
  }

  if (loading) {
    return (
      <AuthLayout>
        <p className="text-sm text-gray-500 dark:text-gray-400 text-center">{t('consent.loading')}</p>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout>
      {info?.logoUri && (
        <div className="flex justify-center mb-4">
          <img
            src={info.logoUri}
            alt={info.clientName}
            className="h-12 w-12 rounded-lg object-contain"
            onError={(e) => {
              (e.currentTarget as HTMLImageElement).style.display = 'none';
            }}
          />
        </div>
      )}
      <CardTitle>{t('consent.title', { appName: info?.clientName ?? clientId })}</CardTitle>
      <p className="text-sm text-gray-500 dark:text-gray-400 mb-4">{t('consent.subtitle', { appName: info?.clientName ?? clientId })}</p>

      {info?.description && (
        <p className="text-sm text-gray-600 dark:text-gray-300 mb-4">{info.description}</p>
      )}
      {info?.clientUri && (
        <p className="text-xs text-gray-500 dark:text-gray-400 mb-4">
          <a
            href={info.clientUri}
            target="_blank"
            rel="noopener noreferrer"
            className="text-primary hover:underline"
          >
            {info.clientUri}
          </a>
        </p>
      )}

      {error && <Alert variant="error">{error}</Alert>}

      <div className="space-y-2 mb-6">
        {(info?.scopes ?? scope.split(' ')).map((s) => {
          const labelKey = SCOPE_LABELS[s];
          return (
            <div key={s} className="flex items-center gap-3 p-3 bg-gray-50 dark:bg-gray-800/60 rounded-lg">
              <div className="w-2 h-2 bg-primary rounded-full shrink-0" />
              <span className="text-sm text-gray-700 dark:text-gray-300">
                {labelKey ? t(labelKey) : s}
              </span>
            </div>
          );
        })}
      </div>

      <div className="flex gap-3">
        <Button onClick={() => handleDecision('allow')} loading={submitting} className="flex-1">
          {t('consent.allow')}
        </Button>
        <Button variant="secondary" onClick={() => handleDecision('deny')} disabled={submitting} className="flex-1">
          {t('consent.deny')}
        </Button>
      </div>

      <CardFooter>
        <p className="text-xs text-gray-400 dark:text-gray-500">{t('consent.hint')}</p>
      </CardFooter>
    </AuthLayout>
  );
}

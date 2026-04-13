import { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import AuthLayout from '../components/AuthLayout';
import { CardTitle } from '../components/ui/card';
import { Button } from '../components/ui/button';
import { Alert } from '../components/ui/alert';

interface ConsentGrant {
  clientId: string;
  clientName: string;
  scopes: string[];
  consentedAt: string;
}

export default function GrantsPage() {
  const { t } = useTranslation();
  const [grants, setGrants] = useState<ConsentGrant[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [revoking, setRevoking] = useState('');

  useEffect(() => {
    fetch('/consent/grants')
      .then(async (res) => {
        if (!res.ok) throw new Error();
        setGrants(await res.json());
      })
      .catch(() => setError(t('grants.loadError')))
      .finally(() => setLoading(false));
  }, [t]);

  async function handleRevoke(clientId: string) {
    if (!confirm(t('grants.revokeConfirm'))) return;
    setRevoking(clientId);
    try {
      const res = await fetch(`/consent/grants/${encodeURIComponent(clientId)}`, { method: 'DELETE' });
      if (res.ok) {
        setGrants(g => g.filter(x => x.clientId !== clientId));
      } else {
        setError(t('grants.revokeFailed'));
      }
    } catch {
      setError(t('grants.revokeFailed'));
    } finally {
      setRevoking('');
    }
  }

  return (
    <AuthLayout>
      <CardTitle>{t('grants.title')}</CardTitle>
      <p className="text-sm text-gray-500 mb-4">{t('grants.subtitle')}</p>

      {error && <Alert variant="error">{error}</Alert>}

      {loading ? (
        <p className="text-sm text-gray-500">{t('grants.loading')}</p>
      ) : grants.length === 0 ? (
        <p className="text-sm text-gray-500">{t('grants.noGrants')}</p>
      ) : (
        <div className="space-y-3">
          {grants.map((g) => (
            <div key={g.clientId} className="flex items-start justify-between p-3 bg-gray-50 rounded-lg">
              <div>
                <p className="text-sm font-medium text-gray-900">{g.clientName}</p>
                <p className="text-xs text-gray-500 mt-0.5">
                  {g.scopes.join(', ')}
                </p>
                <p className="text-xs text-gray-400 mt-0.5">
                  {t('grants.grantedOn', { date: new Date(g.consentedAt).toLocaleDateString() })}
                </p>
              </div>
              <Button
                variant="secondary"
                size="sm"
                loading={revoking === g.clientId}
                onClick={() => handleRevoke(g.clientId)}
              >
                {t('grants.revoke')}
              </Button>
            </div>
          ))}
        </div>
      )}
    </AuthLayout>
  );
}

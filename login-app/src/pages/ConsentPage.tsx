import { useState, useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
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

/**
 * Scopes the user is not offered a choice about. `openid` is what makes this an OpenID Connect
 * request at all — without it no id_token is issued and the client has nothing to sign the user in
 * with, so presenting it as optional would only produce a grant that cannot work.
 */
const REQUIRED_SCOPES = new Set(['openid']);

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
  const [granted, setGranted] = useState<Set<string>>(new Set());

  const offered = useMemo(
    () => info?.scopes ?? scope.split(' ').filter(Boolean),
    [info, scope],
  );

  useEffect(() => {
    fetch(`/consent/info?client_id=${encodeURIComponent(clientId)}&scope=${encodeURIComponent(scope)}`)
      .then(async (res) => {
        if (!res.ok) throw new Error('Failed to load');
        setInfo(await res.json());
      })
      .catch(() => setError(t('consent.loadError')))
      .finally(() => setLoading(false));
  }, [clientId, scope, t]);

  // Everything starts ticked: this screen is an opportunity to grant LESS than the app asked for, not
  // a form the user has to fill in before they can continue.
  useEffect(() => setGranted(new Set(offered)), [offered]);

  /**
   * A readable label for scopes with no translation of their own, e.g.
   * `projects-api.tracked-records.write` → "View and change tracked records". Anything not shaped
   * `<api>.<resource>.<read|write>` is shown verbatim: a raw scope string is unhelpful, but a guessed
   * label on a permission prompt would be worse.
   */
  function describeScope(s: string): string {
    const labelKey = SCOPE_LABELS[s];
    if (labelKey) return t(labelKey);

    const parts = s.split('.');
    const action = parts[parts.length - 1];
    if (parts.length < 3 || (action !== 'read' && action !== 'write')) return s;

    const resource = parts.slice(1, -1).join(' ').replace(/-/g, ' ');
    return t(action === 'write' ? 'consent.scopeWrite' : 'consent.scopeRead', { resource });
  }

  function toggle(s: string) {
    if (REQUIRED_SCOPES.has(s)) return;
    setGranted((current) => {
      const next = new Set(current);
      if (next.has(s)) {
        next.delete(s);
      } else {
        next.add(s);
      }
      return next;
    });
  }

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
          // Only what the user left ticked. The server records this as the grant and the scopes it was
          // chosen from separately, so a scope declined here is not re-prompted on every sign-in.
          scopes: [...granted],
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
      <p className="text-sm text-gray-500 dark:text-gray-400 text-center">{t('consent.loading')}</p>
    );
  }

  return (
    <>
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

      <p className="text-xs text-gray-500 dark:text-gray-400 mb-3">{t('consent.selectHint')}</p>

      <div className="space-y-2 mb-6">
        {offered.map((s) => {
          const required = REQUIRED_SCOPES.has(s);
          return (
            <label
              key={s}
              className={`flex items-center gap-3 p-3 bg-gray-50 dark:bg-gray-800/60 rounded-lg ${
                required ? 'cursor-default' : 'cursor-pointer'
              }`}
            >
              <input
                type="checkbox"
                checked={granted.has(s)}
                disabled={required || submitting}
                onChange={() => toggle(s)}
                className="h-4 w-4 shrink-0 accent-primary"
              />
              <span className="text-sm text-gray-700 dark:text-gray-300 flex-1">
                {describeScope(s)}
              </span>
              {required && (
                <span className="text-xs text-gray-500 dark:text-gray-400 shrink-0">
                  {t('consent.required')}
                </span>
              )}
            </label>
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
        <p className="text-xs text-gray-600 dark:text-gray-300">{t('consent.hint')}</p>
      </CardFooter>
    </>
  );
}

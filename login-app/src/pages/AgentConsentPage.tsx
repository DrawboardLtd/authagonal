import { useState, useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useSearchParams, useNavigate } from 'react-router';
import { CardTitle, CardFooter } from '../components/ui/card';
import { Button } from '../components/ui/button';
import { Alert } from '../components/ui/alert';
import AgentAuthorityList from '../components/AgentAuthorityList';
import { resolveRedirect } from '../lib/returnUrl';
import {
  decorateAuthority,
  constraintSummary,
  narrowCeiling,
  type AgentConsentInfo,
} from '../lib/agentAuthority';

/** Selection key. Actions are unique only within a connector type, so both are needed. */
function key(type: string, action: string) {
  return `${type}::${action}`;
}

/**
 * Granting an agent standing authority to act on the user's behalf — the RFC 9396 counterpart of
 * ConsentPage.
 *
 * This is a separate screen from the scope consent on purpose. The two grant different things:
 * ConsentPage grants OAuth scopes for one client, this grants an authority set that gates every
 * future delegated token the agent mints. Rendering an authority request on a screen that speaks
 * only in scopes would show the user one thing while granting another.
 *
 * The consent is standing — `POST /consent/agents` stores it with a five-year expiry precisely
 * because revocation, not expiry, is the exit. That is the whole reason the Authorized Apps page
 * has to list these too.
 */
export default function AgentConsentPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { clientId = '' } = useParams();
  const [searchParams] = useSearchParams();
  const returnUrl = searchParams.get('returnUrl') ?? '';

  const [info, setInfo] = useState<AgentConsentInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');
  /**
   * What the user has ticked, or `null` while they have not touched the form.
   *
   * Everything starts ticked, as on the scope consent screen: this is an opportunity to grant LESS
   * than the agent asked for, not a form that has to be filled in before the user can continue.
   * `null` expresses that as derived state rather than as an effect that re-seeds a Set every time
   * the fetch resolves — the same default without the cascading render.
   */
  const [granted, setGranted] = useState<Set<string> | null>(null);

  useEffect(() => {
    fetch(`/consent/agents/${encodeURIComponent(clientId)}/info`, { credentials: 'include' })
      .then(async (res) => {
        if (!res.ok) throw new Error('Failed to load');
        setInfo(await res.json());
      })
      .catch(() => setError(t('agentConsent.loadError')))
      .finally(() => setLoading(false));
  }, [clientId, t]);

  /**
   * What is on offer, in the connector catalog's words.
   *
   * Driven by `ceiling` (the RFC 9396 wire form) rather than by `connectors`, because the wire form
   * is what the server will intersect the grant against: it has already dropped deny-policy actions
   * and grants that permit nothing. Offering the user a tick box for something that cannot be
   * granted would be a lie the Allow button then quietly corrects.
   */
  const offered = useMemo(
    () => decorateAuthority(info?.ceiling, info?.connectors),
    [info],
  );

  const constraints = useMemo(() => {
    const byType: Record<string, string[]> = {};
    for (const grant of info?.ceiling ?? []) byType[grant.type] = constraintSummary(grant);
    return byType;
  }, [info]);

  const everything = useMemo(
    () => new Set(offered.flatMap((c) => c.actions.map((a) => key(c.type, a.name)))),
    [offered],
  );
  const selected = granted ?? everything;

  function toggle(type: string, action: string) {
    setGranted((current) => {
      const next = new Set(current ?? everything);
      const k = key(type, action);
      if (next.has(k)) next.delete(k);
      else next.add(k);
      return next;
    });
  }

  /**
   * Where the user goes once they have decided, either way. `returnUrl` is caller-supplied, so it
   * goes through the same allow-list every other page uses — a same-origin path, or the home URI of
   * a registered client. Anything else falls back to Authorized Apps, which is where the consent
   * they just made (or declined to make) is managed from.
   */
  async function leave() {
    const target = returnUrl ? await resolveRedirect(returnUrl, () => '') : '';
    if (target) window.location.href = target;
    else navigate('/grants');
  }

  async function handleAllow() {
    setSubmitting(true);
    setError('');
    try {
      const res = await fetch('/consent/agents', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({
          clientId,
          // Always sent, never omitted. Omitting `authority` is defined as consenting to the agent's
          // FULL ceiling, so an unticked action would be granted anyway.
          authority: narrowCeiling(info?.ceiling, (type, action) => selected.has(key(type, action))),
        }),
      });
      if (!res.ok) {
        setError(t('agentConsent.submitError'));
        setSubmitting(false);
        return;
      }
      await leave();
    } catch {
      setError(t('agentConsent.submitError'));
      setSubmitting(false);
    }
  }

  if (loading) {
    return (
      <p className="text-sm text-gray-500 dark:text-gray-400 text-center">
        {t('agentConsent.loading')}
      </p>
    );
  }

  const appName = info?.clientName || clientId;

  return (
    <>
      {info?.logoUri && (
        <div className="flex justify-center mb-4">
          <img
            src={info.logoUri}
            alt={appName}
            className="h-12 w-12 rounded-lg object-contain"
            onError={(e) => {
              (e.currentTarget as HTMLImageElement).style.display = 'none';
            }}
          />
        </div>
      )}

      <CardTitle>{t('agentConsent.title', { agentName: appName })}</CardTitle>
      <p className="text-sm text-gray-500 dark:text-gray-400 mb-4">
        {t('agentConsent.subtitle', { agentName: appName })}
      </p>

      {info?.description && (
        <p className="text-sm text-gray-600 dark:text-gray-300 mb-4">{info.description}</p>
      )}

      {error && <Alert variant="error">{error}</Alert>}

      {info && offered.length === 0 ? (
        <p className="text-sm text-gray-500 dark:text-gray-400 mb-6">
          {t('agentConsent.nothingToGrant')}
        </p>
      ) : (
        <>
          <p className="text-xs text-gray-500 dark:text-gray-400 mb-3">
            {t('agentConsent.selectHint')}
          </p>
          <div className="mb-6">
            <AgentAuthorityList
              connectors={offered}
              constraints={constraints}
              selection={{
                isSelected: (type, action) => selected.has(key(type, action)),
                toggle,
                disabled: submitting,
              }}
            />
          </div>
        </>
      )}

      <div className="flex gap-3">
        <Button
          onClick={handleAllow}
          loading={submitting}
          disabled={selected.size === 0}
          className="flex-1"
        >
          {t('agentConsent.allow')}
        </Button>
        <Button variant="secondary" onClick={leave} disabled={submitting} className="flex-1">
          {t('agentConsent.cancel')}
        </Button>
      </div>

      <CardFooter>
        {/* Said before the grant, not discovered afterwards: this consent does not lapse, and the
            Authorized Apps page is where it is taken back. */}
        <p className="text-xs text-gray-600 dark:text-gray-300">{t('agentConsent.standingHint')}</p>
      </CardFooter>
    </>
  );
}

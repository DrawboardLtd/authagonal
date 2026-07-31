import { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { CardTitle } from '../components/ui/card';
import { Button } from '../components/ui/button';
import { Alert } from '../components/ui/alert';
import AgentAuthorityList from '../components/AgentAuthorityList';
import {
  decorateAuthority,
  constraintSummary,
  type AgentConnectorView,
  type AgentConsentInfo,
  type AgentConsentListItem,
  type AgentConsentListResponse,
} from '../lib/agentAuthority';

interface ConsentGrant {
  clientId: string;
  clientName: string;
  scopes: string[];
  consentedAt: string;
}

/** A stored agent consent plus the catalog wording resolved for it. */
interface AgentGrant extends AgentConsentListItem {
  connectors: AgentConnectorView[];
  constraints: Record<string, string[]>;
}

export default function GrantsPage() {
  const { t } = useTranslation();
  const [grants, setGrants] = useState<ConsentGrant[]>([]);
  const [agents, setAgents] = useState<AgentGrant[]>([]);
  const [loading, setLoading] = useState(true);
  // Tracked separately from `loading` so "No apps have been authorized yet" cannot flash on a page
  // that is about to list an agent.
  const [agentsLoading, setAgentsLoading] = useState(true);
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

  /**
   * Standing agent consents (RFC 9396 authority), which gate every delegated token the agent mints
   * and which the server documents as revocable only — they do not expire. Without this list a user
   * auditing "Authorized Apps" would clean up their OAuth grants and never see, let alone be able to
   * withdraw, an agent's authority to act on their behalf.
   *
   * Failures here are silent by design. A host that does not map the agent endpoints (an older
   * server, or an embedding that only takes the protocol surface) answers 404, and that is not an
   * error the user can act on — it means there are no agent consents, which is exactly what an empty
   * list renders as.
   */
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const res = await fetch('/consent/agents', { credentials: 'include' });
        if (!res.ok) return;
        const body: AgentConsentListResponse = await res.json();
        const consents = body?.consents ?? [];

        // The list endpoint returns the granted authority, but only /info knows the connector
        // catalog's display names, descriptions and highRisk flags. One lookup per consent —
        // resolved in parallel, and a failure just leaves that consent rendering under its raw
        // type/action names rather than dropping it off a page whose whole job is to show it.
        const decorated = await Promise.all(
          consents.map(async (consent) => {
            let info: AgentConsentInfo | null = null;
            try {
              const infoRes = await fetch(
                `/consent/agents/${encodeURIComponent(consent.clientId)}/info`,
                { credentials: 'include' },
              );
              if (infoRes.ok) info = await infoRes.json();
            } catch {
              // fall through to raw names
            }
            const constraints: Record<string, string[]> = {};
            for (const grant of consent.authority ?? []) {
              constraints[grant.type] = constraintSummary(grant);
            }
            return {
              ...consent,
              connectors: decorateAuthority(consent.authority, info?.connectors),
              constraints,
            };
          }),
        );
        if (!cancelled) setAgents(decorated);
      } catch {
        // same as a 404: nothing to show
      } finally {
        if (!cancelled) setAgentsLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

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

  /**
   * The exit the server documents and the UI previously did not offer. Subsequent exchanges fail
   * with consent_required on their next mint, and delegated tokens are refresh-less and short-lived,
   * so the tail is bounded.
   */
  async function handleRevokeAgent(clientId: string) {
    if (!confirm(t('grants.agentRevokeConfirm'))) return;
    setRevoking(clientId);
    try {
      const res = await fetch(`/consent/agents/${encodeURIComponent(clientId)}`, {
        method: 'DELETE',
        credentials: 'include',
      });
      if (res.ok) {
        setAgents(a => a.filter(x => x.clientId !== clientId));
      } else {
        setError(t('grants.revokeFailed'));
      }
    } catch {
      setError(t('grants.revokeFailed'));
    } finally {
      setRevoking('');
    }
  }

  // Headings only earn their place once there are two kinds of thing to tell apart. A deployment
  // with no agents sees the page exactly as it was.
  const sectioned = agents.length > 0;

  return (
    <>
      <CardTitle>{t('grants.title')}</CardTitle>
      <p className="text-sm text-gray-500 dark:text-gray-400 mb-4">{t('grants.subtitle')}</p>

      {error && <Alert variant="error">{error}</Alert>}

      {loading || agentsLoading ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">{t('grants.loading')}</p>
      ) : grants.length === 0 && agents.length === 0 ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">{t('grants.noGrants')}</p>
      ) : (
        <>
          {grants.length > 0 && (
            <section>
              {sectioned && (
                <h2 className="text-xs font-medium uppercase tracking-wide text-gray-500 dark:text-gray-400 mb-2">
                  {t('grants.appsHeading')}
                </h2>
              )}
              <div className="space-y-3">
                {grants.map((g) => (
                  <div key={g.clientId} className="flex items-start justify-between p-3 bg-gray-50 dark:bg-gray-800/60 rounded-lg">
                    <div>
                      <p className="text-sm font-medium text-gray-900 dark:text-gray-100">{g.clientName}</p>
                      <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
                        {g.scopes.join(', ')}
                      </p>
                      <p className="text-xs text-gray-400 dark:text-gray-500 mt-0.5">
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
            </section>
          )}

          {agents.length > 0 && (
            <section className={grants.length > 0 ? 'mt-6' : undefined}>
              <h2 className="text-xs font-medium uppercase tracking-wide text-gray-500 dark:text-gray-400 mb-1">
                {t('grants.agentsHeading')}
              </h2>
              <p className="text-xs text-gray-500 dark:text-gray-400 mb-2">
                {t('grants.agentsSubtitle')}
              </p>
              <div className="space-y-3">
                {agents.map((a) => (
                  // Outlined rather than filled, so the connector blocks inside — which carry the
                  // page's usual grey fill — stay legible as a list within the card.
                  <div
                    key={a.clientId}
                    className="p-3 border border-gray-200 dark:border-gray-700 rounded-lg"
                  >
                    <div className="flex items-start justify-between gap-3">
                      <div className="min-w-0">
                        <p className="text-sm font-medium text-gray-900 dark:text-gray-100">
                          {a.clientName}
                        </p>
                        <p className="text-xs text-gray-400 dark:text-gray-500 mt-0.5">
                          {t('grants.grantedOn', {
                            date: new Date(a.consentedAt).toLocaleDateString(),
                          })}
                        </p>
                      </div>
                      <Button
                        variant="secondary"
                        size="sm"
                        loading={revoking === a.clientId}
                        onClick={() => handleRevokeAgent(a.clientId)}
                      >
                        {t('grants.revoke')}
                      </Button>
                    </div>

                    {a.connectors.length > 0 ? (
                      <div className="mt-3">
                        <AgentAuthorityList
                          connectors={a.connectors}
                          constraints={a.constraints}
                        />
                      </div>
                    ) : (
                      <p className="text-xs text-gray-500 dark:text-gray-400 mt-2">
                        {t('grants.agentNoAuthority')}
                      </p>
                    )}
                  </div>
                ))}
              </div>
            </section>
          )}
        </>
      )}
    </>
  );
}

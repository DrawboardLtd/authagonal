import { useTranslation } from 'react-i18next';
import { TriangleAlert } from 'lucide-react';
import type { AgentConnectorView, ActionPolicy } from '../lib/agentAuthority';

interface Selection {
  isSelected: (type: string, action: string) => boolean;
  toggle: (type: string, action: string) => void;
  disabled?: boolean;
}

interface AgentAuthorityListProps {
  connectors: AgentConnectorView[];
  /** Per-connector extra lines (locations, named constraints), keyed by connector type. */
  constraints?: Record<string, string[]>;
  /**
   * Present on the grant screen, absent on the review screen. With it every action gets a checkbox
   * and the list is a form; without it the same markup is a read-only record of what is live.
   */
  selection?: Selection;
}

/**
 * An agent's RFC 9396 authority, rendered as connectors and the actions inside them.
 *
 * Shared by the grant screen and the Authorized Apps review, because the two must describe an
 * authority set identically — a user who revokes on the strength of this list has to be looking at
 * the same words they agreed to.
 */
export default function AgentAuthorityList({
  connectors,
  constraints,
  selection,
}: AgentAuthorityListProps) {
  const { t } = useTranslation();

  return (
    <div className="space-y-2">
      {connectors.map((connector) => (
        <div key={connector.type} className="bg-gray-50 dark:bg-gray-800/60 rounded-lg p-3">
          <p className="text-sm font-medium text-gray-900 dark:text-gray-100">
            {connector.displayName}
          </p>
          {connector.description && (
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">{connector.description}</p>
          )}

          <ul className="mt-2 space-y-1.5">
            {connector.actions.map((action) => {
              const body = (
                <>
                  <span className="flex-1 min-w-0">
                    <span className="block text-sm text-gray-700 dark:text-gray-300">
                      {action.description || action.name}
                    </span>
                    {/* The raw action name stays on screen whenever the catalog supplied a
                        description, because the name is what appears in the token and in an audit
                        log — the user should be able to match the two up. */}
                    {action.description && (
                      <span className="block text-xs text-gray-400 dark:text-gray-500 font-mono mt-0.5">
                        {action.name}
                      </span>
                    )}
                  </span>
                  <span className="flex items-center gap-1.5 shrink-0 mt-0.5">
                    {action.highRisk && (
                      <span className="inline-flex items-center gap-1 text-xs text-amber-700 dark:text-amber-400">
                        <TriangleAlert className="h-3.5 w-3.5" aria-hidden="true" />
                        {t('agentConsent.highRisk')}
                      </span>
                    )}
                    <PolicyBadge policy={action.policy} />
                  </span>
                </>
              );

              // Same row either way. A checkbox turns it into a label so the whole row is the hit
              // target; without one it is a plain list item and nothing looks clickable.
              return selection ? (
                <li key={action.name}>
                  <label className="flex items-start gap-3 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={selection.isSelected(connector.type, action.name)}
                      disabled={selection.disabled}
                      onChange={() => selection.toggle(connector.type, action.name)}
                      className="h-4 w-4 shrink-0 mt-0.5 accent-primary"
                    />
                    {body}
                  </label>
                </li>
              ) : (
                <li key={action.name} className="flex items-start gap-3">
                  {body}
                </li>
              );
            })}
          </ul>

          {constraints?.[connector.type]?.length ? (
            <p className="text-xs text-gray-400 dark:text-gray-500 mt-2">
              {t('agentConsent.limitedTo', { limits: constraints[connector.type].join(' · ') })}
            </p>
          ) : null}
        </div>
      ))}
    </div>
  );
}

/**
 * Whether the action runs on its own or comes back to the user first. `ask` is the one worth
 * spelling out — it is the difference between an agent that can act unattended and one that cannot.
 */
function PolicyBadge({ policy }: { policy: ActionPolicy }) {
  const { t } = useTranslation();
  if (policy === 'ask') {
    return (
      <span className="text-xs text-gray-500 dark:text-gray-400">{t('agentConsent.policyAsk')}</span>
    );
  }
  if (policy === 'deny') {
    return (
      <span className="text-xs text-gray-500 dark:text-gray-400">{t('agentConsent.policyDeny')}</span>
    );
  }
  return (
    <span className="text-xs text-gray-500 dark:text-gray-400">{t('agentConsent.policyAuto')}</span>
  );
}

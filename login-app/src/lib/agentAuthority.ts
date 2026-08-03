/**
 * The RFC 9396 `authorization_details` shapes the agent-consent endpoints speak, plus the small
 * amount of logic needed to render them.
 *
 * Two different things come back from the server and they must not be confused:
 *  - `GET /consent/agents` returns each stored consent's **floor** — what the user actually granted.
 *  - `GET /consent/agents/{clientId}/info` returns the agent's **ceiling** — the most it could ever
 *    be granted — decorated with connector display names, action descriptions and `highRisk` flags
 *    from the connector catalog.
 *
 * The Authorized Apps page must show the floor, because that is what is live; it only borrows the
 * ceiling's descriptors for wording. `decorateAuthority` is exactly that borrowing, and it is
 * deliberately tolerant: a missing descriptor falls back to the raw type/action name rather than
 * hiding a grant the user holds.
 */

/** Per-action policy as the server serializes it (`AuthorityJson.PolicyName`). */
export type ActionPolicy = 'auto' | 'ask' | 'deny';

/**
 * One element of an `authorization_details` array. `type` and `actions` are RFC 9396;
 * `action_policies` is this server's per-action auto/ask/deny extension; every other member is a
 * named constraint whose value is carried verbatim.
 */
export interface AuthorityGrantJson {
  type: string;
  actions?: string[];
  locations?: string[];
  action_policies?: Record<string, ActionPolicy>;
  [constraint: string]: unknown;
}

/** An action inside `GET /consent/agents/{clientId}/info`'s rendered ceiling. */
export interface AgentActionView {
  name: string;
  description?: string | null;
  highRisk: boolean;
  policy: ActionPolicy;
}

/** A connector inside `GET /consent/agents/{clientId}/info`'s rendered ceiling. */
export interface AgentConnectorView {
  type: string;
  displayName: string;
  description?: string | null;
  actions: AgentActionView[];
}

/** `GET /consent/agents/{clientId}/info`. */
export interface AgentConsentInfo {
  clientId: string;
  clientName?: string | null;
  description?: string | null;
  logoUri?: string | null;
  /** `delegated` | `service` | `both`. */
  mode: string;
  ceiling: AuthorityGrantJson[];
  /**
   * What the user has ALREADY granted this agent, absent when there is no standing consent.
   *
   * The consent screen pre-ticks from this. Without it the page fell back to ticking the whole ceiling,
   * so re-visiting after deliberately narrowing a grant showed the maximum as the default — and Allow
   * posts what is ticked, while the server REPLACES the stored floor rather than narrowing it. One click
   * silently restored the full ceiling.
   */
  granted?: AuthorityGrantJson[] | null;
  connectors: AgentConnectorView[];
}

/** One entry of `GET /consent/agents`. */
export interface AgentConsentListItem {
  clientId: string;
  clientName: string;
  authority: AuthorityGrantJson[];
  consentedAt: string;
}

/** `GET /consent/agents`. */
export interface AgentConsentListResponse {
  consents: AgentConsentListItem[];
}

/** Members of an authority element that are structure, not constraints. */
const STRUCTURAL_MEMBERS = new Set(['type', 'actions', 'action_policies']);

/**
 * Dress an authority set in the connector catalog's language.
 *
 * Iteration is over the AUTHORITY, never over the descriptors: the descriptors describe the ceiling,
 * and showing a ceiling entry the user did not grant would overstate what the agent may do. A type
 * or action with no descriptor still renders — under its raw name, which is unhelpful but honest.
 */
export function decorateAuthority(
  authority: AuthorityGrantJson[] | undefined,
  connectors: AgentConnectorView[] | undefined,
): AgentConnectorView[] {
  return (authority ?? []).map((grant) => {
    const descriptor = connectors?.find((c) => c.type === grant.type);
    return {
      type: grant.type,
      displayName: descriptor?.displayName || grant.type,
      description: descriptor?.description ?? null,
      actions: (grant.actions ?? []).map((name) => {
        const action = descriptor?.actions?.find((a) => a.name === name);
        return {
          name,
          description: action?.description ?? null,
          highRisk: action?.highRisk ?? false,
          // The consent's own policy wins: it is what the mint will enforce. The descriptor's is
          // only the ceiling's default, and a floor is allowed to have tightened it.
          policy: grant.action_policies?.[name] ?? action?.policy ?? 'auto',
        };
      }),
    };
  });
}

/**
 * The named constraints on a grant, as `name: value` strings — `locations` included, since it is
 * the member that says WHERE the authority applies.
 *
 * Constraint names are operator- and connector-defined, so there is nothing to translate them
 * against; they are shown as registered. Leaving them off the screen entirely would be worse — a
 * grant limited to one recipient domain reads very differently from an unlimited one.
 */
export function constraintSummary(grant: AuthorityGrantJson): string[] {
  return Object.entries(grant)
    .filter(([name]) => !STRUCTURAL_MEMBERS.has(name))
    .map(([name, value]) => `${name}: ${formatConstraint(value)}`);
}

function formatConstraint(value: unknown): string {
  if (Array.isArray(value)) return value.map((v) => formatConstraint(v)).join(', ');
  if (value === null || value === undefined) return '';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}

/**
 * Narrow a ceiling to the actions the user ticked, as an `authorization_details` array for
 * `POST /consent/agents`.
 *
 * The server stores `requested ∩ ceiling`, so nothing here can widen anything — the point of
 * sending the narrowed set rather than omitting `authority` altogether is that omitting it grants
 * the FULL ceiling. Grants left with no ticked action are dropped; every other member of the
 * element (locations, constraints) is carried through unchanged so a limit cannot be lost on the
 * way back.
 */
export function narrowCeiling(
  ceiling: AuthorityGrantJson[] | undefined,
  isSelected: (type: string, action: string) => boolean,
): AuthorityGrantJson[] {
  const result: AuthorityGrantJson[] = [];
  for (const grant of ceiling ?? []) {
    const actions = (grant.actions ?? []).filter((a) => isSelected(grant.type, a));
    if (actions.length === 0) continue;

    const narrowed: AuthorityGrantJson = { ...grant, actions };
    if (grant.action_policies) {
      narrowed.action_policies = Object.fromEntries(
        Object.entries(grant.action_policies).filter(([action]) => actions.includes(action)),
      );
    }
    result.push(narrowed);
  }
  return result;
}

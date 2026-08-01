# Agentic Auth

Authagonal ships the building blocks for delegating a user's authority to AI agents (or any
non-human workload) safely: registered agents, fine-grained authority grants, composite
delegation tokens, standing user consent, just-in-time approvals, capability tickets, and a
delegation-aware audit surface. The library owns the primitives and the invariant; the host
application assembles them into a product (connector implementations, approval UX,
notification delivery, and business policy stay host-side).

## The invariant

Every delegated token obeys:

```
effective authority = admin ceiling ∩ user consent ∩ task request ∩ subject-token authority
```

Nothing downstream can widen it — each further delegation hop intersects again, so authority
only ever narrows. The intersection is implemented once (`AuthoritySet.Intersect`) and used
everywhere.

## Entities

| Entity | Type | Notes |
|---|---|---|
| Agent | `AgentProfile` on a confidential `OAuthClient` | Registering a profile is what makes a client an agent; deleting it reverts the client to plain OAuth. |
| Authority | `AuthoritySet` / `AuthorityGrant` | RFC 9396 `authorization_details` shape: connector `type`, `actions`, `locations`, constraints, per-action `auto`/`ask`/`deny` policies. |
| Ceiling | `AgentProfile.Ceiling` | The widest authority any delegation through the agent can carry. Admin-managed (`/api/v1/agents`). |
| Consent (floor) | `PersistedGrant` type `agent_consent` | Per (user, agent), managed at `/consent/agents`. Stored pre-intersected with the ceiling and re-intersected at every mint. |
| Delegation | RFC 8693 token exchange | Composite identity: `sub` = user, `act` = agent (nested per hop), `authorization_details` = the effective intersection. Short-lived, never refreshable. |
| Approval | `PersistedGrant` type `approval` | JIT gate for `ask`-policy actions; device-flow polling semantics; single-use, request-shape-bound. |
| Capability ticket | `ICapabilityTicketService` | Opaque single-use handle bound to a token — the BFF ws-ticket generalized, atomic over the grant store. |
| Audit | `IAuthHook` | `OnDelegationMintedAsync`, `OnApprovalRequested/ResolvedAsync`, `OnAgentConsentChangedAsync`, `OnCapabilityTicketRedeemedAsync`, plus the pre-mint `OnTokenIssuingAsync` gate. |

## Registering an agent

1. Create a confidential client allowing `urn:ietf:params:oauth:grant-type:token-exchange`
   (delegated mode) and/or `client_credentials` (service mode).
2. `PUT /api/v1/agents/{clientId}`:

```json
{
  "mode": "delegated",
  "ceiling": [
    {
      "type": "email",
      "actions": ["send", "read"],
      "action_policies": { "send": "ask" },
      "recipient_domains": ["@acme.com", "*.partners.acme.com"]
    },
    { "type": "calendar", "actions": ["read"] }
  ],
  "maxDelegationDepth": 0,
  "maxTokenLifetimeSeconds": 300,
  "highRiskDefault": "ask"
}
```

Constraint members are typed by JSON shape: string/string-array → allowlist (set-intersection
meet; entries support exact, `*.host` wildcard, and `@suffix` matching), number → cap (min
meet), bool → gate (AND meet). Uninterpretable members are preserved verbatim and fail closed
on evaluation. `GET /api/v1/agents/{clientId}/effective-grant?subjectId=…` previews
ceiling ∩ consent for the admin UI.

## User consent (the floor)

- `GET /consent/agents/{clientId}/info` — the ceiling rendered against the connector catalog
  (register an `IConnectorCatalog` for display names, action descriptions, high-risk flags —
  its types are advertised in discovery as `authorization_details_types_supported`).
- `POST /consent/agents` `{ "clientId": …, "authority": […] }` — grants the floor (omit
  `authority` to consent to the full ceiling). A user can tighten a policy (`auto` → `ask`)
  but never loosen or widen — the store pre-intersects with the live ceiling.
- `GET /consent/agents` / `DELETE /consent/agents/{clientId}` — list and revoke. Revocation
  stops the next mint; outstanding delegations are refresh-less and expire within their
  (short) lifetime. No consent → the exchange fails `invalid_grant` /
  `consent_required` — the ceiling alone grants nothing.

## Minting a delegation

The agent authenticates as itself and exchanges the user's token:

```
POST /connect/token
grant_type=urn:ietf:params:oauth:grant-type:token-exchange
client_id=agent&client_secret=…            (or private_key_jwt, below)
subject_token={user access token}
subject_token_type=urn:ietf:params:oauth:token-type:access_token
authorization_details=[{"type":"email","actions":["read"]}]   (the task slice; omit = everything grantable)
```

The mint enforces, in order: agent mode, standing consent, sub-delegation depth (every actor
already in the `act` chain needs `maxDelegationDepth` budget for one more hop), the
intersection, explicit-request denials (`invalid_target` — an agent must not believe it holds
authority it lacks), the ask-gate, and the lifetime clamps (client lifetime ∩ subject-token
remainder ∩ `maxTokenLifetimeSeconds`). The token carries `act` (RFC 8693; nested per hop)
and `authorization_details` (RFC 9396); the response echoes the granted details; introspection
emits both. Exchanging a delegated token further attenuates automatically because the subject
token's own claim joins the intersection.

Clients **without** an agent profile keep today's exchange behavior exactly, except that an
`authorization_details` request parameter now narrows (never widens) the exchanged token.

## Approvals (ask-gate)

When the effective slice contains an `ask` action, the exchange parks:

```json
{ "error": "authorization_pending", "approval_id": "…", "interval": 5 }
```

The host is notified via `IAuthHook.OnApprovalRequestedAsync` (email/push/chat delivery is
host-side). The user resolves it — `GET /approvals`, `POST /approvals/{id}`
`{ "decision": "approve" | "deny" }` — while the agent retries the identical request plus
`approval_id`, with device-flow vocabulary throughout (`slow_down`, `access_denied`,
`expired_token`). Approvals are single-use (atomic consume), expire after
`ApprovalLifetimeSeconds` (default 300), and are bound to the exact request shape *and
current policy state* — an admin ceiling edit between park and poll invalidates the approval
rather than minting stale authority. A consumed approval mints with its `ask` actions
resolved to `auto` (asked and answered).

Service mode (`client_credentials`) has no user in the loop: the ceiling applies alone and
`ask` degrades to `deny`.

## Resource-side enforcement

- `AuthorityEvaluator.Permits(user, type, action, context, location, strict)` in any resource
  server (context keys are matched against constraint names — pass what you can derive,
  e.g. `recipient_domains` when sending mail). A token without the claim evaluates
  unrestricted (legacy compatibility); a garbled claim evaluates as deny-all.
  - `location` is the RFC 9396 `locations` value you are acting at. A grant that names
    locations is honoured only at those; a granted location is a **root**, so
    `https://api.example.com/orders` covers `/orders/17` but not `/orders-admin`.
  - `strict: true` denies when the caller supplied no context for a constraint, instead of
    skipping it. Use it wherever you can enumerate every key you support —
    `AuthoritySet.UncheckedConstraints(type, context)` names the ones you did not.
- BFF chokepoint: `BffUpstream.RequiredAuthority = ["email:send"]` makes the proxy check the
  outgoing bearer before forwarding — 403 on failure, no anonymous passage. The location it
  presents is the upstream the request will actually reach (`AuthorityLocation` overrides the
  root when authority is minted against a public identifier rather than the internal address);
  `StrictAuthority` makes the proxy refuse a constraint it cannot evaluate rather than leaving
  it to the upstream.

## Capability tickets

`ICapabilityTicketService` (default `GrantStoreCapabilityTicketService`, registered by
`AddAuthagonal`) mints opaque single-use handles bound to a token, redeemed atomically via
the grant store's conditional delete — durable and replay-safe across pods, unlike a plain
cache's get-then-remove. The BFF's ws-ticket keeps its existing distributed-cache contract
(`WsTicketKey` / `TryRedeemWsTicketAsync`) because its redeemer is typically a separate host
sharing only Redis; co-hosted brokers should prefer the capability-ticket service.

## private_key_jwt

Agents are workloads; shared secrets are the weakest link in the chain. Set
`OAuthClient.JwksJson` (inline JWKS) or `JwksUri` (fetched, cached ~10 min) and authenticate
with an RFC 7523 client assertion (`client_assertion_type=…:jwt-bearer`). Enforced:
signature against the registered JWKS, `iss` = `sub` = `client_id`, audience = issuer or
token endpoint, bounded `exp` (≤ 10 min), and single-use `jti` (replay cache over
`IRevokedTokenStore`). A present assertion never falls back to the secret path.

## Compatibility

- No agent profile → no behavior change on any flow. All new tables/columns are
  nullable-default and auto-provisioned on both storage providers (`AgentProfiles` table;
  `JwksJson`/`JwksUri` on clients; consents/approvals/tickets ride the existing grant table).
- New `IAuthHook` members are default-interface methods — existing hooks compile unchanged.
- `ITokenExchangeSubjectTransformer` still runs on every exchange and can reject or bind
  context claims; it can never widen the delegation (its output is re-intersected) or touch
  the `act` chain (reserved claim).

# Agentic Auth — Library Implementation Plan

Status: **implemented**, 2026-07-24 — all eight work packages landed in one pass; see
`docs/agentic-auth.md` for the usage documentation and the `[Unreleased]` CHANGELOG entry for
the shipped surface. This document is kept as the design rationale.
Companion design notes: admin configure-screen mockup and the entity/relationship diagram
(claude.ai artifacts `96e2faaf…` and `994d7d4f…`).

## Thesis

A BFF is a confidential broker: it holds the powerful long-lived credential, hands the
untrusted edge a short-lived scoped revocable handle, and sits on every call as the
policy/audit chokepoint. Agentic auth is the same shape with the edge = an AI agent, plus
three axes the browser case gets to skip:

1. **Delegation depth** — N-hop chains (user → agent → sub-agent), each hop attributable
   and only ever narrowing authority.
2. **Intent-scoped least privilege** — the edge is a non-deterministic planner, so tokens
   are scoped to the task, not the client's blanket grant.
3. **Ongoing per-capability consent** — recurring approvals with a human in the loop for
   high-risk actions, not one login.

The invariant every minted token obeys:

```
effective authority = admin ceiling ∩ user consent ∩ task scope ∩ time-box
```

Nothing downstream can widen it; every additional hop intersects again.

## Library boundary

Authagonal provides the **building blocks**; a host application assembles them into a
product. The line:

**The library owns:** agent registration and the ceiling model, the authority algebra and
its wire format, delegation minting (composite identity via token exchange), standing
agent consent, the pending-approval primitive and its protocol semantics, capability
tickets, revocation, delegation-aware audit hooks, and resource-side evaluation helpers.

**The host owns:** connector implementations and tool execution, the approval UX and its
notification channel (email/push/chat), spend metering and business policy (the library
carries the caps as claims and exposes the gate), admin/consent UI rendering, and the
agent runtime itself.

## What exists today (verified against source)

| Concept | Today | Status |
|---|---|---|
| User | `AuthUser` | in place |
| Agent | `OAuthClient` (confidential client) | extend |
| Connector / Action | `Scope` (flat string) + client `Audiences` (RFC 8707) | extend — no RAR/`authorization_details` anywhere |
| Ceiling (admin grant) | `AllowedScopes` + `IClientScopeGuard` | new structured model needed |
| Floor (user consent) | `PersistedGrant` `Type="consent"`, key `consent:{sub}:{client}`, checked in `AuthorizeEndpoint` | extend to exchange path |
| Delegation | **RFC 8693 token exchange is already implemented** (`TokenGrantHandlers.HandleTokenExchange`, `ProtocolTokenService.HandleTokenExchangeAsync`): downscope-only, no refresh token, lifetime capped at subject token's remainder, host seam `ITokenExchangeSubjectTransformer` | extend — `actor_token` is currently rejected (`TokenGrantHandlers.cs:89`), no `act` claim |
| Approval | device-flow pending/poll template (`authorization_pending`, `slow_down`); `IGrantStore.TryConsumeAsync`/`TryMarkConsumedAsync` atomic primitives | new |
| Capability ticket | BFF ws-ticket: single-use, 30 s TTL, optionally bound to an exchanged (downscoped) token via `TicketExchangeParams` | generalize |
| Audit | `IAuthHook` (`OnTokenIssuedAsync(subjectId, clientId, grantType)` gate before mint) | extend — no delegation context |
| Agent client auth | `client_secret_basic/_post` only | gap — no `private_key_jwt`/mTLS |

Key structural facts that shape the plan:

- Grant dispatch is a hardcoded `switch` in **two** token endpoints (Protocol and Server).
  Delegation rides the existing `TokenExchange` arm, so **no new grant type and no
  registry refactor is required**.
- Persistence is Table Storage + DynamoDB via store interfaces; **no EF, no migrations**.
  New entities follow the nullable-default + `EnsureTable` pattern in both providers.
- `OAuthClient` has no metadata bag — agent configuration gets its own entity rather
  than growing the client record.
- Token claim injection has a deliberate ungated path (`OidcSubject.AdditionalClaims`,
  used "where the claim is the whole point") — the natural carrier for the authority
  claim during mint.

## Design decisions

**D1 — Composite identity, never impersonation.** A delegated token names the user as
`sub` and the agent as `act` (RFC 8693 §4.1). Sub-delegation nests: `act: { sub: agent2,
act: { sub: agent1 } }`. Chain length is the delegation depth. The current explicit
rejection of `actor_token` is lifted only for this path.

**D2 — Delegation is token exchange; the actor is the authenticated client.** The common
case needs no `actor_token` parameter at all: the agent authenticates as itself (client
auth) and presents the user's token as `subject_token`; the actor is the requesting
client. `actor_token` is accepted for the sub-delegation case where the presented
subject token already carries an `act` chain.

**D3 — Authority is structured, not stringly.** Flat scopes stay for coarse OAuth
compatibility; fine-grained authority is an RFC 9396-shaped `authorization_details`
array. One typed model (`AuthorityGrant`) is used everywhere: the ceiling, the consent,
the request, and the token claim — so the invariant is literally one `Intersect` call.

**D4 — Approvals ride the exchange error channel.** A request hitting an `ask` policy
returns `authorization_pending` + `approval_id` (device-flow template). The agent
retries the same exchange with `approval_id=…`; the user resolves the approval
out-of-band. No new grant type; single-use consumption via the grant store's atomic
`TryMarkConsumedAsync`.

**D5 — Tickets become durable-atomic.** The generalized capability ticket uses
`IGrantStore.TryConsumeAsync` (ETag-conditional) instead of `IDistributedCache`
get-then-remove, which closes the documented ws-ticket replay window as a side effect.

**D6 — Service mode is client_credentials.** An agent acting as itself mints via the
existing `client_credentials` arm; its authority claim is the ceiling alone (no floor,
no `act`). Approvals don't apply — `ask` policies degrade to `deny` in service mode.

---

## Work packages

### WP1 — Authority algebra (`Authagonal.Core`)

The foundation everything else composes with. New namespace `Authagonal.Core.Authority`.

```csharp
public sealed record AuthorityGrant
{
    public required string Type { get; init; }          // connector id, e.g. "email", "mcp:tools.internal"
    public List<string> Actions { get; init; } = [];    // e.g. "send", "read"
    public List<string> Locations { get; init; } = [];  // audiences/resources (RFC 9396 locations)
    public Dictionary<string, ConstraintValue> Constraints { get; init; } = [];
    public Dictionary<string, ActionPolicy> ActionPolicies { get; init; } = []; // auto | ask | deny per action
}

public sealed record AuthoritySet(IReadOnlyList<AuthorityGrant> Grants)
{
    public AuthoritySet Intersect(AuthoritySet other);   // the invariant, as code
    public bool Permits(string type, string action, IReadOnlyDictionary<string, string> context);
}
```

Constraint meet semantics by value kind, extensible via a merger registry keyed on
constraint name (defaults by shape):

- string-set (allowlists: recipient domains, calendar ids, hidden fields) → set ∩
- numeric (booking window, spend cap, rate) → min
- boolean → AND
- `ActionPolicy` (`auto < ask < deny`) → most restrictive wins

Wire format: serialize `AuthoritySet` to/from the `authorization_details` claim and the
`authorization_details` request parameter (RFC 9396 shape; unknown fields preserved).
Ship `AuthorityEvaluator` as the resource-side helper: given a validated principal,
answer "may this token do action X on connector Y with parameters Z" — used by host
resource servers and by the BFF proxy (WP6).

Properties to test: intersect is commutative, associative, idempotent; result never
permits anything an input denied; unknown-constraint handling is fail-closed.

Also in WP1: connector/action *catalog* metadata. `Scope` grows nothing; instead a new
`ConnectorDescriptor` (id, display, actions with risk level and descriptions) via
`IConnectorCatalog` (config-backed default, store-backed optional) so consent screens
and admin UIs can render authority in plain language. Discovery advertises
`authorization_details_types_supported`.

### WP2 — Agent registration & the ceiling (`Core` + providers + `Server` admin)

An agent **is** a confidential `OAuthClient` (unchanged record) plus a new entity:

```csharp
public sealed record AgentProfile
{
    public required string ClientId { get; init; }
    public AgentMode Mode { get; init; }                 // Delegated | Service | Both
    public AuthoritySet Ceiling { get; init; }           // per-connector grants incl. ActionPolicies
    public int MaxDelegationDepth { get; init; } = 0;    // 0 = no sub-delegation
    public int MaxTokenLifetimeSeconds { get; init; } = 300;
    public ActionPolicy HighRiskDefault { get; init; } = ActionPolicy.Ask;
}
```

- `IAgentProfileStore` in Core; `TableAgentProfileStore` / `DynamoAgentProfileStore`
  with `EnsureTable` registration in both providers (nullable-default columns, JSON for
  the authority blob — same pattern as `ClientEntity`).
- Admin API endpoints (gated like the rest of `AdminApi`): CRUD on agent profiles, plus
  `GET /admin/agents/{clientId}/effective-grant` computing ceiling-∩-consent previews —
  this feeds the configure screen's live summary rail.
- Presence of an `AgentProfile` is what makes a client an agent; no flag on
  `OAuthClient`. The profile requires the client to allow the token-exchange grant
  (Delegated) and/or client_credentials (Service) — validated at upsert.

### WP3 — Composite delegation (`Authagonal.Protocol`)

All inside the existing token-exchange path; the two endpoint `switch`es are untouched.

1. `TokenGrantHandlers.HandleTokenExchange`: accept `actor_token` +
   `actor_token_type` (validation mirroring subject-token validation) instead of
   rejecting; still reject when the client has no `AgentProfile`.
2. `ProtocolTokenService.HandleTokenExchangeAsync`, after the existing scope/audience
   narrowing and before the host transformer:
   - Load `AgentProfile` for the authenticated client. No profile → current behavior
     (plain downscoping exchange), fully backward compatible.
   - Delegated mode: look up agent consent (WP4); missing/stale → `invalid_grant` with
     `error_description="consent_required"` (distinct, documented error the host login
     app can act on).
   - Parse requested `authorization_details` (absent → request the full remaining
     intersection).
   - Compute `effective = subjectAuthority ∩ ceiling ∩ consent ∩ requested`, where
     `subjectAuthority` is the subject token's own `authorization_details` claim when
     present (this is what makes sub-delegation attenuate for free) else ⊤.
   - Policy gate: any effective action resolving to `ask` without a consumed approval →
     WP5 pending path. Any resolving to `deny` requested explicitly → `invalid_target`.
   - Depth check: `act`-chain length + 1 ≤ min(`MaxDelegationDepth` over the chain).
   - Mint with: `act` claim (nested per RFC 8693), `authorization_details` claim =
     effective set, lifetime = min(existing clamps, `MaxTokenLifetimeSeconds`).
     Claim emission goes through first-class fields on `OidcSubject` mint context (not
     the ungated `AdditionalClaims` bag) and both claims join `ReservedClaimNames`.
3. `ITokenExchangeSubjectTransformer` continues to run **after** all of this — hosts can
   still bind extra context or reject; they can never widen (transformer output is
   re-intersected with `effective`).
4. Introspection endpoint: emit `act` and `authorization_details` for resource servers.
5. Revocation story: delegations are short-lived and refresh-less by the existing
   exchange rules; standing revocation = delete consent (WP4) and/or agent profile;
   immediate kill of outstanding tokens via the existing `IRevokedTokenStore` by `jti`.

### WP4 — Agent consent, the floor (`Authagonal.Server`)

Reuses the consent machinery with a new grant type and structured payload:

- `PersistedGrant` `Type="agent_consent"`, key `agent_consent:{subjectId}:{clientId}`,
  `Data` = serialized `AuthoritySet` (the floor) + per-action policy tightenings
  (a user may turn an admin `auto` into `ask`; never the reverse — enforced by
  intersect) + `ConsentedAt`.
- Endpoints alongside the existing consent set: `GET /consent/agents/{clientId}/info`
  (requested vs ceiling, rendered from the WP1 catalog), `POST /consent/agents`
  (persists floor, always pre-intersected with the current ceiling),
  `GET /consent/agents` (list standing agent consents for the current user),
  `DELETE /consent/agents/{clientId}` (revoke → subsequent exchanges fail with
  `consent_required`).
- Ceiling edits after consent: consent is stored as granted but **re-intersected with
  the live ceiling at every mint** (WP3 does this by construction), so an admin
  narrowing takes effect immediately without consent migration.

### WP5 — Approvals, the JIT gate (`Server` + `Core`)

The device-flow pending/poll pattern applied to exchange:

- `PersistedGrant` `Type="approval"`, key `approval:{id}`; `Data` = requesting client,
  subject, the exact effective-authority *slice* awaiting approval, a hash of the
  triggering request's parameters (an approval is valid only for the request shape it
  was minted for), resolution state. Short TTL (default 5 minutes, option-configurable).
- Mint path (WP3) on `ask`: create the pending approval, fire
  `OnApprovalRequestedAsync` (WP7 — the host's notification channel), return
  `authorization_pending` + `approval_id` + `interval`; `slow_down` on hot polling,
  mirroring the device-code handler.
- Agent retries the identical exchange with `approval_id`. Handler validates the
  request-hash match, then consumes via `TryMarkConsumedAsync` (atomic, single-use) and
  proceeds to mint exactly the approved slice. Denied → `access_denied`; expired →
  `expired_token` (device-flow vocabulary throughout).
- User-facing endpoints: `GET /approvals` (pending for the current session's user, with
  catalog-rendered descriptions), `POST /approvals/{id}` (`approve` | `deny`). The
  library serves the data and the state machine; the host renders the UI and delivers
  the notification.

### WP6 — Capability tickets (`Core` + `Bff`)

Generalize the ws-ticket into a first-class primitive:

- `ICapabilityTicketService` in Core: `MintAsync(boundToken, AuthoritySet? narrowing,
  TimeSpan ttl, int uses = 1)` → opaque handle; `TryRedeemAsync(handle)` → bound token +
  authority, atomically consumed. Backed by `IGrantStore` (`Type="capability_ticket"`,
  `TryConsumeAsync`) per D5 — durable, atomic, pod-restart-safe.
- `Authagonal.Bff` refactors `WsTicketAsync`/`TryRedeemWsTicketAsync` onto the service
  (same endpoint contract and `WsTicketKey` compatibility shim for existing redeemers,
  one release of overlap, removal noted in CHANGELOG).
- BFF exchange routes grow per-route required-authority declarations: an upstream route
  can declare `{ type: "email", action: "send" }`, and the proxy runs `AuthorityEvaluator`
  against the injected token before forwarding — making the BFF the enforcement
  chokepoint for hosts that don't touch their resource servers.

### WP7 — Delegation-aware audit (`Core` + call sites)

Extend `IAuthHook` with default interface methods (no breaking change for existing
implementations):

- `OnDelegationMintedAsync(DelegationAudit)` — subject, full actor chain, effective
  authority, lifetime, approval id if any. Fired post-mint in the exchange path.
- `OnApprovalRequestedAsync(ApprovalAudit)` / `OnApprovalResolvedAsync(ApprovalAudit)`.
- `OnAgentConsentChangedAsync(subjectId, clientId, granted|revoked)`.
- `OnCapabilityTicketRedeemedAsync(ticketId, boundSubject, actorChain)`.
- A richer pre-mint gate `OnTokenIssuingAsync(TokenIssuanceContext)` (client, subject,
  grant type, scopes, requested authority) with a no-op default — the existing
  three-argument `OnTokenIssuedAsync` stays and keeps firing.

Every event carries the actor chain, so a host can answer "which agent, as which user,
did what, when, approved by whom" from hooks alone.

### WP8 — Agent client authentication hardening (parallel track)

Agents are workloads; shared secrets are the weakest link in the chain. In
`ClientAuthentication.cs` + client model:

- `private_key_jwt` (`client_assertion` / `client_assertion_type` per RFC 7523):
  new `OAuthClient.JwksJson` / `JwksUri` fields (nullable-default, both providers),
  assertion validation with `jti` replay cache via the grant store.
- Discovery: `token_endpoint_auth_methods_supported` updated.
- DPoP sender-constraint on delegation tokens is noted as the follow-on once RFC 9449
  is prioritized; not in this plan's critical path.

---

## Persistence summary

New tables (all nullable-default, auto-provisioned via `EnsureTable` in both the Azure
and AWS providers; no migration framework exists or is needed):

| Entity | Store | Notes |
|---|---|---|
| `AgentProfile` | `IAgentProfileStore` | authority blob as JSON column |
| agent consent | existing `IGrantStore` | new `Type="agent_consent"` |
| approval | existing `IGrantStore` | new `Type="approval"`, atomic consume |
| capability ticket | existing `IGrantStore` | new `Type="capability_ticket"` |
| client JWKS (WP8) | on `ClientEntity` | nullable columns |

Three of five ride the existing grant table — the store interfaces already have the
atomic primitives the runtime flows need.

## Sequencing

1. **M1 — WP1 + WP2**: authority algebra, wire format, catalog, agent profiles, admin
   CRUD. Shippable alone: hosts get structured downscoping on the *existing* exchange
   via `authorization_details`, even before delegation lands.
2. **M2 — WP3 + WP4**: composite delegation with the full invariant and standing
   consent. The core of agentic auth; end-to-end demo: user token → agent exchange →
   attenuated composite token → introspection shows `act` + authority.
3. **M3 — WP5**: approvals. Depends on M2's policy gate.
4. **M4 — WP6 + WP7**: capability tickets on the durable store, BFF enforcement
   routes, audit surface.
5. **WP8** runs parallel to M2–M4.

Testing per milestone: WP1 property tests (never-widen); an end-to-end delegation-chain
test in `Authagonal.Tests` (mirroring the existing archetype-login e2e style): register
agent → consent → exchange → sub-delegate → verify depth cap, attenuation, `act` chain,
audit events; replay tests for approvals and tickets (double-redeem must fail on both
providers' stores).

## Non-goals (host concerns, out of library scope)

- Connector implementations, tool execution, MCP transport.
- Approval notification delivery and UI (the library fires the hook and serves the
  state; the host owns the channel and the screen).
- Spend metering and business-rule policy engines — the library carries caps as
  constraints in the token and exposes the pre-mint gate; counting dollars is not an
  auth-server concern. (`IRateLimiter` remains available to hosts that want broker-side
  throttling.)
- Agent orchestration/runtime, prompt-level safety, model choice.

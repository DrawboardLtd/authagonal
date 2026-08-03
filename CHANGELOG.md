# Changelog

## [Unreleased]

### Breaking

- **`IMfaStore` gains `TryUpgradeRecoverySecretAsync`.** Source-breaking for an external implementer of the
  interface; every bundled backend (Azure Table, SQL, DynamoDB) implements it, so a deployment using one of
  those is unaffected.

  It rewrites a recovery credential's protected secret **only while that credential is still unconsumed**,
  and returns `false` — skip the rewrite — when it is gone, already consumed, or was written by someone else
  first. The recovery-verification path upgrades every legacy unsalted-SHA-256 digest it touches, for the
  user's whole set, from a snapshot taken *before* verification. Persisting that snapshot with an
  unconditional upsert put `IsConsumed` back to `false` for any code a concurrent request had spent in the
  meantime, so a single-use bypass of the entire second factor was usable twice — reopening the race
  `TryConsumeRecoveryCodeAsync` exists to close, from inside the same request handler.

  There is deliberately no unconditional "update the secret" on the interface: the guard is the whole point.
  Implement it with your backend's conditional-write primitive (ETag, version compare-and-set, conditional
  expression) and return `false` on a lost race rather than retrying. It must not stamp `LastUsedAt` — the
  upgrade sweeps the user's whole recovery set, so stamping marks every code as used because one of them was.

- **`IMfaStore` gains `TryRecordWebAuthnUseAsync` and `TryActivateCredentialAsync`.** Source-breaking for an
  external implementer; every bundled backend (Azure Table, SQL, DynamoDB) implements both, so a deployment
  using one of those is unaffected.

  Both are conditional writes that replace a blind `UpdateCredentialAsync` of a snapshot, and both must
  return `false` rather than inserting when the credential is gone — an unconditional upsert re-creates a
  row an administrator revoked mid-request, and the handler then completes the sign-in it was revoking.
  `TryRecordWebAuthnUseAsync` raises the sign counter and stamps `LastUsedAt` only while the stored counter
  has not already passed the value; implement the comparison as `<=`, not `<`, because a sign count of zero
  means the authenticator implements no counter (WebAuthn §6.1.1) and reports zero forever, so a strict
  increase would refuse every assertion from that hardware. `TryActivateCredentialAsync` writes the name and
  nothing else: a conditional write that carries fields it does not own is a blind write wearing a guard.

- **`/connect/*` now returns HTTP 400 `invalid_request` with `"TLS is required at the OAuth endpoints"`.**
  If your deployment started answering that after upgrading, this is why, and there are two one-line fixes.

  RFC 6749 §3.1 and §3.2 require TLS at the authorization and token endpoints. Nothing enforced it before:
  a plaintext exchange handed anyone on the network path the authorization code, the client secret in the
  `Authorization: Basic` header, and the access and refresh tokens that came back. `/connect/authorize`,
  `/connect/token`, `/connect/userinfo`, `/connect/par`, `/connect/revocation`, `/connect/introspect`,
  `/connect/endsession`, `/connect/deviceauthorization` and `/connect/register` now refuse a non-https
  request. `/health`, the internal endpoints, the admin and SCIM APIs, discovery and JWKS are **not** gated.

  The scheme is read *after* forwarded-header processing, so **the cause is almost always that the scheme
  never reached the server**, not that TLS is missing:

  - **You terminate TLS at a proxy or ingress but never call `UseForwardedHeaders`.** The request arrives at
    Kestrel as plain http and there is nothing to correct it. `Authagonal.Server`'s `UseAuthagonal()` calls
    it for you; a host that builds its own pipeline (including one embedding `Authagonal.Protocol` directly)
    must call it itself, and needs it anyway for `Secure` cookies and correct absolute URLs.
  - **Your proxy has not been declared.** `X-Forwarded-Proto` is honoured only from a proxy named in
    `ForwardedHeaders:KnownProxies` / `ForwardedHeaders:KnownNetworks`. Declaring it is the fix, and an
    undeclared proxy's scheme claim is a claim any caller could have made.

    A private address is **not** a declaration. The fallback trust set (loopback + RFC1918, see the
    `/_internal/backchannel-logout` fix below) adjusts the **client IP** only — it is a guess about
    topology, and this library ships on nuget.org and cannot see the network it was deployed onto. On a
    flat LAN, a shared VPC or a shared container bridge, every neighbouring workload holds a private
    address and could assert `https` over a request that arrived in cleartext. A best-effort client IP
    still beats the framework's empty-set behaviour of believing every caller; a best-effort *scheme*
    would be a security gate resting on an inference, so it is not offered.

    If your proxy has no fixed address — a Kubernetes ingress, a rotating load balancer, a platform that
    will not tell you the hop's CIDR — declare `ForwardedHeaders:KnownNetworks: ["0.0.0.0/0", "::/0"]`.
    That states the assumption you are already relying on (nothing but the proxy can reach this process)
    somewhere it can be reviewed, instead of leaving the library to infer it from an address range.
  - **You genuinely serve the protocol surface over plain http** — a laptop, a demo, a test host. Set
    `Auth:AllowInsecureHttp` (env `Auth__AllowInsecureHttp=true`). The server logs a warning at startup
    whenever it is on. Never set it in production. For a host that embeds `Authagonal.Protocol` without
    `Authagonal.Server`, the equivalent is `AuthagonalProtocolOptions.AllowInsecureHttp`; when you use
    `AddAuthagonal()` the config key is propagated for you, so one switch governs the whole surface.

  `Authagonal.Protocol` embedders are affected even though they compose their own pipeline. In
  `Authagonal.Server` the gate is middleware over the whole `/connect/*` prefix; in `Authagonal.Protocol` it
  is a filter on the four routes that package owns — `/connect/authorize`, `/connect/token`,
  `/connect/userinfo`, `/connect/par` — attached where each route is declared, precisely so it cannot be
  lost by a host that never had a middleware slot for it. Mapping an endpoint individually rather than
  through `MapAuthagonalProtocolEndpoints` does not opt out of it.

  The shipped `docker-compose.yml`, the demo compose file and the custom-server demo all serve http and all
  set the opt-in explicitly. `dotnet run --project src/Authagonal.Server` now defaults to the `https` launch
  profile; the `http` profile carries the opt-in.

- **Server-initiated outbound fetches no longer use the ambient HTTP proxy, and refuse internal addresses
  at the socket.** Two config keys exist for the deployments this affects: `Auth:AllowedInternalTargets` and
  `Auth:AllowOutboundProxy`.

  The outbound SSRF guard now resolves the target and refuses every private, loopback, link-local and
  otherwise internal ADDRESS at connect time, on every redirect hop (see the Security section below for
  why a URL check cannot do this). Two consequences an operator can see:

  - **An internal destination you fetch on purpose is now refused.** An IdP reachable only over a private
    network, a provisioning app in the same cluster. List it in `Auth:AllowedInternalTargets`:
    `idp.corp.internal` (that host and whatever it resolves to), `*.corp.internal` (any host under the
    suffix), `10.4.0.0/16` or `10.4.1.7` (that network or address, under any name). It applies to the
    upstream SAML metadata fetch, upstream OIDC discovery — including the `token`, `userinfo` and `jwks_uri`
    endpoints that document names — and provisioning callbacks: the paths where the URL came from *your*
    configuration or admin API. It deliberately does **not** reach a client-registered `jwks_uri` or
    back-channel logout URI, where an internal host is never a deployment, so widening a federation target
    cannot also open the cloud metadata service to an anonymous `/connect/token` request. A malformed CIDR
    entry fails at startup rather than silently permitting nothing.

  - **If your egress requires a proxy, those fetches now fail.** The guard is attached to
    `SocketsHttpHandler.ConnectCallback`, and with a proxy in effect .NET invokes that callback with the
    *proxy's* endpoint and never the target's — so the check would pass on the proxy every time and permit
    everything. It would fail open in precisely the networks most likely to have a proxy, so the clients
    that carry the guard set `UseProxy = false`. `Auth:AllowOutboundProxy` sends the operator-configured
    fetches (SAML metadata, OIDC discovery, provisioning callbacks) back through the proxy, accepting that
    only the URL check remains for them. It does **not** reach the client `jwks_uri` fetch or back-channel
    logout delivery: those targets are registrant-chosen and reachable from anonymous requests, so there is
    no switch. A network that must proxy those needs an SSRF-filtering egress gateway, not a bypass.
    `UseAuthagonal()` logs a warning at startup when it finds `HTTPS_PROXY`/`HTTP_PROXY`/`ALL_PROXY` set,
    because otherwise the symptom is "SSO stopped working" with nothing pointing at the cause.

  The BFF's two clients (`AuthagonalBff`, `AuthagonalBffProxy`) are **not** guarded and use the proxy as
  before. `BffUpstream.TargetBaseUrl` is host configuration whose own documented example is
  `https://api.internal.acme.com`, the token client talks to the authority that host configured, and
  `BffProxy` already refuses any composed target that left the configured upstream authority — so a caller
  cannot steer those requests and there is nothing for an address check to add. Email delivery (`Resend`)
  is likewise unguarded and unaffected: its target is a compile-time constant.

### Security — the two sign-out paths disagreed about what "logged out" means

- **`POST /api/auth/logout` notified no relying party and revoked no grant (high).** Its entire body was an
  upstream-session cleanup and a cookie `SignOutAsync`. It built no Logout Token, rendered no front-channel URI,
  and never called `RemoveBySubjectAsync(subjectId, PersistedGrantTypes.SessionBound, …)`.
  `/connect/endsession` did all three.

  This is the path that matters most, because it is the one the OP's own login app's Sign out button invokes —
  so for most users, signing out left every relying party believing they were still present, and left the
  refresh tokens, authorization codes, device codes and pushed-authorization requests minted for that session
  in the store and usable. Meanwhile discovery advertises `backchannel_logout_supported`,
  `frontchannel_logout_supported` and both `*_session_supported` flags unconditionally, and
  `docs/configuration.md` promises "a signed logout token (JWT) to each client's registered URI" — describing
  the path that did it and not the one users take.

  The collect-notify-revoke block is now `SessionTermination`, called by both. Extracted rather than duplicated
  for the reason this review keeps rediscovering: a rule implemented twice is a rule about to be fixed once.
  Every hardening already in that block now applies to both paths by construction — the outbound-URL check on
  both URI kinds (loopback permitted for the browser-side iframe fetch, refused for the server-side POST), the
  two-minute token lifetime, the explicit `TokenType` so a logout token cannot be presented as a subject token,
  and session-bound-only revocation so signing out does not silently discard the user's stored consents.

  `POST /api/auth/logout` now returns `frontchannel_logout_uris` for the caller to load, because a front-channel
  logout is by definition performed by the user's browser and a JSON endpoint has no page to render them into.

### Security — an agent could switch off its own approval gate

- **A client-supplied `action_policies` entry counted as an administrator's decision (high).**
  `AuthoritySet.MergeSameType` records a policy for an action when the meet is not `Auto` **or** when either
  operand carried an explicit entry — deliberately, because `ApplyHighRiskDefaultsAsync` reads *absence* as
  "apply the profile default", so an administrator who marks a high-risk action `auto` must not have that
  decision erased.

  But in a delegated exchange the operands include the client's own `authorization_details` request, and nothing
  filtered `action_policies` out of it: `AuthorityJson.TryParse` treats the member as first-class, which is
  exactly why `FindUngrantedConstraint` — the guard that stops a client contributing members the ceiling never
  defined — never inspected it. So an agent put `"action_policies": {"transfer": "auto"}` in its own request,
  that was recorded as an explicit decision, and the high-risk default skipped the action: the human-approval
  gate on the riskiest actions, switched off by the party it exists to gate.

  `Intersect` now takes policy provenance. A non-authoritative operand — the request, or a subject token's own
  claim — may still **raise** a policy (`Auto` → `Ask` → `Deny`), because that only narrows; it cannot create
  the explicit-entry marker that suppresses the default.

### Security — four more highs: an MFA bypass, an unauthenticated logout endpoint, a spendable challenge, a resettable throttle

- **`/_internal/backchannel-logout` treated loopback as a credential (high).** With `Cluster:Secret` unset — the
  shipped default — the guard authorised on a loopback source address, and loopback is exactly what this
  project's own **mandatory** deployment shape produces: the installation and configuration docs require a
  TLS-terminating reverse proxy in front of Authagonal, and a proxy on the same host (`proxy_pass
  http://127.0.0.1:8080`, IIS/ANCM, `network_mode: host`) connects to Kestrel *from* 127.0.0.1. The raw-peer
  capture faithfully records that real peer, so the guard saw 127.0.0.1 for every request the proxy forwarded,
  internet-originated ones included. The route is anonymous, antiforgery-disabled, and revokes every grant for
  an arbitrary subject — so unauthenticated mass session revocation for any user, plus a probe for whether a
  subject had sessions at all. The earlier fix stopped trusting forwarded headers and stopped accepting RFC1918
  on the reasoning that "a source address is not a credential", then kept one source address as a credential.

  It now fails closed: no `Cluster:Secret` means 404 for everyone, with `Cluster:AllowLoopbackWithoutSecret` as
  an explicit development opt-in and a startup diagnostic naming whichever state applies. Nothing in the product
  calls these endpoints, so the closed default breaks no shipped flow — pod-to-pod callers are on different
  addresses and already needed the secret.

- **`MfaPolicy.Required` was never enforced at `/connect/authorize` (high).** It was evaluated in exactly two
  places, the password-login handler and the federated MFA flow, and both derived the client — and therefore
  the policy — from the `returnUrl` **query parameter**. Absent or naming another client, the policy fell back
  to Disabled and a full session cookie was signed with no second factor. The authorize endpoint had a step-up
  guard, but it keyed only on `MfaEnabled: true` and never read `client.MfaPolicy`. So the enrolled case was
  covered regardless of `returnUrl`, and the enrolment-forcing case — the documented "force enrollment for
  users without MFA" — rested entirely on an attacker-controllable value: an unenrolled user reached a Required
  client with a password-only session by arriving without the parameter. Now enforced at the authorize endpoint,
  where the client comes from the validated request rather than a query string, with `interaction_required`
  under `prompt=none`.

- **An MFA challenge could be spent six times over (high).** `TableMfaStore.ConsumeChallengeAsync` read the
  entity then issued an unconditional delete, and the Azure SDK documents that overload as "should not fail
  because the entity does not exist" — so every concurrent caller that got past its own read also got a
  successful delete and the challenge back. Eight concurrent consumes returned **six** non-null. That is the
  anti-replay guard on the second factor. SQL and DynamoDB were already atomic.

  Worth recording precisely, because the obvious fix is wrong: a *conditional* delete does not close it either.
  Azurite answers a conditional delete of an already-deleted row with success, so the delete's status cannot be
  the arbiter — while a stale-ETag delete against a row that still exists **is** refused with 412, so the
  precondition itself works. The claim is now a compare-and-set `UpdateEntityAsync` marking the challenge
  consumed, which every loser fails with 412 because the row is still there to compare against; the delete
  follows as best-effort cleanup. Eight concurrent consumes now return one.

- **Every security throttle on a node could be reset remotely (high).** `InProcessRateLimiter` is one
  process-wide dictionary with a hard entry cap, and at the cap it evicted the oldest tenth **ordered by window
  Start** — while eviction hands the evicted key a fresh budget. Window lengths differ by call site, so a
  one-hour password-reset-per-IP window that began five minutes ago sorted older than a one-minute SAML-ACS
  window created a second ago, with 55 minutes still to run: a flood of short-lived keys evicted the long
  security windows and kept itself. The flood was reachable unauthenticated, because `POST
  /saml/{connectionId}/acs` built its limiter key from the raw route segment *before* looking the connection up.
  Eviction now prefers windows closest to lapsing naturally, so an actively-held limit is the last thing
  dropped, and the ACS handler resolves the connection first — bounding the key space to connections that exist.

### Security — two more places a write reverted someone else's decision

- **`PUT /api/v1/clients/{id}` reset every omitted field to the model default (high).** The handler bound a
  whole `OAuthClient` and wrote it, so any JSON property the body left out arrived at its declared default and
  was persisted: `Enabled` back to **true**, `MfaPolicy` to Disabled, `RequireConsent` and
  `RequirePushedAuthorizationRequests` to false, `JwksJson`/`JwksUri` to null (killing private_key_jwt), and
  every URI, scope and origin list to empty. Exactly two fields were special-cased — `ClientSecretHashes` and
  `AudiencesDeclared`, the latter with a comment explaining this very hazard — and about forty were not.

  No attacker required: an operator disables a compromised client to cut it off, and the next rename, logo-URL
  change, admin-console save that posts only the fields it renders, or scripted bulk update turns it back on and
  removes the MFA requirement from its users. Nothing in the response or the audit row said anything had changed.

  Absent and explicitly-default are indistinguishable once JSON is bound to a non-nullable record, so the merge
  happens at the node level, before binding — which also means no per-property list to fall out of date, that
  being the failure mode it replaces. PUT is now a merge rather than a replace, the same choice
  `ClientSeedService` makes and for the same reason. The format validators are each gated on their own input
  having been supplied, so a client whose STORED hash or redirect URI predates the validator that now rejects it
  is still editable — otherwise the fix would have made those clients unmaintainable, with an error naming a
  field the operator never sent.

- **The Azure store left the security stamp in cleartext (high).** `TableUserStore` encrypts a hard-coded
  column list, and `SecurityStamp`, `PendingPasswordHash` and `RolesJson` were not on it. The SQL and AWS
  providers encrypt the whole serialized document, so the shared provider-parity tests could never have caught
  it, and the existing PII coverage asserted only the Tier-1 fields.

  The stamp is not ordinary PII. The email-confirmation / passwordless-claim token is
  `base64("{stamp}||{email}||{exp}[||pc=…]")` and carries **no MAC** — the confirm-email handler's own comment
  says the stamp is the only thing authorising the state change. So the adversary `IFieldCipher` exists to stop
  (read access to the store, no Vault key: a leaked read-only role, a copied backup, a snapshot, a support
  export) could mint one: set `EmailConfirmed` on any account without ever holding the mailbox — the gate JIT
  federation and `email_verified` rely on — and complete a staged self-service claim on a passwordless
  SSO/SCIM-provisioned account. `RolesJson` is authorization state, useful for choosing whom to target.

  `ExternalId`, `OrganizationId` and `Locale` stay in cleartext deliberately: the first two are query and index
  keys, which would need blind indexes as `Email` has, and the third is a language tag. Legacy plaintext rows
  keep reading and are re-written encrypted on their owner's next profile write.

### Security — a deleted account's identity and credentials did not die with it

- **A deleted account's second factors authenticated the next account at that id (high).**
  `IMfaStore.DeleteAllCredentialsAsync` had exactly one caller in the whole product — the admin MFA-reset
  endpoint. Neither `DELETE /api/v1/profile/{userId}` nor SCIM `DELETE /scim/v2/Users/{id}` touched the MFA
  store, so a deleted account's TOTP secret, recovery-code hashes, WebAuthn public keys and WebAuthn
  credential-id index rows all survived, keyed on the user id.

  The id comes back two ways: the admin create endpoint accepts a caller-supplied `UserId`, and the SCIM create
  path reclaimed a tombstoned row. The reclaim asserted in a comment that "nothing the deleted account held
  survives" — false for the second factor, and `POST /api/auth/mfa/passwordless/complete` never consults
  `MfaEnabled`: it resolves the account from the credential. So an attacker who had enrolled a passkey kept a
  way in across the exact remedy an incident responder reaches for — delete the account, re-create it — with no
  password, no email and no session. `AccountArtefactPurge` now removes credentials and group memberships on
  every delete path, and again on reclaim for rows tombstoned by an earlier version.

- **SCIM group membership survived the delete too (high, no attacker required).** `DeleteUserAsync` never
  removed the departing user from any group, and `RetainOwnedMembersAsync` deliberately *keeps* tombstoned
  members because they still carry the owning client id. An offboarded address re-issued months later to a
  different person therefore inherited every group the departed employee still occupied, including role-mapped
  ones — so the new user silently received roles no administrator granted, with no record of the assignment.

- **A re-provisioned address inherited the departed user's OIDC `sub` (high).** The reclaim kept the tombstoned
  row's `id`, which RFC 7643 §3.1 requires to be "a stable, non-reassignable identifier" and which this server
  uses as the subject. At every relying party the new person *was* the old one — their documents, their
  permissions, their audit identity — with nothing in the IdP recording that the human had changed. A reclaim
  now deletes the tombstone (which releases the email-index entry it owned) and creates a fresh resource with a
  new id. Nothing asserted the reuse, which is why it survived: the existing re-creation test checked only that
  the create succeeded.

- **One wrong password permanently destroyed a DynamoDB account's password hash (high).** `ReadUserAsync`
  treated the presence of the single `failedCount` marker as proof that the entire promoted login-state group
  was authoritative, including `pwd` — and both login stamps CREATE `failedCount` on a row that lacks it while
  writing no `pwd`: the failed-login stamp materialises the group deliberately, and the success stamp carries
  `pwd` only on a rehash. So one login attempt published `PasswordHash = null`, and the login path forces a
  failed verify whenever the stored hash is empty. The account could never log in again, and forgot-password
  issues no reset for an account with no local password, so self-service recovery was closed as well —
  unauthenticated, one request per victim, permanent. The user's own *correct* password was equally sufficient
  to trigger it.

  Rows arrive without the promoted group from a restore, which writes raw rows, so this lands precisely when an
  operator can least absorb it. `SqlUserStore` refuses the partial stamp with a comment naming this exact
  failure mode; the Dynamo store never learned it. The overlay now consults each attribute individually, which
  also **repairs** rows already damaged rather than only preventing new damage, and the failed-login stamp
  completes the group instead of half-writing it.

### Breaking — `IClientStore` gains `TryUpgradeSecretHashAsync`

Not source-breaking: the default returns `false`, so an external implementer keeps compiling and simply leaves
legacy client-secret hashes on their legacy format (which `LegacySecretHashWarning` already reports).

It rewrites ONE entry of `ClientSecretHashes` to a stronger format, and only while the stored entry is still the
one that was verified and nothing else about the record has changed. The upgrade previously re-read the client,
mutated the hash list, and wrote the WHOLE record back with an unconditional upsert, comparing only the entry at
`index` as it stood at the re-read — so any administrative write landing between that read and the write was
reverted wholesale: the hash list (undoing a secret rotation, so a rotated-out secret kept working), `Enabled`
(re-enabling a client an admin had just disabled), scopes, redirect URIs. The losing write was always the
administrator's, and the attacker did not need to observe the rotation: the throttle permits 30 authentications
a minute per client, which is enough to hold the window open.

Implement it with your backend's conditional primitive and return `false` on a lost race rather than retrying —
authentication has already succeeded, so a skipped upgrade costs nothing but a repeat attempt next call. Azure
Table implements it with ETag CAS over the whole row. The SQL and DynamoDB client stores do **not**: their rows
carry no version attribute, so they stay on the default and leave legacy hashes in place. That is strictly safer
than the unconditional write they did before, but it is not parity.

### Security — the client-secret verification path

- **A correct credential answered HTTP 500 on any tenant-scoped host (high).**
  `PasswordHasherClientSecretVerifier` is registered `TryAddSingleton`, so the provider it holds is the ROOT
  provider — and `GetService<IClientStore>()` against the root throws for a store registered `AddScoped`, which
  is how every multi-tenant host registers it. The resolution sat *outside* the `try`, so the exception escaped
  `VerifyAsync` after the presented secret had already verified: correct `client_credentials` answered 500
  instead of a token, on `/connect/token`, `/par`, `/introspect`, `/revocation` and `/deviceauthorization`. It
  was permanent (the upgrade could never complete, so every retry repeated it) and triggered by the legitimate
  credential rather than by an attacker. This is the defect `LegacySecretHashWarning` had — it stopped
  tenant-scoped hosts from starting until it resolved inside a scope — reintroduced one file over. Nothing in
  the suite registered the store as scoped, which is why it shipped; now something does.

- **An unbounded bcrypt cost factor was an anonymous, uncancellable CPU burn (high).** Every other
  imported-cost lever was bounded and bcrypt had none: recognition was a four-character prefix test, so
  `$2a$31$…` passed the admin API's hash validator and reached `BCrypt.Verify`. Cost is a base-2 exponent and
  the library accepts up to 31 — 2^31 key expansions, pinning one thread-pool thread per request for days. Any
  unauthenticated caller who knows the `client_id` triggers it; a few dozen requests take the provider down for
  every tenant. Bounded at cost 15 now, checked at the write *and* at verification, so a hash stored before the
  bound existed fails closed instead of burning the CPU.

- **A malformed bcrypt hash was a permanent 500 for that principal (medium).** Both verifiers caught only
  `SaltParseException`, and against the pinned library `$2a$1` and `$2b$` throw `IndexOutOfRangeException`,
  `$2a$xy$…` throws `FormatException`, and `$2a$11$short` throws `ArgumentOutOfRangeException`. None was caught,
  so every authentication for that client — including its own legitimate one — returned 500 forever, with a
  stack trace to an anonymous caller wherever developer exception pages are on. The validator's own doc comment
  described fixing exactly this for the empty-string case while the bcrypt-shaped variants passed. Now
  structurally validated, and the catch is broad because a stored hash that cannot be parsed must never fault a
  request.

  `BcryptHashFormat` in Core is the single rule, shared by the Protocol package's `BCryptClientSecretVerifier`
  and the Server host's `PasswordHasher`. They had the identical defect and could not see each other, which is
  how a fix to one would have left the other.

- **The number of secret hashes was unbounded (medium).** The format of each entry was validated and the count
  was not, while the verifier derives every entry on each failed authentication with no early exit. Work per
  request is `count × per-hash cost` with `count` caller-chosen: 1,000 entries at the permitted 1,000,000
  PBKDF2 iterations is minutes of thread-pinning CPU per anonymous token request, 30 of which are allowed per
  minute per client. Capped at 8 — well past what rotation needs.

- **`Auth:Pbkdf2Iterations` above 1,000,000 silently wrote unverifiable hashes (high).** The write path was
  uncapped and the verify path capped, at different values: `VerifyPbkdf2V2` refuses a recorded count above
  1,000,000, correctly, since that value drives an uncancellable derivation reachable from an anonymous
  request — but the bound applied to this server's own fresh hashes too. Only a floor was validated at startup,
  so `Auth__Pbkdf2Iterations=2000000` (or a fat-fingered `6000000` for the documented 600,000) came up healthy
  and then wrote hashes it rejects on read: those users could never log in and each attempt incremented the
  lockout counter, and every DCR-issued and seeded client secret became permanently `invalid_client`. Fixing
  the configuration afterwards does not repair it — the cost lives in each stored blob. A validated ceiling now
  makes it a refusal to start, naming the value.

### Security — the retention path: what a rollup may read, what it must sign, what a scoped restore may delete

Three defects with one shape — a check that existed on one path and not on its sibling.

- **A scoped restore executed other envs' deletes (high).** `RestoreService` learned to scope the data apply
  to `CleanEnvPrefix`; `ApplyTombstonesAsync` was left unscoped. `BackupTombstonesAsync` filters the change log
  on `Timestamp` alone — `BackupOptions` has no env filter — so on a shared sandbox table set the
  `_tombstones` file holds every env's deletes, with the `{env}|` prefix intact in the `OrigPK` column it
  writes as `PartitionKey`. An operator restoring `sandbox-1` by the documented procedure therefore deleted
  rows from `sandbox-2..N`, and unlike `MergeService.IsDeleted` this path has no recreate or timestamp guard,
  so rows a sibling created *after* the backup went too — unrecoverable, because they are not in the archive
  being restored. Deleting a revocation record also resurrects a revoked token. Now scoped, with the declined
  deletes reported as `RestoreResult.TombstonesSkippedOtherEnv` rather than silently executed.

- **The rollup wrote an unsigned manifest (high).** `MergeToTargetAsync` had no manifest key and never called
  `ManifestAuthentication.Sign`, so the retained copy was the one that could not be authenticated — while
  `RollupAndCleanAsync` deletes the signed full and incrementals. The operator's only route back was
  `AllowUnauthenticatedManifest`, which downgrades the archive to hashes sitting in a plain JSON file beside
  the data on the same target: exactly the circularity `ManifestMac` exists to remove. Every rollup test in the
  suite passed `AllowUnauthenticatedManifest = true`, which is what made the gap invisible.

- **The rollup verified nothing about what it read (high).** It consulted `FileHashes` for no input and hashed
  its output fresh, so the new manifest vouched for whatever the merge happened to find. Archives are plaintext
  JSONL by default: an attacker with write access edits `Users.jsonl` in yesterday's full, leaves the manifest
  alone, and the next scheduled rollup writes the tampered bytes into a new snapshot with correct hashes over
  them — then deletes the original. Verification is now unconditional wherever the input's own manifest records
  a hash, and needs no key, so the cloud host (which signs `_manifest.sig` with Vault Transit and recomputes
  the hashes from whatever is on the target before signing **those**) is covered with no caller change. A file
  the manifest lists but the store no longer holds is an error rather than an empty read. `VerifiedRead` is now
  shared by both readers, because a check only one of them performs is the defect this class keeps producing.

- **Three tables were missing from the backup set (high).** `BackupDefaults.Tables` describes itself as "all
  Authagonal data tables" and omitted `AgentProfiles`, `UserRoles` and `UpstreamRefreshTokens` — the last
  already named in `SecretBearingTables` as though it were in the archive, which is how the omission hid.
  None of the absences was fail-safe. `AgentProfiles` holds the agent's authority ceiling, mode, delegation
  depth and high-risk default, and every gate that enforces them lives inside `if (agentProfile is not null)`;
  so after any rebuild-from-backup the agent client authenticated with its restored secret and its exchange
  took the unprofiled path — no consent requirement, no ceiling intersection, no approval parking, no depth
  budget, no `act` chain for audit to see. Closed as a class: a convention test compares the set against the
  tables the provider actually creates, and every exclusion is named with its reason.

### Security — federated account squatting, closed at the step that actually mattered

- **Adoption inherited the squatter's login binding (high).** Both hosts gated JIT and adoption on whether the
  connection owns the email's domain. That is a real check and it never fires during the attack:

  1. The attacker configures a connection they operate, `AllowedDomains` left empty so nothing restricts the
     address it may assert, JIT on.
  2. They federate once asserting `ceo@acme.com` with `email_verified: true` — a claim chosen by whoever runs
     the upstream, which here is them. The unverified-email gate passes. The domain-routing gate passes too,
     because Acme has not onboarded and the domain has no row. An account is minted bearing that address with
     `EmailConfirmed = true` and the attacker's `(provider, subject)` binding attached.
  3. Acme onboards. Their connection genuinely *is* the authority for `acme.com`, so on the real user's first
     login every gate agrees and the account is adopted — carrying the squatter's binding with it. The
     attacker signs in through their own connection and is the CEO.

  The unasked question was never "does this connection own the domain" but "who else can already sign in to
  this account". `FederationAdoptionPolicy` asks it, for both hosts: a connection that is the established
  authority for the domain (routing table, or an administrator's `AllowedDomains`) **evicts** foreign
  connection bindings instead of inheriting them; a connection that is not the authority is refused rather
  than allowed to evict a rival's binding.

  Eviction rather than refusal, deliberately — refusing would hand an attacker a permanent denial of service
  over any address they squatted first. Social logins (`google`, `github`, …) are never evicted: they are not
  connection-scoped, nobody can make Google assert an address they do not control, and removing a user's own
  social login because their employer later onboarded SSO would be a self-inflicted lockout.

  Comments on both hosts asserted this was already closed. They have been corrected, since a comment claiming
  a gate closes an attack it cannot see is how this survived two rounds of review.

### Security — an authority that permitted nothing minted a token that permitted everything

- **`authorization_details: []` evaluated as UNRESTRICTED at every resource server (critical).** Five links,
  each defensible alone:

  1. `ConstraintValue.Meet` collapses two disjoint string sets to an empty `StringSet`, and *any* kind
     mismatch to `Nothing`.
  2. `AuthoritySet.MergeSameType` stored that met value and left `Actions` populated, so `Intersect` kept the
     grant. It already dropped a grant with disjoint `locations`; the constraint case was missed.
  3. Every emptiness guard on the mint path counted `Grants`, which still held that grant — and
     `PolicyFor` reported its actions grantable, because it does not consult constraints at all.
  4. `AuthorityJson.ToNode` then dropped the grant, correctly: emitting a positive grant carrying a
     non-standard denial marker reads as *permitted* to any spec-conforming resource server. Dropping the last
     grant leaves `[]`, and `!string.IsNullOrEmpty("[]")` is true, so the claim was written.
  5. A JWT-to-ClaimsPrincipal conversion flattens an array claim into one claim per element, so an empty array
     yields **zero** claims — and `AuthorityEvaluator.FromPrincipal` reads zero claims as `Unrestricted`,
     deliberately, so that coarse scope-based tokens keep working.

  So the narrowest possible grant produced the broadest possible token. Link 5 is not a bug and has not
  changed; it is why `[]` must never be minted, and why omitting the claim instead would be no safer.

  Reachable with one request — an agent naming `"max_amount": "unlimited"` against a ceiling that caps it
  numerically was enough. The sharpest path needs no attacker input at all: in unattended
  `client_credentials` mode `ask` degrades to deny, so an agent whose ceiling is *entirely* approval-gated —
  the most careful configuration an admin can write — had every grant dropped and received an unrestricted
  token from its own valid credentials. That path had no emptiness guard of any kind.

  Fixed in three layers, each independently sufficient for the paths it covers: a constraint that meets to
  nothing now drops the grant in `Intersect` exactly as disjoint locations do (restoring the never-widen
  invariant for in-process consumers too); both mint paths ask `AuthorityJson.SerializesToNothing` — the
  question put to the **wire form**, since serialization is where the authority is lost — and refuse with
  `invalid_authorization_details` / `unauthorized_client`; and the single mint choke point throws rather than
  sign an empty array, for the next caller that forgets.

### Fixed — the admin API was unreachable, and admin-secret rotation was a silent no-op

- **Configuration may seed the administrative scope again.** Reserving `authagonal-admin` against *every*
  write path — including the two config seeders — locked deployments out of their own admin API.
  `docs/admin-api.md` ("Bootstrapping the first admin token") documents a config-seeded
  `client_credentials` client carrying that scope as the only way to mint an admin token, and the
  `IdentityAdmin` policy admits nothing else; with the seeders refusing that descriptor, a fresh install
  could never reach `/api/v1/*` at all.

  The second consequence was worse than the first, because it looked like success. The seeders log at
  `Error` and **skip** the descriptor, so an operator rotating a suspected-compromised admin secret — "a
  config change + restart", per that same page — wrote no new hash, and the credential they believed they
  had revoked went on authenticating indefinitely. Every signal they could check agreed the rotation had
  worked, including the admin API still answering, because it was still answering to the old secret.

  The reservation is real and unchanged on every path a *caller* can reach: the admin API, dynamic client
  registration and `POST /api/v1/token` all still answer `403 forbidden_scope`. Configuration is the trust
  root rather than a caller — whoever writes the `Clients:` section can already set `AdminApi:Scope`,
  replace the signing keys or repoint the store, so refusing it there bought nothing. A scope entry
  containing whitespace is still refused everywhere, on its own merit: one stored entry that becomes two
  scopes on the wire means the client does not say what it appears to say.

  Two tests asserted the lockout as intended behaviour, which is why it shipped — and the only coverage of
  the admin surface writes its client straight into `IClientStore`, bypassing the seeder, so the documented
  bootstrap was never exercised end to end. It is now, through a real host.

### Security — audit remainder

An independent audit of the info-severity fixes found ten of twenty-eight only partially closed. Two
(`azp` multi-audience, the login timing oracle) were closed immediately; these are the rest, plus one
adjacent defect the audit found while checking a finding it had already passed.

- **SAML Single Logout was a forced-logout gadget.** The NameID binding only compared when a NameID was
  present, so a `LogoutRequest` carrying none skipped the comparison entirely and signed the browser out.
  Unsigned requests are accepted when the session belongs to the connection, `/saml/{id}/slo` is an
  anonymous GET, and every request carries a fresh ID so the replay cache never fires — which made
  `<img src="…/slo?SAMLRequest=…">` on any page a repeatable logout of every visitor on that connection.
  An absent or encrypted identifier is now a refusal (Core §3.7.1 makes it mandatory), the subject
  comparison is unconditional, and a session the connection did not establish — including any local
  password session, which has no `saml_name_id` — is no longer that IdP's to end. `NotOnOrAfter` is now
  honoured when the IdP states one, and SP-initiated logout state no longer shares a key namespace with
  pending AuthnRequest IDs.

- **Published SAML metadata contradicted the login path.** `AuthnRequestsSigned` was computed from
  `SignAuthnRequests` alone, while the login path signs when there is an SP key AND (the connection forces
  it OR the IdP's own metadata declares `WantAuthnRequestsSigned`). A connection with `SignAuthnRequests`
  unset, talking to an IdP that wants signed requests, signed every AuthnRequest while publishing
  `AuthnRequestsSigned="false"` — to the administrator configuring against that document. Both logout legs
  also silently sent unsigned when the SP private key would not load; they now refuse, as the login path
  already did.

- **`bff-lib` pinned no signature algorithms.** The .NET BFF pinned its inbound `id_token` and logout-token
  algorithms; the TypeScript package did not, on either call. jose refuses to resolve a key for `HS*` or
  `none`, so this was a guarantee borrowed from a dependency rather than held by this code — the same
  reasoning the .NET pin was added under. Both now pin the identical asymmetric set. The .NET logout
  consumer also now sets `RequireSignedTokens` and `ValidateIssuerSigningKey` explicitly, which the
  callback set and it inherited weaker defaults for.

- **The provisioning-app quota re-count was eventually consistent on DynamoDB.** The cap is enforced by
  counting after the write; `DynamoProvisioningAppStore.GetAllAsync` omitted `consistentRead`, so two
  concurrent creates could each write and each count against a replica that had seen neither, keeping both
  rows and exceeding a paid boundary exactly as before the fix. Now strongly consistent, matching every
  other correctness-critical read in that provider. The compensating delete is audit-logged.

- **`RestoreMode.Clean` scoped the wipe but not the import.** `CleanEnvPrefix` bounded which rows were
  deleted and then applied the data file wholesale — and `BackupOptions` has no env filter, so a backup
  from a shared sandbox table set contains every env's rows and restoring one env wrote all its siblings
  back in. The apply is now scoped to the same prefix and reports what it skipped. Clean with no prefix
  refuses rather than printing a warning to `Console.Error` that no host process surfaces;
  `AllowCleanAllEnvs` is the explicit opt-out for a genuinely single-env table set.

- **The repeated-parameter check never saw the query string on the PAR leg.** Once a `request_uri` is
  present the parameter source becomes the pushed payload, so `?request_uri=A&request_uri=B` resolved
  first-wins, unrefused and unlogged — while the proxy, WAF or log pipeline in front of the server parsed
  the same query its own way. The query is now scanned on both legs, in both hosts.

- **The security stamp was compared with an ordinal compare on a third path.**
  `GET /api/auth/confirm-email` is anonymous and unthrottled, and the stamp is the whole of the
  authorisation on a token that carries no MAC. Two sites were fixed when this was first reported; this one
  post-dated that sweep. A convention test now covers the class rather than the instance, since no
  behavioural test can distinguish a constant-time compare from a short-circuiting one.

- **The Server host's userinfo never required the `openid` scope** — the defect its Protocol-host twin was
  fixed for. The comment above the validation parameters asserted the requirement; nothing enforced it, so
  a pure-OAuth access token read the endpoint and got `sub` back. Per-claim scope gates kept the residue to
  subject disclosure rather than PII.

### Security

- **Every outbound SSRF guard was a text check, and a hostname is not text the attacker has to be honest
  about.** `logout.attacker.test` passes every literal-address and internal-suffix rule and then answers
  with `169.254.169.254`. The guards ran where a URL was accepted — an admin write, a dynamic client
  registration — and the name was resolved later, by something else, on behalf of whoever owns it.

  Resolving at the write would not have fixed it, in two directions at once: an attacker can answer
  truthfully at registration and differently at delivery, and pinning what was resolved then goes stale for
  entirely ordinary reasons (failover, autoscaling, a CDN, a reclaimed address), at which point this server
  would be POSTing logout tokens carrying `sub` and `sid` to whoever inherited the address.

  So the check now runs at the socket, per connection: resolve, refuse **every** returned address that is
  internal — not merely the first, because a name answering with one public and one private address is the
  rebinding attack compressed into a single response — and then connect to an address that was actually
  checked rather than handing the name back to the socket, which would reopen the window between the two
  lookups. Because a redirect is a new connection, it re-runs on every hop for free.

  The URL checks stay. One refuses a bad URL early, where the error is attributable to whoever typed it; the
  other refuses a bad address late, where no lie about DNS can help. Which clients carry the socket check,
  and what a proxy does to it, is a per-client decision documented in the Breaking section above and pinned
  by tests: it belongs on a target a *registrant* chose and needs an operator escape hatch on a target the
  *operator* chose.

- **The origin guard covered four routes, not the whole interactive surface — and this file said otherwise.**
  The device-grant entry above originally asserted the device endpoints were the only interactive POSTs
  missing `InteractiveOriginGuard`. A diff-scoped review pass found that was false. Also unguarded, and all
  cookie-authenticated with antiforgery disabled: **`POST /consent`** — the primary OAuth consent grant, which
  writes the grant *and* the offered-scope set that suppresses future prompts — `DELETE /consent/grants/{id}`,
  `DELETE /consent/agents/{id}`, `POST /api/auth/logout`, `PATCH /api/auth/profile`, both session-revocation
  routes, and the entire `/api/auth/mfa/*` setup group.

  The sharpest is `POST /api/auth/mfa/recovery/generate`. It binds **no request body**, so a
  `fetch(url, {method:'POST', credentials:'include'})` sends no `Content-Type` and is a *CORS-simple* request:
  the browser issues no preflight and the request **executes whatever the CORS policy says** — only the
  response is withheld. It deletes the victim's recovery codes and issues ten new ones. Destroying the
  recovery path is the damage even unread; with a credentialed CORS grant the attacker reads the codes, and
  each satisfies `/api/auth/mfa/verify`.

  The guard is now an endpoint filter (`RequireOwnOrigin`) applied to groups rather than a call to remember,
  and a convention test fails the build on any cookie-authenticated state-changing route without it — with
  anonymous and client-authenticated routes listed explicitly, and reasons. `DELETE /consent/grants/{id}` was
  confirmed live before the fix: the cross-origin DELETE returned `204 No Content`.

- **`AllowedCorsOrigins` granted a credentialed cross-origin policy on every path, and the shipped image
  seeded it.** Client-registered origins were correctly scoped to `/connect/` and `/.well-known/`; the
  operator's list was returned verbatim for every path, with `AllowCredentials` — so the exact sink that
  scoping exists to protect, `POST /api/auth/mfa/recovery/generate` returning plaintext recovery codes, stayed
  readable by any listed origin. And `src/Authagonal.Server/appsettings.json` shipped
  `"AllowedCorsOrigins": ["http://localhost:3000"]`, which `.dockerignore` re-admits into the published
  image — so a deployment that never set the key granted a credentialed origin to anything the victim's
  browser reached on port 3000.

  `AllowCredentials` is now granted per path and never on `/api/auth/`, `/api/v1/`, `/scim/`, `/consent` or
  `/approvals`; those surfaces are first-party and need no CORS grant. The shipped default is empty, with the
  localhost origin moved to `appsettings.Development.json`, which is excluded from the image.

- **#205 — the highest-impact admin writes left no audit record.** Four groups produced no row at all: MFA
  reset and credential removal (stripping a second factor is the first thing an attacker with an admin
  credential does), `POST /api/v1/token` (which mints a token AS another user — the strongest impersonation
  primitive in the product, on a subject who never agreed to it), nine writes on the user admin endpoints
  including set-password, unlock, delete, confirm-email and identity linking, and the provisioning "test this
  app" probe (which makes the server send a request carrying that app's API key and returns the response body
  to the caller). Some logged a `Warning`, which is not a trail: the shipped log configuration decides whether
  it survives, nothing indexes it by subject, and there is nowhere to query. All now record an attributable
  row, and a convention test fails the build on the next admin write that does not.

- **#192 — a `workflow_dispatch` free-text input was interpolated into a shell script.**
  `rebuild-images.yml` did `ref="${{ inputs.ref }}"`, which substitutes before bash parses, so a value like
  `v1.0.0"; curl … | sh; "` would run on a runner holding the registry credentials. It now travels through the
  environment. The sibling workflows had been fixed; this one was missed. Every workflow was swept for the
  same shape.

- **#288 — the load-test harness defaulted to a literal client secret and a live host.** `tests/load/helpers.js`
  fell back to `"load-test-secret"` and to the public demo deployment's URL, so an unparameterised run pointed
  a load generator at a real service and authenticated to it with a credential published in a public
  repository. The secret is now required with no default, and the base URL defaults to localhost.

- **The Server host's `Clients:*:Audiences` seed setting is new, and both seeders now apply one policy.**
  There are two seeder classes because `ProtocolSeedService` ships in `Authagonal.Protocol`, which is embedded
  without `Authagonal.Server` — so the classes stay two and the rule became one (`ClientSeedPolicy`). Both now
  refuse the administrative scope, a scope entry that is not a single token, and an audience list that is not
  within the documented caps and absolute; both mark a client as having declared its audiences when it names
  any. Previously the audience half existed in one seeder and not the other, and the Server host's had no
  audiences field at all, which made configuration the one write path that could still put an unbounded or
  non-absolute value into a signed token's `aud`.

  A malformed audiences list is now logged at `Error` and that client is SKIPPED, rather than throwing and
  taking the host down — matching what both seeders already did for the two scope rules.

- **`/connect/userinfo` answered the same request two different ways depending on which host served it.** A
  valid access token without the `openid` scope got `403 insufficient_scope` from `Authagonal.Protocol` and
  `401 invalid_token` from `Authagonal.Server`, because the Server host resolved the subject and looked up the
  user BEFORE checking the scope — and a `client_credentials` token has no subject at all. `invalid_token`
  tells a client its token is bad, so a conforming client refreshes, receives a token with the same scopes,
  and loops. The scope check now runs first on both, which also stops a user-store read for a token that was
  never entitled to the endpoint.

- **An undeclared scope on `client_credentials` answered `invalid_grant` instead of `invalid_scope`.** RFC 6749
  §5.2 distinguishes them for a reason: `invalid_grant` says the grant is bad, `invalid_scope` says the grant
  is fine but the client asked for something it does not hold, and only the second tells the client what to
  change. The code was derived by string-matching the exception message and only the token-exchange handler
  carried the match, so its sibling fell through to the generic answer. Scope rejections now carry the code
  explicitly. A test had asserted `invalid_grant` and thereby pinned it.

- **`LegacySecretHashWarning` prevented any multi-tenant host from starting.** It took `IClientStore` as a
  constructor dependency, and a hosted service is constructed from the ROOT provider during
  `Host.StartAsync` — so on a host whose stores are resolved per tenant-scoped request the construction threw
  and the process never came up. Its own try/catch could not help, because the failure happened before
  `StartAsync` was entered. It now resolves the store inside a scope, the shape `PlaintextSigningKeyWarning`
  and `UseAuthagonal`'s email-sender diagnostic already used, so an unavailable store costs the diagnostic
  and nothing else.

- **The cluster rate-limit sweep could not keep up with what it was cleaning, and gave up at the first
  throttle.** Only 404 and 412 were handled per row, so any other `RequestFailedException` — including the 429
  the very flood being swept produces on that table — escaped and abandoned the whole pass until the next
  tick: the one condition under which retention matters stopped the sweep. Deletes were also issued one round
  trip at a time against a table an anonymous endpoint fills at request rate. Now the expiry predicate is
  applied server-side, deletes run 16 at a time, a failing row is counted and skipped, and the interval is a
  minute rather than five. A row carrying no `ExpiresAt` is no longer collected — the store writes that
  property on every path, so no writer produces one.

- **A rate-limit bucket key built from caller input could fail forever, and the fail-open needed no outage
  to reach.** Keys reached the backend verbatim as a partition key, and Azure Table forbids `/ \ # ?` and
  control characters there — so `POST /saml/a%23b/acs` produced a key the store rejected with 400 on every
  attempt, permanently, each one logged at Error and allowed through. Keys are now sanitised, with a hash of
  the original appended whenever the rewrite changed anything so two sources cannot be folded into one budget.

- **Turning on `Auth:DurableRateLimiting` silently changed which keys share a budget.** The in-process
  limiter matched keys case-INsensitively while every durable backend matches ordinally, so two keys differing
  only in case were one budget before the switch and two after it. Nothing was exploitable at the current call
  sites, but that made their safety an accident of a comparer two layers away. The in-process limiter is now
  ordinal, matching the backends; callers that want case-folding do it at the call site.

- **A rate-limit budget could silently reset itself.** Bucket retention was two windows, which for the
  five-second poll limiters is five seconds of tolerance for clock skew between the sweeping leader and the
  incrementing node — ordinary drift for pods without tight NTP. The leader collected live buckets and the
  budget restarted from zero with no error and no log line. Retention now has a ten-minute floor whatever the
  window, and the Azure store logs a warning if it ever has to recreate a swept-away bucket anyway.

- **Seeded clients could not declare their audiences, and the Server host's seeder had no audiences field at
  all.** `ResourceAudiencePolicy` reads a client that has declared none as the legacy "may name any absolute
  URI" case, and configuration was the one write path that could neither set the flag nor run the validator —
  so a config-seeded client kept the permissive reading with no way to tighten it, and operator config was the
  only route left for putting an unbounded or non-absolute value into a signed token's `aud`. Both seeders now
  validate the list and mark it declared, and `Clients:*:Audiences` exists. The protocol seeder also no longer
  overwrites a stored list with an empty one, which used to revert an admin PUT on every restart while the
  monotonic declared-flag survived — leaving a client that declares audiences and has none, able to name no
  resource at all.

- **A cancelled outbound connect was reported as a connection failure**, retried against every remaining
  address on an already-cancelled token first; an empty DNS answer was reported as the SSRF refusal, sending
  operators chasing a firewall rule for a resolution failure; and the `allowLoopback` parameter's
  documentation named a caller that does not exist and does not use the class. All three corrected.

- **`WWW-Authenticate` escaping did not hold the guarantee its comment claimed.** The challenge helpers
  replaced only the double quote, under a comment saying escaping "keeps that true of the next one somebody
  adds". A description ending in a backslash escapes the closing quote (RFC 9110 §5.6.4 quoted-pair) and a
  CR/LF is refused by Kestrel's header validation, so a refusal would have answered 500. Every caller passes a
  fixed literal today; the escaping now covers both cases, so the next one can rely on it.

- **A failed device denial left the device polling until expiry.** The `user_code` is consumed before the
  denial is recorded — deliberately, so a late deny cannot erase a consumed marker — but a failure between the
  two destroyed the code without recording anything, and nobody could retry either the deny or the approve.
  The device then polled `authorization_pending` for its full remaining lifetime, which is the outcome the
  endpoint exists to prevent. A failed denial now consumes the device code instead, so the device gets a
  terminal error.

- **Two more sites in the confirm-email rebase.** The provisioning-rejection path still resolved its row
  through `FindByEmailAsync` — the attacker-supplied half of the token, through the mutable email index — so
  if the address moved during the round-trip and another account came to own it, that OTHER user had their
  staged credential dropped and their security stamp rotated, signing out all their sessions. And
  `EmailConfirmed = true` was stamped onto whatever address the re-read row held, though the token proved only
  one; an address changed mid-flight was vouched for, and `/connect/userinfo` then asserted
  `email_verified=true` for it. Both now use the stable key and refuse a mismatch.

- **The socket SSRF guard omitted 100.64.0.0/10, 198.18.0.0/15 and IPv6 `fec0::/10`.** The resolved-address
  check is the last line of defence — no URL check runs against a resolved address — so whatever it omits is
  the bypass list. RFC 6598 shared address space is not exotic: `100.100.100.200` is the Alibaba Cloud
  metadata service, `100.64.0.0/10` is the pod/service CIDR on EKS clusters using secondary CIDRs, and it is
  the whole of Tailscale's range. A registrant-supplied `jwks_uri` or back-channel logout URI on a hostname
  resolving into any of those reached internal services. All three ranges are now refused.

- **Every outbound call failed permanently on a host with IPv6 disabled.** The socket was constructed
  *outside* the try block in the connect callback, so `socket(AF_INET6)` returning `EAFNOSUPPORT` — a host or
  container booted with `ipv6.disable=1` — escaped the candidate loop instead of falling through to the next
  validated address. DNS still returns the AAAA record on such a host and RFC 6724 ordering puts it first, so
  every request to every dual-stack host died on the first candidate while a reachable A record sat next in
  the list. .NET's own connect path, which this callback replaces, falls through; this was a regression
  introduced with the guard, not inherited behaviour. Cancellation now also stops the loop rather than being
  retried against every remaining address.

- **`TurnstileVerifier` ran on the framework default handler.** 100-second timeout and up to 50 automatic
  redirects, on a client reached from anonymous `POST /login` and `/register` that fails closed — so a stalled
  connection to Cloudflare held a request slot for 100 seconds *and* rejected the login, which is exactly the
  amplifier the 10-second timeouts elsewhere were added to remove. It is a *typed* client, registered through
  a different overload, which is how it escaped a hardening test that enumerated only the named ones. Now
  bounded and non-redirecting, and in that test's list.

- **Token exchange kept its own copy of the resource rule, and the copy was the defective one.** Two in-code
  comments asserted all three sites read `ResourceAudiencePolicy`; grep found two. The exchange path still
  used the bare `Uri.TryCreate(…, UriKind.Absolute)` check that the shared policy exists to replace — and on
  Unix that *succeeds* for a bare path, because the runtime infers a `file:` scheme. A client whose stored
  `Audiences` held a bare path (a row predating the policy, or one a config seeder wrote without it) could
  exchange with `resource=/admin` and receive a tenant-signed token whose `aud` was `/admin`. The RFC 8693
  `audience` list got no shape check at all, only membership. Both now go through the policy.

  Fixing that surfaced a second, pre-existing defect: `TokenGrantHandlers` derived the OAuth error *code* by
  string-matching the exception message (`"Resource '"`), and the shared policy's message begins lowercase —
  so the `client_credentials` path had already been silently answering `invalid_grant` where RFC 8707
  requires `invalid_target`, with no test noticing. Both paths now throw a typed `ProtocolTokenException`, so
  the code no longer depends on prose.

- **A slow rate-limit counter store took logins down.** `DurableRateLimiter`'s documented posture — a store
  failure must not take authentication with it — only ever covered a store that *throws*. There was no
  timeout, and the login handler makes two of these checks on the hot path, so multi-second store latency
  blocked every login for twice that while the fail-open never fired, because nothing had failed. The store
  call is now bounded at two seconds, and exceeding it takes the same fail-open path as an unreachable store.

- **A deleted account could be resurrected by a confirmation in flight.** `ConfirmEmailAsync` re-reads the
  row after its provisioning round-trip and previously fell back to `?? user` — the pre-round-trip instance —
  and `GetAsync` returns null only when the row is *gone*. So a delete landing during that round-trip (SCIM
  deprovision, admin delete, GDPR erasure) made the handler write the stale instance, and `UpdateAsync`
  CREATES the row when absent on all three persistent providers: the erased account came back with
  `EmailConfirmed = true` and, on the claim path, the claimant's promoted password — no concurrency guard
  consulted, nothing logged. Erasing an account while its owner had a verification link open un-erased it.
  The handler now writes nothing and reports the link as no longer valid.

- **A passwordless claim's staged profile was destroyed rather than applied.** The same rebase applied the
  claimed first/last name and custom attributes to the in-memory user before the round-trip, then carried
  only the credential fields onto the re-read row while nulling `PendingClaimJson` in the same breath — so
  the claimed profile was unrecoverable afterwards. The failure path already reloaded a clean copy "so none
  of the staged profile persists", which is only meaningful if the success path persists it. It now does.

- **The device grant's approve and deny endpoints had no cross-origin protection.** Both are
  cookie-authenticated with antiforgery disabled, and `SameSite=Lax` withholds the session cookie from a
  cross-*site* request but not from a same-site cross-*origin* one — so on the ordinary `idp.acme.com` beside
  `app.acme.com` deployment, any XSS or hostile script on a sibling origin could `fetch()` these endpoints
  with `credentials:'include'` and a `user_code` of its choosing. Approve grants the attacker's device in the
  victim's name and records a consent grant; deny cancels the victim's own device authorizations. They are
  the third consent-granting interactive POST in the server and were — as first claimed here, wrongly —
  "the only ones missing" `InteractiveOriginGuard` — the approvals and agent-consent endpoints have carried it since it was written,
  for exactly this threat. Both now check it, ahead of their rate limiter so a cross-origin script cannot
  spend the victim's approval budget on its way to being refused.

- **The cluster-wide rate limiter failed open under the load it exists to bound, on Azure.** With
  `Auth:DurableRateLimiting` on and the Azure backend, the increment is an ETag compare-and-set retried
  against a single row — the most contended row in the system by design, since every request against one
  budget targets it. Exhausting the retry budget threw an ordinary store exception, and `DurableRateLimiter`
  treats a store it cannot reach as an outage and allows the request. So contention read as unavailability:
  raising concurrency *lifted* the bound instead of tripping it, and an attacker grinding a device
  `user_code` or a client secret with enough parallel connections had the excess waved through un-counted.
  Azure Table also throttles a hot single-entity partition with 429/503, neither of which was retried, so the
  first throttled response took the same path.

  Three changes. Retries are now spaced with full-jitter backoff, so losers stop colliding on the same tick;
  429/503 and the transient server-side classes are retried rather than propagated; and exhausting the budget
  raises a distinct `RateLimitContentionException` that the limiter fails **closed** on. The fail-open posture
  for a genuinely unreachable store is unchanged and deliberate — refusing every request would turn a storage
  blip into a total authentication outage — which is precisely why the two cases had to be told apart. Note
  that hundreds of simultaneous callers on one budget still cannot all settle (a single-row CAS admits one
  winner per round trip, whatever the backoff); they are now refused, which is the limiter working. DynamoDB
  (`ADD`) and SQL (single-statement upsert) settle in one statement and never had this path. The Azure
  counter store also had no tests; it does now.

- **A revoked second factor could be resurrected by the request that was being revoked.** Three MFA paths
  finished by writing a whole credential row back from a snapshot read earlier in the handler:
  TOTP enrolment confirm, WebAuthn during MFA verification, and passwordless passkey sign-in.
  `UpdateCredentialAsync` is an unconditional upsert on every provider, so if an administrator called
  `DeleteAllCredentialsAsync` in between, the write re-created the deleted credential — and each handler
  then went on to set `MfaEnabled` and issue a session cookie. The TOTP path had a second defect in the
  same line: it persisted the snapshot's `LastTotpStep`, which is the value from *before* the conditional
  step claim above it, so it could move the step back past a concurrent verification and return a spent
  code to play for the rest of its window. The WebAuthn paths had the analogous one — the sign counter is
  clone detection and must only rise, and a captured value could move it down past a concurrent
  assertion's. All three now go through conditional store operations that refuse rather than insert.

  The comment above the TOTP write asserted that carrying the claimed step made it safe. It is worth
  recording that a fix's own comment is not evidence: this was the third time in this review that a defect
  and its cause arrived in the same commit.

- **On the Azure provider, no non-live environment enforced the MFA attempt cap or challenge anti-replay.**
  `MfaChallengeEntity.ToModel` set `ChallengeId` from `PartitionKey`, and the stored partition key is
  `EnvPartitioner.PK(challengeId)` — so a challenge read back on an environment named anything other than
  live carried `env|challengeId`. Both things the verify handler does with that value re-apply the prefix:
  the failed-attempt increment landed at `env|env|challengeId`, a row nothing reads, so the five-attempt
  ceiling on a 10^6 TOTP space never fired; and `ConsumeChallengeAsync` deleted that same phantom and left
  the real challenge in place, so a challenge stayed redeemable for its full lifetime after a successful
  verification. `MfaCredentialEntity.ToModel` had the same shape, one consequence milder — a lost write
  rather than a lost guarantee. Invisible on the live environment, where the prefix is empty and
  `PK(x) == x`, which is why every test passed over it. The store now strips the prefix on the way out, the
  way `TableUserStore` already did.

- **Every per-source quota was shared by the whole deployment on three of five endpoints.** Login and the
  SAML ACS keyed their rate limiter on the raw TCP peer address, which behind a reverse proxy is that proxy
  for every request — so the login cap (30 attempts per 5 minutes by default) was a single budget for every
  user of the server, and any anonymous caller could spend it and hold everyone at `429`, repeatably. This
  was true whether or not the proxy was declared, i.e. on a correctly configured deployment. Forgot-password
  had the opposite defect: it keyed on `Connection.RemoteIpAddress`, which is whatever `X-Forwarded-For` said
  whenever the immediate peer sits in the default-trusted private ranges, so its cap — the only bound on a
  caller walking an address list and having this server mail each one from your verified sending domain —
  could be minted afresh per request by varying one header. Registration and dynamic client registration
  already did the right thing; these three were sibling call sites of that fix.

  All five now key through `InternalEndpointGuard.SourceQuotaKey`, which decides on one rule: the forwarded
  client IP when the operator declared the proxy that supplies it, and the raw peer otherwise. A convention
  test fails the build on the next site that derives a quota source any other way, because no behavioural
  test can catch it — the correct and the broken key are indistinguishable in a host with no
  forwarded-headers pipeline.

  **If you have not declared your proxy, those quotas are still shared, and that is not fixable in code.**
  Whether the rightmost forwarded hop was written by a proxy or by the caller is exactly what
  `ForwardedHeaders:KnownNetworks` / `KnownProxies` states and what nothing else can reveal; folding the
  forwarded value in regardless would restore the per-request bypass above. The startup warning for an
  undeclared proxy now names this consequence explicitly instead of talking only about `X-Forwarded-Proto`.

- **The https requirement on an OIDC connection stopped at the discovery URL.** The document is the trust
  anchor, so it was required to be https and bound to its own `issuer` — and then the endpoints it *named*
  were re-validated for SSRF only. The SSRF guard permits `http` by design, because scheme policy belongs to
  the caller, so the document got to name a plaintext `jwks_uri` (the keys every upstream `id_token` is
  validated against), `token_endpoint` (this connection's client secret and the authorization code),
  `userinfo_endpoint` (the access token), or `authorization_endpoint` (where the user's own browser is sent).
  Each is a full compromise of the connection to anyone on the network path, reached from a document served
  over TLS. All four now require https, enumerated in one loop rather than checked three-at-a-time, since
  three-of-four is how the sibling gets missed. An IdP that publishes a plaintext endpoint in an otherwise
  https document will now fail to connect; that document was describing an insecure exchange.

- **Resolving redirects by hand had silently dropped .NET's refusal of an https→http downgrade.**
  `SafeOutboundHttp` follows hops itself so it can re-run the SSRF guard on each one — the whole point — but
  it re-imposed only that guard, and the guard permits `http`. So an https metadata or JWKS URL answering
  `302 Location: http://…` was followed onto the network in cleartext, on precisely the fetches whose
  responses are trust anchors. A scheme downgrade mid-redirect is now refused outright.

- **Any authenticated user could reach the full admin API by changing the case of a scope name.** Three
  components disagreed about scope-name comparison: the client allow-list matched case-INsensitively so
  `Admin` passed, `IScopeStore` point-reads the exact name so a case variant looked *unregistered* — and
  `ScopeRoleGate` deliberately leaves unregistered scopes alone — while the `IdentityAdmin` policy then
  matched the minted claim case-insensitively and honoured it. Scope tokens are case-sensitive (RFC 6749
  §3.3), so a variant is simply an unknown scope and is now refused at the authorize endpoint; the
  entitlement gate additionally resolves against the whole registered set so it fails closed on its own.
  **Breaking:** a client sending a differently-cased scope than the one registered now gets `invalid_scope`.

- **An embedded space in a scope name was a permanent admin backdoor.** `AllowedScopes` is joined into a
  space-delimited `scope` string on the wire, so an entry like `"authagonal-admin x"` was one opaque string
  to the reservation's whole-string comparison but two scopes to every consumer that splits. The check now
  splits each entry, lives in one place (`AdminScopeReservation`) used by all three write paths, and
  whitespace in a scope name is refused outright. `ClientSeedService` had **no** reservation check at all,
  so configuration could hand a client the scope the admin API and DCR both refuse to grant.

- **Token exchange accepted any JWT this server signs as `subject_token`.** All four mint sites shared one
  issuer, one key and the default `typ: JWT`, so an id_token or a back-channel logout token was exchanged
  for a live access token carrying the victim's `sub` and roles. Worse, neither carries a `jti`, so the
  0.20.0 revocation check silently degraded to a no-op and `RevocationEndpoint` (which requires `client_id`
  and `jti`) could not revoke them at all — there was no operator remedy short of rotating signing keys.
  Access tokens now carry RFC 9068 `at+jwt` and the exchange pins `ValidTypes`, with claim-shape checks on
  top so tokens minted before the header existed keep working through their remaining lifetime. The same
  pin closes cross-JWT confusion at userinfo and introspection. Logout tokens are typed `logout+jwt` and
  given an explicit 2-minute expiry instead of inheriting IdentityModel's 60-minute default.
  **Note for resource servers:** any validator with a strict `typ` allow-list must accept `at+jwt`.

- **`/_internal/backchannel-logout` was remote unauthenticated mass session destruction.** With
  `Cluster:Secret` unset the guard fell back to "the source address looks private", reading
  `Connection.RemoteIpAddress` — which `UseForwardedHeaders` has already overwritten from the
  client-supplied `X-Forwarded-For`. The forwarded trust set defaulted to EMPTY, which means *every* caller
  is a trusted proxy, so any internet client could claim `10.0.0.1` and revoke every grant for an arbitrary
  subject. The guard now reads the raw peer captured before forwarded headers are applied, accepts loopback
  only (a private-range address is not a credential), and the trust set defaults to the loopback/RFC1918
  ranges with a startup warning to pin the real CIDR. That default governs `X-Forwarded-For` alone —
  `X-Forwarded-Proto` is honoured only from a proxy the operator declared, because the scheme decides
  whether `/connect/*` answers at all and "the peer looks private" is a guess a library cannot verify.

- **The SAML EncryptedAssertion path was a decryption oracle against the SP private key.** RSA-PKCS#1 v1.5
  was accepted, `Pkcs1` sat in a catch-all fallback that tried paddings in turn rather than honouring the
  declared algorithm, CBC used default PKCS7 padding — and the exact `CryptographicException` message was
  returned to an anonymous, unauthenticated, unthrottled caller. That is Bleichenbacher/ROBOT plus a CBC
  padding oracle, and the SP keypair is minted for *every* connection whether or not the IdP encrypts, so
  it was armed by default everywhere. v1.5 is refused, the declared OAEP digest is honoured, every
  decryption failure returns one constant message, and the ACS is rate-limited. Also removed the
  pre-signature "quick parse" that used a second, unhardened reader (`XmlDocument.LoadXml`, DTD processing
  enabled) — a ~1 KB document of nested internal entities expanded to gigabytes before any signature check.
  Both parses now share one hardened loader, with a size cap.
  *Found while fixing:* .NET's `EncryptedXml.Encrypt(element, cert)` uses v1.5, so the project's own test
  fixture had only ever exercised the vulnerable algorithm.

- **A token-exchange client with no agent profile dictated the `authorization_details` the AS signed.**
  `ReadAuthorityClaim` returns `Unrestricted` when the subject token has no authority claim — the universal
  case — and `Unrestricted.Intersect(x)` returns `x` verbatim, so the client's request *became* the signed
  claim. Any client holding the exchange grant could mint issuer-signed fine-grained authority that no
  admin ceiling, consent record or user interaction ever produced. A client with nothing to attenuate now
  gets `invalid_authorization_details`.

- **The credentialed CORS policy applied to every endpoint.** The provider ignores `policyName` and
  `UseCors()` supplies none, so any origin a client registered could read authenticated responses from the
  cookie-authenticated interactive-auth API — including `POST /api/auth/mfa/recovery/generate`, which
  returns plaintext recovery codes. Client-registered origins are now honoured only on the OAuth protocol
  surface; everything else uses operator configuration. Dynamic registration no longer accepts an arbitrary
  origin list either: origins are derived from the registrant's own validated https redirect URIs.

- **A nested-parenthesis SCIM filter killed the worker process.** The filter parser's mutual recursion had
  no depth bound, and a `StackOverflowException` cannot be caught in .NET — it terminates the process,
  taking down every tenant on that pod from one request. Depth (~20) and total length (1024) are now bounded
  and surface as `400 invalidFilter`.

- **Passwordless passkey sign-in did not require user verification.** It was marked `mfa_authenticated` and
  documented as strong auth, but UV was `Preferred` and the resulting flag was never inspected, so an
  assertion proving only possession of an unlocked device satisfied an MFA-required policy. Passwordless now
  requires UV (Fido2 enforces it during assertion); the second-factor path keeps `Preferred`, where a
  password was already presented.

- **SAML bearer confirmation was entirely fail-open, and replay retention ignored assertion validity.**
  Every part of `SubjectConfirmation` was checked only "if present", so an assertion with none at all was
  accepted. That is not just conformance: `SubjectConfirmationData/NotOnOrAfter` is the SHORT bound (minutes
  at Entra/Okta/Google) while `Conditions/NotOnOrAfter` is ~an hour, so losing it left an assertion
  acceptable long enough to outlive the replay cache and be replayed. Bearer confirmation with `Recipient`
  and `NotOnOrAfter` is now required (an unparseable timestamp fails rather than skipping the check), and
  the replay record is retained for at least the assertion's own acceptability window. Only the SQL provider
  expired these rows; Azure and DynamoDB keep them indefinitely and were never exposed.

- **Federated account squatting, and SCIM as a takeover primitive.** JIT provisioning did not gate on
  `email_verified`, so anyone able to configure a self-service IdP could pre-create `ceo@acme.com`; when
  Acme's real connection was added, the genuine user's first login adopted that account along with the
  attacker's still-valid `(issuer, subject)` binding. JIT now refuses an unverified email and refuses a
  domain that `ISsoDomainStore` routes to a different connection. Separately, SCIM could mint a
  **pre-verified** account for any address, and `/auth/forgot-password` would then issue a reset for it —
  `ResetPasswordAsync` sets `PasswordHash` unconditionally — so repointing a bound account's email
  converted into authenticating as the real user's `sub` at every relying party, bypassing the upstream IdP.
  SCIM now validates the address and honours an optional per-client domain allow-list, and no reset is
  issued for an account with no local password.

- **A passwordless-account claim link promoted whatever was staged at click time.** Two claimants both send
  a verification link to the same (real owner's) inbox; the link asserted only "this address is verified",
  so the owner clicking their *own* link promoted the later claimant's password. The link is now bound to
  the credential staged when it was issued and refuses to promote a different one.

- **MFA verification had no per-account failure counter, and the per-challenge one could be raced.**
  `FailAttemptAsync` read `Attempts` from a snapshot and wrote it back with a blind full-row upsert in every
  provider (no ETag, no version column, no atomic increment on `IMfaStore`), so concurrent guesses shared one
  value — and an attacker could mint a fresh challenge whenever the budget ran out, making "5 guesses per
  challenge" no bound at all. Verification now goes through the same durable, atomically-incremented,
  cluster-wide lockout counter as the password step, plus a per-subject rate limit; success resets it.
  **Breaking:** the 5th wrong MFA code now locks the account (HTTP 423) rather than only burning the
  challenge.

- **The device-flow approval screen showed nothing about what was being approved.** With
  `verification_uri_complete` pre-filling the code, approval was one click on an opaque prompt — RFC 8628
  §5.4's remote-phishing shape, where an attacker starts a device flow, sends the victim the complete URI,
  and the victim authorises the attacker's device against their own account. A new authenticated,
  rate-limited `GET /api/auth/device/info` returns the requesting client and the scopes that would actually
  be granted (post-entitlement-gate), and the login app now requires an explicit confirmation showing both.

- **An MFA challenge was accepted as an MFA enrolment token, so the password alone was enough to take
  over an enrolled account.** Login returns an `MfaChallenge` id in two very different situations — "you
  have a second factor, prove it" and "you have none, enrol one" — and the record carried nothing to tell
  them apart. `MfaSetupEndpoints.ResolveUserIdAsync` accepted any live challenge in `X-MFA-Setup-Token`
  and returned its `UserId` as an authenticated identity, with no session and no check of which kind it
  held. Four sinks made that exploitable, all reachable with a correct password and no second factor:

  - `POST /recovery/generate` returned ten fresh recovery codes **and deleted the victim's real ones**;
    one code then satisfied `/verify` and signed a cookie carrying `mfa_authenticated`.
  - `POST /webauthn/setup` + `/webauthn/confirm` enrolled an attacker-controlled passkey, which survived
    a victim password reset (reset rotates the security stamp and clears grants but touches no MFA
    credential) — persistent takeover, not a one-shot bypass.
  - `POST /totp/confirm` was a second TOTP acceptance path with **no attempt counter at all** and no
    replay-ledger write: wrong codes were free guesses against a 10⁶ space, a code already burned at
    `/verify` was still accepted here, and success issued a session.
  - `/verify` accepted *unconfirmed* `TOTP (pending)` / `WebAuthn (pending)` rows as live factors, and
    those rows had no expiry — an abandoned enrolment left a permanent, self-service-invisible factor.

  `MfaChallenge` now carries an `MfaChallengePurpose` (`Verify` / `Enrol` / `PasswordlessDiscovery`) set
  at every mint site and enforced at every consumer. `Verify = 0` so a row written by a previous
  deployment reads back as the least-privileged case. Additionally, and independently of the
  discriminator: `/recovery/generate` now requires a real session, `/totp/confirm` is rate-limited and
  counts failures against the challenge budget and burns the accepted step, pending rows expire after 30
  minutes, and a challenge with an empty `UserId` is refused outright (`/passwordless/begin` minted one to
  anonymous callers, and the callers' `userId is null` guard let an empty string through). Each layer is
  independently sufficient for most sinks, verified by disabling them one at a time.

- **`--dry-run --mode clean` deleted everything, for real.** `CleanTableAsync` ran before the `DryRun`
  check that guarded the writes, so the flag whose entire purpose is to make a restore safe was the most
  destructive option the tool offered. Now gated like every other mutation.

- **A clean restore of an incremental destroyed every unchanged row.** The manifest has always recorded
  `Mode: "incremental"`; nothing read it. Emptying the table and then applying a delta leaves only the
  delta. Refused now, with the correct sequence in the error; `AllowCleanFromIncremental` overrides.

- **A clean restore wiped sibling envs sharing the physical table.** Sandbox envs are keyed
  `{env}|{natural}` in shared tables, and the wipe was unscoped. New `--clean-env` scopes it to one env's
  PartitionKey range; the bound uses `char.MaxValue` rather than `~`, so keys containing non-ASCII
  characters are inside the range instead of being silently skipped.

- **A concurrent login silently reverted administrative writes (Azure provider).**
  `RecordSuccessfulLoginAsync` read the user entity and wrote it back with an unconditional full-entity
  `Replace`, so anything an admin changed between the read and the write was discarded — not just the
  login columns but `IsActive` (undoing a SCIM deprovision), `MfaEnabled` (undoing an enrolment, which
  login gates on), `RolesJson` (undoing a role revocation), `PasswordHash` and `SecurityStamp`. An
  attacker who keeps authenticating controls one side of that race. Now ETag-conditional with retry,
  mirroring `RecordFailedLoginAsync` directly below it.

- **Nine of every ten recovery codes were dead, and the tenth worked ten times.** All ten were protected
  under one secret-provider name, and `ISecretProvider.ProtectAsync` treats `name` as the storage key, so
  each call overwrote the last and every reference resolved to the final hash. Now keyed per code.
  `ProtectAsync`'s contract documents that `name` must be unique per distinct value.

- **`IUserStore.CreateAsync` silently overwrote an existing account on the SQL and DynamoDB providers.**
  Azure has always failed closed via `AddEntityAsync` (409) while SQL used `INSERT … ON CONFLICT DO
  UPDATE` and DynamoDB a plain `PutItem`, so a create on an existing id replaced that account's password
  hash, roles, MFA flag and email. Both are insert-only now (`PutIfAbsentAsync` /
  `attribute_not_exists(pk)`). The one call site that does not generate its own id is OIDC JIT federation
  with `UseUpstreamSubjectAsUserId`, where the id is the upstream `sub`.
- **Failed logins are held to a fixed wall-clock floor (default 250ms), closing a user-enumeration
  timing oracle.** The no-such-user path already verified against a dummy hash so it wouldn't return
  instantly, but the dummy is always the native PBKDF2 format at the configured cost while a real
  account holds whatever format it arrived in: a Duende-migrated deployment stores ASP.NET Identity V3
  blobs verified at the iteration count embedded in the blob (10,000 for older ASP.NET Identity,
  210,000 for .NET 8), and a bcrypt import costs whatever its cost factor was. Neither equals the
  dummy, and the upgrade to the native format only happens after a *successful* login, so unrehashed
  accounts keep their foreign cost indefinitely. Against a 10,000-iteration migration a wrong password
  on an existing account came back an order of magnitude faster than one on an address that doesn't
  exist, which reads account existence straight off the latency and defeats the uniform
  `invalid_credentials` response. Matching the cost is unachievable when the verifier dispatches on the
  stored hash; matching the clock is, and it stays correct as formats change. Configurable via
  `Auth:FailedLoginMinimumMilliseconds` (`0` disables); raise it if you hold hashes more expensive than
  the floor, which a one-time warning now tells you about.
- **WebAuthn §7.2 step 6 (user-handle ownership) was stubbed out, never performed.** The ownership
  callback handed to Fido2NetLib was a hardcoded `true`, so the library asked the question on every
  assertion and the answer was always yes — and the passwordless endpoint never read
  `response.userHandle` at all, resolving the account solely from the credential-id index. Not a way in
  on its own: the index is authoritative and the signature is checked against that owner's key. But the
  handle is precisely the tiebreaker the spec puts there for the case where the index is ambiguous or
  has been repointed, and it was absent. The callback now compares the handle against the account the
  caller established — the challenged user for second-factor verification, the credential's indexed
  owner for discoverable login — and `/api/auth/mfa/passwordless/complete`, where the spec makes the
  handle mandatory, refuses an assertion without one (`401 user_handle_required`) or with one naming
  another account.
- **WebAuthn §7.1 step 22 (credential-id uniqueness) was disabled, and the compensating check was
  TOCTOU.** The uniqueness callback was likewise hardcoded `true`. The cross-user half was
  re-implemented in the enrolment endpoint, but as a read followed by an unconditional write, so two
  registrations of the same credential id could both pass it and the second repointed the index —
  redirecting passwordless login for that credential at whoever wrote last. Nothing at all rejected the
  *same* user re-registering an identical credential id: that produced a second credential row sharing
  one credential id, with its signature counter reset to the attestation's, and verification then picked
  whichever row enumerated first — possibly the stale, lower-counter one, silently weakening clone
  detection. Deleting either row removed the index entry both depended on. The callback now consults the
  store, and the index row is claimed with an insert-if-absent write (Azure `Add`/409, DynamoDB
  conditional put, SQL `ON CONFLICT DO NOTHING`) before the credential is created, so exactly one
  registration of a credential id can win. A duplicate is refused with `409
  credential_already_registered` whoever it belongs to.

### Changed

- **Breaking (custom `IMfaStore` implementations):** `StoreWebAuthnCredentialIdMappingAsync` is replaced
  by `TryStoreWebAuthnCredentialIdMappingAsync`, which returns `false` when the credential-id index row
  is already claimed. There is deliberately no unconditional write left on the interface — that row is
  what makes a credential id resolve to exactly one account, and a read followed by a write cannot
  establish that. Every bundled backend already had the underlying primitive.

### Fixed

- **Device flow: a `user_code` typed without the dash is now accepted (RFC 8628 §6.1).** Codes are
  displayed as `WDJB-MJHT` and the grant was keyed on exactly that, while entry only uppercased and
  trimmed. So `WDJBMJHT`, `WDJB MJHT` and `WDJB–MJHT` (the em dash a mobile keyboard's smart
  punctuation produces) were all rejected as invalid codes. Entry now strips everything outside the
  31-character alphabet before lookup and the grant is keyed on that same separator-free form; the dash
  survives only in the displayed `user_code` and in `verification_uri_complete`. The login app's code
  field formats as you type, so it submits the canonical form either way. No security impact, but each
  rejected variant spent one of the ten attempts per minute the brute-force limiter allows, so a user
  hunting for the right punctuation could lock themselves out of an approval that was valid all along.
- The `user_code` brute-force limiter (ten attempts per minute per subject) has a regression test:
  eleven wrong codes in one authenticated session, `429` on the eleventh, and a valid code presented
  after the budget is spent refused too. The guard was correct but unpinned behind a documented
  security claim. Both the endpoint and the docs now record that the default `InProcessRateLimiter`
  counts per node, so a multi-replica deployment needs the global limit enforced at the edge.

## [0.21.0], 2026-07-30

### Added

- **`Authagonal.SqlProvider`: self-hosted storage on PostgreSQL or SQLite.** The full
  `Authagonal.Core.Stores` surface, clustering (`ILeaseProvider`, `IClusterEventBus`) and the
  DataProtection key ring, with no cloud account, emulator or managed service. Reference the package and
  call `AddAuthagonalPostgres(…)` / `AddAuthagonalSqlite(…)` before `AddAuthagonal()` — the same
  contract as `Authagonal.AwsProvider`. Nothing else changes: `Authagonal.Server` does not reference it,
  so an Azure- or AWS-only application never pulls in Npgsql or the SQLite native binaries.

  Tables mirror the Azure and DynamoDB layouts one-for-one and are created on startup if absent, so a
  backup taken on one backend restores onto another.

  Every operation that must not race is a single statement: `DELETE … RETURNING` for single-use
  redemption (authorization codes, MFA challenges, OIDC state, SAML request ids),
  `UPDATE … WHERE consumedAt IS NULL` for refresh rotation, `UPDATE … WHERE version = @v` with retry
  for the lockout counter, `INSERT … ON CONFLICT DO NOTHING` for SAML assertion replay detection, and a
  conditional upsert for the leader-election lease. One test suite runs over both dialects.

  On PostgreSQL the key columns are pinned to `COLLATE "C"`. The key scheme is byte-ordinal throughout
  — prefix bounds, env-partition ranges, the grant expiry sweep, keyset paging — and a database created
  with a linguistic collation (`en_US.UTF-8` and ICU locales are the common defaults) orders
  punctuation and case differently, which would have made those scans silently return the wrong rows:
  expired grants never reaped, prefix search missing matches. The suite runs against an ICU-collated
  database to keep the pin honest.

  Neither engine expires rows the way DynamoDB TTL does, so `SqlExpiryReaper` sweeps the transient
  tables (SAML replay, OIDC state, MFA challenges, upstream refresh tokens, the revocation list).
  Grants stay with `IGrantStore.RemoveExpiredAsync`, which owns all three of their tables and the
  matching tombstones.

## [0.20.0], 2026-07-27

An RFC-by-RFC security review, prompted by outside comments on SCIM and SAML. Each item below was
checked against its RFC and the known CVE classes for that area; where a defence already existed it was
left alone and pinned by a test rather than rewritten. Every fix marked as a defect has a regression
test that fails against the previous release.

### Security

- **A revoked access token could be exchanged for a fresh, unrevoked one (RFC 8693).** Revocation was
  enforced at the resource server, at userinfo and at introspection, but the token-exchange path never
  consulted the revoked-token list. A revoked token still verifies and is still inside its `exp` —
  cutting a token short before expiry is the entire point of revoking one — so it was dead at every API
  and alive at `/connect/token`: hand it over as `subject_token`, receive a live successor. Revoking in
  response to a compromise ended the token's use against resources while leaving it able to mint
  replacements. Any client holding the exchange grant could do it.
- **PKCE accepted `plain` for clients not marked `RequirePkce` (RFC 7636).** The S256 requirement sat
  inside that check, so a client opting into PKCE *voluntarily* got no method validation — and RFC 7636
  §4.3 makes a missing method mean `plain` as well. `plain` offers nothing against the attack PKCE
  exists for: the challenge IS the verifier. **Breaking:** `plain` is refused everywhere now, and the
  token endpoint no longer defaults a missing method. Discovery has only ever advertised S256.
- **SAML signature verification: two structural gaps.** Only the first `Reference` was checked against
  the signed element's ID, and a document with duplicate IDs left `#id` ambiguous — the URI string check
  and `CheckSignature`'s own resolution could select different elements, which is the precondition for
  every classic wrapping attack. Both refused now.
- **Device flow (RFC 8628).** `user_code` was drawn with `byte % 31`, which is biased; generation is
  unbiased now. Code entry is rate-limited per §5.1 (ten attempts per minute per subject).
- **Algorithm pinning** across every inbound-token path — SAML signature and digest methods, client
  assertions (RFC 7523), upstream `id_token`s, BFF back-channel logout tokens, and token exchange.
  Measured honestly: .NET already refused the specific hostile inputs we could construct, so these are
  policy made explicit rather than holes closed. They keep the guarantee a property of this code.
- **BFF back-channel logout tokens now require a recent `iat`.** A logout token carries no `exp`, so
  without a freshness bound one stayed valid indefinitely: capture a legitimate token, replay it after
  the user signs back in, and they are logged out again, repeatably.

### Added

- **RFC 9207 — the authorization response names its issuer.** A client configured against several
  authorization servers could not tell which one a code came back from, and that ambiguity is the
  mix-up attack. `iss` is now returned and `authorization_response_iss_parameter_supported` advertised.
  Additive; clients that ignore it are unaffected.

### Added

- **SCIM: the full RFC 7644 §3.4.2.2 filter grammar.** All ten comparison operators (`eq ne co sw ew gt
  ge lt le`) plus `pr`, `and`/`or`/`not` with parenthesised grouping, value paths
  (`emails[type eq "work"].value`), sub-attributes (`name.givenName`) and URN-prefixed attribute paths.
  Previously one `attribute eq|co "value"` term was understood, against three attributes.
  - **Why it mattered beyond the missing features:** SCIM's ServiceProviderConfig has no way to
    advertise a *partial* filter capability, so `filter.supported = true` — which this provider has
    always returned — is a claim to the whole grammar. Anyone evaluating the integration could read the
    discovery document, try `userName sw "a"`, and find the claim did not hold.
  - Filters are evaluated against the resource as it is serialized to the client, so sub-attributes and
    multi-valued attributes work without a bespoke field map and a filter can only ever match on
    something the caller can see. `userName eq` / `externalId eq` keep their indexed point-lookup fast
    path; everything else is evaluated over a bounded paged scan, since PII is encrypted at rest and
    only reachable through blind indexes.
  - `ScimFilterParser` moves to `Authagonal.Server.Services.Scim` and now returns a parsed expression
    tree; `ScimFilterEvaluator` evaluates it. **Breaking** for any host that called the old
    `Services.ScimFilterParser` directly (its `Parse`/`Matches`/`MatchesGroup` are gone).

### Fixed

- **SCIM: a filter we cannot represent is now refused with `400 invalidFilter` instead of silently
  answering a different question.** `ScimFilterParser` supported one `attribute eq|co "value"` term, but
  its value check only looked at the first and last character, so a compound filter
  (`userName eq "a@x.com" and active eq "true"`) parsed as `userName eq` with the rest of the
  expression embedded in the value. That matched nobody and returned an **empty list** — which a
  provisioning agent asking "does this user exist?" reads as "no", and answers by creating a duplicate.
  A filter naming an attribute we do not index (`active eq "true"`) failed the same way.
  - The value must now be exactly one quoted string with no unescaped interior quote, and the attribute
    must be one the matcher can actually evaluate (`ScimFilterParser.UserFilterAttributes` /
    `GroupFilterAttributes`). Both list endpoints answer `400` with `scimType: invalidFilter` per
    RFC 7644 §3.4.2.2, naming the supported grammar.
  - New `TryParse` reports Absent / Unsupported / Parsed, because "no filter" and "a filter I cannot
    read" are opposite answers. The lenient `Parse` is retained for embedding hosts.

### Verified, unchanged

Recorded so they are not re-audited: comment truncation (CVE-2017-11427/11428) does not apply because
text is read with `InnerText`, which concatenates exactly what canonicalization signs — now pinned by a
test building that payload. Parser-differential (CVE-2025-25291/25292) and DigestValue-hoisting
(CVE-2024-45409) do not apply — one parser throughout, and signature material comes only from the
direct-child signature element. Certificates are pinned from metadata and never taken from `KeyInfo`.
Authorization codes are atomically single-use and bound to client and redirect URI. `redirect_uri`
matching is component-wise exact. TOTP rejects replay within a step. WebAuthn pins origin and RP ID and
rejects signature-counter regression.

## [0.18.0], 2026-07-27

### Changed

- **`@authagonal/login` depends on `react-router` v8 instead of `react-router-dom` v7.** BREAKING for
  consumers: `react-router-dom` leaves the dependency tree entirely (v8 removes the package and ships
  one), so an application importing from `react-router-dom` must move its own imports to
  `react-router`. Every symbol this UI uses — `BrowserRouter`, `Routes`, `Route`, `Link`, `Navigate`,
  `useNavigate`, `useSearchParams` — is on the main `react-router` entry; only `RouterProvider` and
  `HydratedRouter` moved to `react-router/dom`, and this declarative-mode UI uses neither. The React
  floor rises to 19.2.8 (v8 requires 19.2.5+); Vite was already 8.
  - **Why now:** react-router 7.12.0 through 8.2.0 is covered by GHSA-qwww-vcr4-c8h2 (an RSC-mode CSRF
    bypass), fixed only in 8.3.0. Nothing here runs RSC mode, so the advisory is a version gate rather
    than an exposure — but while this package depended on `react-router-dom ^7.13.2` it held every
    consumer inside the range, and no consumer could clear its own `npm audit` from its own side.

## [0.14.0], 2026-07-24

### Added

- **Agentic auth — the building blocks for delegating a user's authority to AI agents** (docs:
  `docs/agentic-auth.md`; design: `docs/agentic-auth-plan.md`). Every delegated token obeys
  `effective = admin ceiling ∩ user consent ∩ task request ∩ subject-token authority`, and authority
  only ever narrows per hop.
  - **Authority algebra** (`Authagonal.Core.Authority`): `AuthoritySet`/`AuthorityGrant` — a typed,
    RFC 9396-shaped (`authorization_details`) model with per-action `auto`/`ask`/`deny` policies and
    shape-typed constraints (allowlists ∩, numeric caps min, boolean gates AND; unknown members are
    preserved verbatim and fail closed). One `Intersect` implements the invariant everywhere;
    `AuthorityEvaluator` is the resource-side check. Property-tested never-widen.
  - **Agent registration** (`AgentProfile` + `IAgentProfileStore`, Table Storage + DynamoDB stores,
    auto-provisioned `AgentProfiles` table): a profile on a confidential client sets mode
    (delegated/service/both), the ceiling, sub-delegation depth, a token-lifetime cap, and the
    high-risk default (applied to `IConnectorCatalog`-flagged actions). Admin CRUD at
    `/api/v1/agents` incl. an `effective-grant` (ceiling ∩ consent) preview.
  - **Composite delegation** on the existing RFC 8693 exchange: a profiled client's exchange mints
    `sub` = user, `act` = agent (nested per hop, `actor_token` now accepted as corroboration), and an
    `authorization_details` claim carrying the intersection; introspection emits both; lifetime is
    additionally clamped by the profile. Sub-delegation requires depth budget from every actor already
    in the chain and attenuates by construction. Clients without a profile are untouched — though
    `authorization_details` now narrows plain exchanges too.
  - **Agent consent (the floor)** at `/consent/agents` (`agent_consent` grants): per (user, agent),
    stored pre-intersected with the ceiling and re-intersected at every mint, so admin narrowing takes
    effect immediately; revocation stops the next mint. No consent → `consent_required`.
  - **Just-in-time approvals**: an `ask`-policy action parks the exchange on
    `authorization_pending` + `approval_id` with device-flow poll semantics (`slow_down`,
    `access_denied`, `expired_token`; TTL `ApprovalLifetimeSeconds`, default 300). Users resolve at
    `GET /approvals` / `POST /approvals/{id}`; approvals are atomically single-use and bound to the
    request shape *and current policy state* (a ceiling edit invalidates instead of minting stale
    authority).
  - **Capability tickets** (`ICapabilityTicketService`): the ws-ticket generalized — opaque,
    short-TTL, atomically single-use handles over the grant store's conditional delete (closes the
    cache get-then-remove replay window). The BFF ws-ticket keeps its shared-cache contract.
  - **BFF authority chokepoint**: `BffUpstream.RequiredAuthority` ("type:action" pairs) makes the
    proxy evaluate the outgoing bearer's `authorization_details` before forwarding (403 on failure;
    never anonymous). `Authagonal.Bff` now references `Authagonal.Core`.
  - **Delegation-aware audit**: new default-interface `IAuthHook` members —
    `OnTokenIssuingAsync` (rich pre-mint gate), `OnDelegationMintedAsync` (full actor chain),
    `OnApprovalRequested/ResolvedAsync`, `OnAgentConsentChangedAsync`,
    `OnCapabilityTicketRedeemedAsync`. Existing hooks compile unchanged.
  - **`private_key_jwt` client authentication** (RFC 7523) for agent workloads:
    `OAuthClient.JwksJson`/`JwksUri` (nullable columns on both providers), assertion validation with
    `iss`=`sub`=`client_id`, token-endpoint audience, ≤10-min `exp`, and single-use `jti` replay cache
    over `IRevokedTokenStore`; advertised in discovery alongside
    `authorization_details_types_supported` (from the connector catalog).

## [0.13.4], 2026-07-24

### Changed

- **Duende migration writes the high-volume passes (users, external logins, MFA, refresh tokens) with
  bounded concurrency** instead of one-at-a-time. Table storage has no referential integrity and these
  entities are independent, so each pass now reads its rows sequentially then fans the writes out via
  `Parallel.ForEachAsync`, bounded by the new `Migration:MaxDegreeOfParallelism` (default 32) to stay
  under Azure Table's per-account throughput ceiling and off hot shared index partitions. A latency-
  bound ~51k-user migration drops from tens of minutes to a few. Counts stay exact (`Interlocked`);
  set the bound to 1 for the old sequential behaviour.

## [0.13.3], 2026-07-24

### Added

- **`Authagonal.Migration` — one-time Duende IdentityServer → Authagonal migration.** A new packable
  library carrying the migration engine, a leader-gated hosted runner, and CLI support (SqlClient stays
  in this package, out of every runtime consumer). `DuendeMigrationEngine` copies users (with claim
  folding: given_name/family_name/company/org_id onto first-class fields, email claims dropped, the rest
  to custom attributes), external logins, roles, scopes (ApiScopes + IdentityResources), clients
  (secrets tagged `SHA256$`/`SHA512$` by digest length, expired skipped), API-resource flattening,
  SAML/OIDC providers, SSO domains, MFA credentials (AuthenticatorKey + recovery codes), and optional
  refresh tokens — each pass idempotent and report-and-skip. `AddAuthagonalDuendeMigration(configuration)`
  wires a `BackgroundService` that runs the engine exactly once per configured `Version` (a
  `MigrationState` marker enforces run-once), gated on cluster leadership so a RollingUpdate's transient
  two-pod overlap can't double-run it; `DryRun` produces the full validation report without writing.
  `GET /admin/migration/status` exposes the marker + last report. Ids are preserved verbatim and host
  provisioning callbacks never fire for migrated users. The old `tools/Authagonal.Migration` console is
  renamed `tools/Authagonal.Migration.Cli` and now drives the shared engine.
- **`PasswordHasher` verifies tagged `SHA256$`/`SHA512$` client-secret digests** (fixed-time compare),
  so Duende-migrated client secrets authenticate. `TotpService.Base32Decode` (tolerant) and
  `RecoveryCodeService.HashForStorage` are exposed for the migration's MFA pass.

## [0.13.2], 2026-07-24

### Fixed

- **Mid-journey registrations/claims resume their journey after the verification click.** The
  flow's `returnUrl` now rides the verification token (optional 5th segment, security-stamp
  integrity like the rest) and is re-emitted on the `email_confirmed` login landing — so a
  registration or passwordless claim that began from an invite-accept continuation returns to
  the accept link after sign-in (including through the MFA "Not now" skip), instead of losing
  the returnUrl across the email hop and stranding on the account card with the invite never
  redeemed. Sanitization unchanged: the login page honors it only via resolveRedirect
  (same-origin or registered-app origins). Older 3/4-segment tokens stay valid.

## [0.13.1], 2026-07-23

### Fixed

- **`{param:guid}` constraint on BFF `ExchangeRoutes` patterns.** Version-prefixed APIs whose
  binding segment is positional (`/{apiver}/{project_id:guid}`) need the capture gated on GUID
  shape — without it a broad pattern captures literal-segment routes ("/v1/user/profile") and
  wrongly demands an exchange for them (403). Unknown constraints never match. The LAST
  placeholder names the exchange parameter; earlier ones act as positional wildcards.

## [0.13.0], 2026-07-23

### Added

- **`ITokenExchangeSubjectTransformer`** — host seam on the RFC 8693 exchange path for
  context-bound tokens (e.g. project/workspace tokens). Invoked after the subject is rebuilt from
  the validated subject token and scopes are narrowed, before the mint: the host validates any
  non-standard request parameters (now forwarded down from the token endpoint) against its own
  authority, forces binding claims via `AdditionalClaims`, and may SHORTEN the token lifetime
  (never lengthen — the service re-clamps to the subject token's expiry). Rejection surfaces as
  `invalid_target`. No-op default; register your own ahead of `AddAuthagonal`.
- **BFF context-token machinery**: `ITokenClient.ExchangeTokenAsync` (RFC 8693 with extension
  params); `AuthagonalBffOptions.TicketExchangeParams` — allowlisted `/ws-ticket` query params
  that bind the ticket to an EXCHANGED downscoped token instead of the session's primary access
  token (denied exchange → 403, no ticket); `AuthagonalBffOptions.ExchangeRoutes` — proxy routes
  (`/projects/{project_id}` style, one placeholder, prefix-matched) whose upstream calls ride a
  context-bound exchanged token, cached per (session, binding) for the token's lifetime.

### Fixed

- Token-exchange `resource`/`audience` rejection paths now have test coverage (`invalid_target`).

## [0.12.0], 2026-07-23

### Added

- **RFC 8693 token exchange** (`grant_type=urn:ietf:params:oauth:grant-type:token-exchange`) on both
  the Protocol and Server `/connect/token` endpoints, advertised in discovery. A client with the
  `token-exchange` grant presents one of this server's own access tokens as `subject_token` and
  receives a downscoped access token: requested scopes must sit inside both the subject token's
  scopes and the client's allowed scopes (no request → the intersection, minus `offline_access`);
  the exchanged token's lifetime never exceeds the subject token's remaining lifetime; custom
  claims are re-gated by the NEW scope set's `UserClaims` whitelists; `resource`/`audience` narrow
  `aud` to values pre-registered on the exchanging client. No refresh token is ever issued from an
  exchange, actor tokens are rejected (`invalid_request`), and client-credentials tokens (no `sub`)
  cannot be exchanged. Response carries `issued_token_type` per the RFC. Intended for minting
  short-lived tokens bounded to a narrow context (e.g. a single project) from a primary session
  token.
- **`AllowUninvitedJit` and `IsExternalConnection` are settable via `OidcProviders` config**, so
  config-seeded connections can opt into uninvited JIT provisioning / the external trust tier
  without the admin API.

## [0.11.0], 2026-07-23

Hardening + correctness release from the v0.10.0→HEAD review. New public surface (see Added) makes
this a minor bump.

### Security

- **BFF open redirect via backslash closed.** `SanitizeReturnUrl` accepted `/\evil.com` — browsers
  normalize `\`→`/`, turning it into a protocol-relative off-site redirect. It now mirrors ASP.NET
  `Url.IsLocalUrl` (a `/`-prefixed path is local only if the second char is neither `/` nor `\`),
  closing the `/login`, `/logout`, and `/logout-callback` return-URL sinks.
- **`prompt=login` is now enforced and no longer loops under PAR.** A pushed-authorization request
  carrying `prompt=login` looped until the `request_uri` expired without ever issuing a code, and an
  existing session could otherwise satisfy the prompt without re-authenticating. Sessions record
  `auth_time`, and `/connect/authorize` honors `prompt=login` only when the session is newer than the
  request (`auth_time >= request CreatedAt` for PAR), signing out any stale session first.
- **Passwordless-account claims can no longer mutate the victim account before verification.** A
  claim's profile/custom-attributes are STAGED and applied only when the fresh verification email is
  clicked; custom-attribute keys are whitelisted via `AuthOptions.ClaimAllowedAttributeKeys` (empty =
  allow all). Previously, knowing a federated account's email was enough to rename it and inject
  attributes that ride the real owner's tokens.
- **Upstream-federated refresh no longer mass-terminates sessions on a config fault.** Only an
  `invalid_grant` from the upstream revokes the local session; a rotated/misconfigured client secret
  (`invalid_client`) or other 4xx is treated as transient and the session survives.
- **Connection trust tier.** An OIDC connection can be marked external (`IsExternalConnection`); the
  first-party-only flags (`UseUpstreamSubjectAsUserId`, `AutoLinkExistingByEmail`) are then neutralised
  even if set, so a misconfiguration can't hand an external IdP an account-takeover lever. Defaults to
  first-party — existing connections are unchanged.
- **`auth_time` is now minted on every sign-in** (password, OIDC, SAML). It was advertised in discovery
  and the token passthrough allowlists but never set, so id_tokens never carried it.

### Added

- **`IUpstreamRefreshTokenStore`** — a durable per-`(user, connection, session)` store for the upstream
  federated refresh token, so every RP grant in a session reads and rotates one shared token instead of
  pinning a login-time cookie copy that dies after the upstream one-time-rotates it. Azure Table +
  DynamoDB implementations, token encrypted at rest. Only active when `RevalidateOnRefresh` is on.
- **`AllowUninvitedJit` on OIDC connections** (default off), matching the existing SAML flag —
  self-service auto-provisioning of an uninvited allowed-domain user, tagged with `federated_connection`.
- **`AuthOptions.ClaimAllowedAttributeKeys`** — whitelist for the custom-attribute keys a passwordless
  claim may carry onto the account.
- **BFF ws-ticket redemption:** `WsTicketKey` is now public and `TryRedeemWsTicketAsync` ships the
  single-use get-then-delete (documenting the residual replay window).
- **`login_hint` is forwarded to OIDC upstreams** on the straight-to-IdP prefill path (previously only
  SAML honored it).

### Fixed

- **MFA credential deletion propagates storage faults** instead of swallowing them; the WebAuthn
  credential-id parse is extracted to a shared `MfaCredential.TryGetWebAuthnCredentialId`.
- **TableTicketStore lifecycle:** 404-swallow on removes (no more 500 on a double logout/revoke race),
  expired sessions hidden from listings, and a lazy sweep of a user's expired sessions on new-session store.
- **Registered-app absolute returnUrls** are honored on the login-app skipMfa / sso_required / MFA-setup
  paths (they were silently dropped to `/`).
- **`ReprovisionAsync` gets a default interface implementation** so adding it no longer source-breaks
  external `IProvisioningOrchestrator` implementors.
- **`accountCreated` / `emailVerified` translated** into all locales (were English-only via fallback).
- BFF proxy prefix match respects segment boundaries; admin bulk MFA-status runs bounded-parallel and
  reports truncation; admin OIDC `InteractionPath` requires a leading `/`; plus comment/casing/doc nits.

### Changed

- **Removed the Klingon (`tlh`) novelty locale.**
- **Consumer product names removed** from XML doc comments and the NuGet `<Authors>` package metadata.
- **Federation JIT gate extracted** to a shared `FederationJitPolicy` used by both the OIDC and SAML
  callbacks, so the two protocols can't drift.
- **Release tooling:** CHANGELOG backfilled (0.10.11–0.10.29); `tag.sh` now gates on `dotnet build`/
  `dotnet test` + the login-app typecheck before tagging, and accepts an explicit version; line endings
  normalised to LF.

## [0.10.29], 2026-07-23

### Security

- **Passwordless-account claims re-prove inbox ownership (breaking).** Registering the email of an
  existing federated (credential-less) account no longer sets a usable password immediately — the
  credential is staged and only activated once the account's own email-verification link is clicked.
  Knowing a federated user's email can no longer be turned into an immediate sign-in as that user.

## [0.10.28], 2026-07-23

### Added

- **Registration abort-shielding.** Registration and downstream provisioning run to completion even
  if the browser disconnects mid-request, so an account can't be left half-provisioned.
- **Straight-to-IdP for hinted SSO-domain emails.** An authorize request whose `login_hint` email
  belongs to an SSO-governed domain redirects directly to that IdP instead of the login card.

### Fixed

- `TestProvisioningOrchestrator` implements `ReprovisionAsync` — a test-double gap (missed in
  0.10.24) that left the test project non-compiling for 0.10.24–0.10.27.

## [0.10.27], 2026-07-23

### Fixed

- Client seeding binds `InitiateLoginUri`, `ClientUri`, and `IsDefaultApplication`.

## [0.10.26], 2026-07-23

### Fixed

- Login `continueDestination` falls back to the tenant default application when none is specified.

## [0.10.25], 2026-07-23

### Added

- **Registered-app return URLs.** Absolute returnUrls for registered applications are honored
  (resolved via `resolveRedirect`), plus email-verified / account-created notices on the login app.

## [0.10.24], 2026-07-22

### Added

- **Reprovision on passwordless-account upgrade.** An account claimed via passwordless upgrade is
  re-provisioned downstream (`ReprovisionAsync`) so the downstream app sees the claim's signup
  context (e.g. org name), which a plain re-provision would skip.

## [0.10.23], 2026-07-22

### Added

- **`AllowPasswordlessAccountClaim`** (opt-in, default off): registering the email of an existing
  credential-less (federated/JIT) account claims it by setting a password and provisioning, instead
  of returning the enumeration-neutral duplicate response. (Superseded by the 0.10.29
  re-verification gate.)

## [0.10.22], 2026-07-22

### Fixed

- **MFA credential deletion clears its WebAuthn index row** — no stale credential-id → user pointers.

## [0.10.21], 2026-07-22

### Added

- **Bulk MFA-enrollment status endpoint** for admin directory badges.

## [0.10.20], 2026-07-22

### Added

- **Invite-vouched email verification.** A provisioning handler can vouch an email as verified,
  skipping the verification email; and the signed-in "dead-end" card now offers a continue path.

## [0.10.19], 2026-07-22

### Fixed

- Register-page password checklist updates live and disappears once the policy is satisfied.

## [0.10.18], 2026-07-22

### Fixed

- **Per-login BFF correlation cookie.** Concurrent logins no longer clobber each other's correlation
  state — the cookie is keyed per login `state`.

## [0.10.17], 2026-07-22

### Fixed

- Persist OIDC connection `InteractionPath` in the Azure table entity (0.10.15 regression).

## [0.10.16], 2026-07-22

### Fixed

- Fill missing `consent` / `grants` / `mfaTooManyAttempts` login-app translations.

## [0.10.15], 2026-07-22

### Added

- **OIDC connection `InteractionPath`.** Render a login-app interstitial (e.g. a share-link
  name/terms form) before `idp_hint` federation.

## [0.10.14], 2026-07-22

### Fixed

- Localize password-policy rule labels on the register page.

## [0.10.13], 2026-07-22

### Added

- **`AllowUninvitedJit`** (SAML): auto-provision an uninvited domain user on SSO login when the
  connection opts in.

## [0.10.12], 2026-07-22

### Added

- **`prompt=login` at the authorize endpoint.** Honor the OIDC `prompt=login` request to force
  re-authentication. (Enforcement and the PAR-loop fix landed later — see [Unreleased].)

## [0.10.11], 2026-07-21

### Added

- **Upstream-federated refresh.** Optionally revalidate a federated session against its upstream IdP
  on token refresh (`RevalidateOnRefresh`).
- **Provisioning attributes through federation.** Carry invite/provisioning context (whitelisted
  params) through federation into JIT provisioning (invite-only).

### Fixed

- Federation loop-breaker reads the live query; a hard auth failure returns `access_denied` to the
  relying party.

## [0.10.10], 2026-07-21

### Added

- **BFF `/logout` accepts an allowlisted `returnUrl`** (same allowlist as `/login`). It is carried
  through the RP-initiated `end_session` round trip via `state` — so it survives the auth host
  clearing its SSO cookie — and echoed back onto a new registered `{BasePath}/logout-callback`
  endpoint, which re-validates and redirects. Enables "abandon the current session, then land back
  here" flows (e.g. a signed-in user opening a guest share link that must be claimed as a different
  identity). The callback URL must be registered as a `post_logout_redirect_uri` for the BFF client.

## [0.10.9], 2026-07-21

### Added

- **`AutoLinkExistingByEmail` on OIDC federation connections** (default off): link a federated
  identity to an existing local account matched by email even without AllowedDomains vouching.
  For trusted first-party connections whose email assertions are inbox-verified (share-link
  providers, where the downstream host pre-creates the local account itself).

### Fixed

- **Federation failure redirect loop**: a failed federation round bounced back to `/connect/authorize`
  with error params appended, which re-federated on the idp_hint forever ("too many redirects").
  The authorize endpoint now returns the federation error to the relying party's validated
  redirect_uri per OAuth.

## [0.10.8], 2026-07-21

### Fixed

- **Protocol: `AdditionalClaims` now ride the id_token as well as the access token.** For an
  embedded provider federating into a full Authagonal host, the id_token is what the downstream
  host reads claims from (`federated:*` capture) — access-token-only claims like a share-link
  token vanished at the federation boundary.

## [0.10.7], 2026-07-21

### Fixed

- **Protocol: authorize now runs the configured `AuthenticationScheme` explicitly.** It previously
  only inspected the default-scheme-populated `HttpContext.User`, so a host whose registered scheme
  is not its default (e.g. an API host with a bearer-stack default registering a purpose-built
  scheme for the embedded provider, like a share-link handler) could never authenticate — every
  authorize went straight to the scheme's challenge. The resolved principal is also what the
  subject resolver now receives.

## [0.10.6], 2026-07-21

### Added

- **Bff: `AllowAnonymousProxyRequests`** (default off): the token-injecting proxy forwards
  session-less (or dead-session) requests to the upstream WITHOUT an Authorization header instead of
  rejecting them with 401 — classic SPA semantics where the API's own auth decides, so
  `[AllowAnonymous]` endpoints (share-link fetch/peek) work signed-out while protected endpoints
  still return their own 401. The anti-forgery header remains required.

## [0.10.5], 2026-07-21

### Fixed

- **Bff: array claims survive `/bff/user`** — repeated id_token claim types (`roles`, `groups`)
  are now space-joined into the session claim map; previously only the first value survived, so a
  multi-role user lost everything after their first role.

## [0.10.4], 2026-07-21

### Changed

- **`OidcSubject` is now a `record`** (was a sealed class; same shape, adds value equality). Lets a
  host DECORATE the registered `IOidcSubjectResolver` and overlay fields with a `with` expression —
  e.g. enriching subjects with live org context from an external system of record on every
  authorize/refresh — without hand-copying (and silently dropping) the remaining fields.

## [0.10.3], 2026-07-21

### Added

- **Per-connection MFA challenge control** (`ChallengeMfaAfterLogin`, SAML + OIDC connections,
  default `true`): whether users are still routed through the LOCAL MFA challenge after this
  connection authenticates them (F42). `false` = the tenant trusts the upstream IdP's own MFA as
  the second factor — federation signs the session in `mfa_authenticated` without a local
  challenge. Threaded through config seed, admin create/update DTOs and the Azure/Dynamo stores
  (persisted in the negative, so pre-existing rows keep challenging).
- **`Scopes[]` config seeding** (`ScopeSeedService`): register custom scopes (name, display name,
  `UserClaims`, discovery visibility) from configuration at startup, mirroring `Clients[]` — they
  appear in `scopes_supported` and are ready for per-scope claim release.
- **`BackchannelLogoutUri` on `Clients[]` seed entries**: config-seeded clients can now register a
  back-channel logout endpoint (previously only dynamic registration could), e.g. a BFF's
  `/bff/backchannel-logout`.

## [0.10.2], 2026-07-21

### Added

- **Bff: websocket tickets** (`AuthagonalBffOptions.WsTicketsEnabled`, off by default): `GET
  {BasePath}/ws-ticket` mints a short-lived (default 30s), single-use ticket bound to the session's
  refreshed access token, stored in the shared distributed cache under `agbff:wst:{ticket}`. A browser
  websocket handshake can't carry a bearer or custom headers, so the SPA fetches a ticket (anti-forgery
  header required), puts it on the connect URL, and the API host exchanges + deletes the cache key to
  authenticate the socket. Requires a shared cache (Redis) — the in-memory default can't serve another
  process. The ticket must never be written to client storage: fetch, connect, drop.

## [0.7.11], 2026-07-16

### Added

- **`ShowOnLogin`** on OIDC federation connections (default `true`): when `false`, the connection is
  reached only via an explicit `idp_hint` and is **not** rendered as a "Continue with {name}" button on
  the login page. For a bounded, machine-triggered connection such as a share-link / guest-OIDC provider
  a login button makes no sense. Threaded through config seed + the Azure Table store; persisted in the
  negative (`HiddenFromLogin`) so existing stored connections default to shown. The `/api/auth/providers`
  payload now filters on it alongside the existing domain-routed check.

## [0.7.10], 2026-07-16

### Added

- **`UseUpstreamSubjectAsUserId`** on OIDC federation connections: when set, a JIT-provisioned federated
  user's local id is the upstream `sub` rather than a fresh GUID, so a trusted first-party connection
  (e.g. a share-link / guest-OIDC provider) keeps the local `sub` equal to the downstream RP's own user
  id. Threaded through config seed + Azure/Dynamo stores. Do NOT enable for arbitrary external IdPs.
- OIDC provider config seed now honours `JitProvisioningEnabled`, `UseUpstreamSubjectAsUserId`,
  `PassthroughParams` and `SessionExpClaim` (previously dropped by `ProviderSeedService`).

### Changed

- **JIT provisioning is now OFF by default.** Renamed the per-connection `DisableJitProvisioning`
  (negative sense) to `JitProvisioningEnabled` (positive) on `OidcProviderConfig` / `SamlProviderConfig`,
  keeping `DisableJitProvisioning` as an inverting alias for back-compat. The stored column/blob is
  unchanged, so existing STORED connections keep their behaviour (an old `DisableJitProvisioning=false`
  reads back as JIT-on) — only the C# default flips, which affects config-seeded connections. A
  connection must now explicitly opt in to auto-provisioning of unknown federated users.

## [0.7.9], 2026-07-16

### Added

- **AWS provider parity**: `DynamoUserStore` now implements the full modern `IUserStore` surface: PII document encryption + blind-index lookup keys via the `IFieldCipher`/`IIndexTokenizer` seams (with plaintext dual-read and reindex/migration backfills), native cursor paging, id/login-state enumeration for retention sweeps, email-domain search, and attribute-only login stamps. New `AddAuthagonalAwsStorage(...)` one-call composition (DynamoDB + Secrets Manager + S3 DataProtection). The AWS lane is now covered by DynamoDB-Local integration tests (stores, crypto, clustering).
- **Email auto-wire**: the built-in Resend sender activates when `Email:ResendApiKey` is configured; `UseAuthagonal` warns at startup when mail would be discarded while the confirmed-email login gate is on.

### Fixed

- **Back-channel logout never notified relying parties**: the logout-token `events` claim used an anonymous object, which the JWT handler cannot serialize (IDX11025); the per-client catch swallowed the failure, so every RP was counted as failed and no request was ever sent. Both the internal fan-out and end-session mints are fixed and now covered by tests that observe the real HTTP fan-out.
- **Admin token mint issued a refresh token unconditionally**: `POST /api/v1/token` now issues one only when `offline_access` is requested and the client allows offline access.
- `appsettings.json` shipped dead SendGrid email keys; replaced with the real `Email:ResendApiKey`/`SenderEmail`/`SenderName`.

### Documentation

- Full refresh of the GitHub Pages docs against the current source, with the CHANGELOG backfilled from 0.4.1 and the dead `Authagonal.Storage` package references corrected to `Authagonal.AzureProvider`.
- All six non-English locales (de, es, fr, pt, vi, zh-Hans) brought to full parity with the English docs.

## [0.7.7] – [0.7.8], 2026-07-15

### Fixed

- **MFA challenge burn**: a wrong TOTP/recovery/WebAuthn code no longer consumes the one-time challenge; the code is validated first and the challenge is consumed only on success, with a bounded retry budget (5 attempts) before it burns. Fixes the "first typo traps you on the MFA page" bug.
- **MFA page escape**: the hosted MFA challenge page has a persistent "Back to sign in" link, and its back-links preserve the return URL.

### Added

- **Login logo background chip**: optional per-mode (`lightLogoBg`/`darkLogoBg`) background behind the login logo so white/transparent artwork stays visible on light cards. Non-regressive when unset.

## [0.7.5] – [0.7.6], 2026-07-15

### Fixed

- **Login-page CSP**: the server-inlined boot payload is now a non-executable `<script type="application/json">` tag (no longer blocked by `script-src`), and `font-src 'self' data:` allows inlined font subsets.
- **Packaging**: `Authagonal.AwsProvider` floats its `Microsoft.Extensions.*.Abstractions` references on `10.*`, fixing the NU1605 that blocked the whole NuGet publish. Note: the v0.7.5 NuGets were never published because of that failure (only npm went out), use v0.7.6 as the effective release.

## [0.7.3] – [0.7.4], 2026-07-14 → 07-15

### Added

- **SAML vendor-quirk readiness**: pasted-metadata support with a condensing parser (vendor metadata routinely exceeds storage limits), friendly-name + OID attribute aliases, multi-valued group attributes, per-connection `NameIdFormat` (including ADFS-safe omission), signature-failure metadata refetch, and the post-login return URL riding the stored AuthnRequest instead of RelayState.
- **SAML SP keypair**: per-connection self-signed SP certificate: signed AuthnRequests (auto-enabled when the IdP wants them), `EncryptedAssertion` decryption (RSA-OAEP/1.5 + AES-CBC/3DES; AES-GCM is not supported by .NET's `EncryptedXml` and reports a clear error), and SP metadata publishing signing + encryption key descriptors.
- **SAML Single Logout**: SP-initiated `/saml/{id}/logout` and IdP-initiated `/saml/{id}/slo` (Redirect + POST bindings), with session-bound safety for unsigned IdP logout requests.

## [0.7.0] – [0.7.2], 2026-07-12 → 07-13

### Added

- **Cursor-paged user listing**: `IUserStore.ListPageAsync`/`ListByScimClientPageAsync` with native continuation tokens; SCIM listing uses cursor pagination and SCIM `eq` filters resolve via blind-index point lookups.
- **`IUserStore.EnumerateLoginStatesAsync`**: a non-PII login-state stream for retention sweeps.
- **GDPR "Your data"**: self-service data export and account-deletion request on the hosted account page.

### Security

- **OIDC/federation + grant-store hardening (F32–F48)**: atomic grant consumption (`TryMarkConsumedAsync`), recovery-code encryption at rest, open-redirect fixes (backslash bypass; disabled/unknown-client redirect guard on both authorize endpoints), upstream-IdP scope filtering, federated logins now route MFA-enrolled users through the MFA challenge, device-flow `slow_down`, OIDC state atomicity + subject consistency checks, and tombstone-first expired-grant removal.

## [0.6.0] – [0.6.6], 2026-07-10 → 07-12

### Added

- **Change-log-driven incremental backups**: stores write every mutation to a change-log so incremental backups point-read changed rows instead of full-table scans. Opt-in via `BackupOptions.ChangeLoggedTables`; a daily full-scan backstop guarantees coverage. Includes the F24 restore-chain correctness cluster: exact EDM type round-trips, restores apply tombstones, a clock-skew margin on incremental filters, and tombstone-first delete ordering in every store.
- **Server-inlinable login boot payload**: the host can inline branding + providers as a JSON script tag to save a round-trip.

### Changed

- **BREAKING: `ITombstoneWriter` → `IChangeWriter`**: the change-log seam gained upsert capture (`WriteUpsertAsync`/`WriteUpsertBatchAsync`) and was renamed. Implementations and registrations must follow.

## [0.5.0] – [0.5.1], 2026-07-10

### Changed

- **BREAKING: OIDC endpoint plumbing deduplicated into `Authagonal.Protocol`**: discovery/JWKS/OAuth-error models moved to `Authagonal.Protocol.Endpoints`; embeddable hosts map them via `MapAuthagonalProtocolEndpoints`.

### Fixed

- **Grant re-key on re-store**: fetched grants are re-keyed before persisting, restoring device approval + refresh rotation (the store never persists the plaintext handle).

## [0.4.38] – [0.4.41], 2026-07-07 → 07-10

### Added

- **Encryption backfills**: legacy plaintext `UserExternalIds` index rows re-keyed; `UserLogins` and provisioning-app API keys encrypted at rest with backfills.

### Changed

- **Perf**: blind-index tokenization + login encryption batched into single Vault round-trips.

## [0.4.23] – [0.4.37], 2026-07-03 → 07-06

### Added

- **PII encryption at rest (searchable)**: Vault Transit (`aes256-gcm96`) field encryption behind the `IFieldCipher` seam for user PII and grant data, with keyed-HMAC blind indexes (`IIndexTokenizer`) for email, first/last-name prefixes, external IDs, email domain, and email local-part prefix, exact search over encrypted data, with lazy migration and an `ReindexUserAsync` backfill. Index updates write-before-delete so a Vault hiccup can't lock out logins.
- **Back to app**: client home URIs + a default application, so hosted pages (account, verification, reset) can send the user back to the right app; the originating client is threaded through verification + reset emails.
- **`OnMfaVerifyFailedAsync`**: auth-hook event for failed MFA/passkey verification.

### Fixed

- **Language pickers**: derive from the locale registry (hi/af/ar were missing).

## [0.4.15] – [0.4.22], 2026-07-01 → 07-02

### Added

- **Passkeys (WebAuthn)**: passwordless passkey login via conditional mediation, multiple passkeys per user, passkey enrollment gated behind TOTP, per-request relying-party config (multi-tenant-safe), and tenant-policy gating on self-service setup.
- **Edge-cacheable discovery**: `Cache-Control` on the OIDC discovery + JWKS documents.
- **Publish-ahead key rotation**: sign with a specific Transit key version.

## [0.4.6] – [0.4.14], 2026-06-24 → 06-28

### Added

- **Provider branding on the login screen**: icons + SAML connections surfaced on the hosted login page.
- **Per-tenant email-confirmation login gate** + an email-confirmed auth-hook event.
- **`extraRoutes` seam**: the host owns product routes inside the hosted login app.
- **Per-theme branding**: `darkMode`, `darkPrimaryColor`, and per-theme background/card colours.
- **Locales**: Hindi and Afrikaans added to the hosted auth pages.
- **Auth-hook events** for self-service MFA + password changes.

### Fixed

- **Email verification links**: served on GET (were POST-only) and the reset-password link prefix corrected.

## [0.4.1] – [0.4.5], 2026-06-21 → 06-23

### Added

- **User locale (preferred language)**: stored on the profile, editable on the new self-service account page, mapped from SCIM `preferredLanguage`, and used for localized email.
- **Arabic translation + RTL support** on the hosted login pages.

### Fixed

- **Storage→AzureProvider rename** completed across build files, tests, and `tag.sh`.
- **Login layout**: consent/grants pages no longer double-wrap `AuthLayout`; the "Powered by Authagonal" footer renders again.

## [0.4.0], 2026-06-17

### Added

- **AWS backend provider**: `Authagonal.AwsProvider` implements all stores and clustering on AWS (DynamoDB / S3 / Secrets Manager). The storage backend is now selectable: run on Azure Table Storage or AWS.

### Changed

- **`Authagonal.Storage` → `Authagonal.AzureProvider`**: the Azure Table Storage implementation moved to a provider-named package alongside `Authagonal.AwsProvider`. Update package references and service registration accordingly.

## [0.3.18] – [0.3.19], 2026-06-17

### Security

A second hardening pass:

- **Login & registration enumeration resistance**: responses no longer reveal whether an account exists.
- **Password-reset rate limiting**: per-email limit to prevent email-bombing.
- **Atomic lockout counter**: closes a parallel brute-force bypass.
- **Token audience validation**: issued tokens are validated against the configured `Audience(s)`.
- **Auth-hook ordering**: `IAuthHook`s now run *before* the session is established.

## [0.3.14] – [0.3.17], 2026-06-16 → 06-17

### Fixed

- **Accessibility**: `main` landmark, WCAG AA contrast, tap-target sizing, and a sensible min-width on the hosted login layout.
- **Login routing**: login-app router mounted at basename `/login` so sub-routes resolve.
- **Backup**: don't dispose the hash before reading it.

## [0.3.8] – [0.3.13], 2026-06-15

### Added

- **Cloudflare Turnstile**: opt-in CAPTCHA on login, registration, forgot-password, and reset-password; CSP allowance when configured; dedicated `captchaFailed` message across all login-app locales.
- **SCIM group → role mapping**: resolved at token issuance.

### Fixed

- **SCIM**: fixed user-listing overflow when fetching all users.

## [0.3.4] – [0.3.7], 2026-06-13 → 06-15

### Added

- **Pluggable event-bus clustering**: replaced gossip-based clustering with a pluggable event-bus architecture.
- **Admin registration**: custom user IDs, pre-confirmation, and extended attributes on admin-created users.

### Fixed

- **SAML (Entra)**: fixed RSA-SHA256 signature verification on .NET/Linux; removed the unsupported SAML `Subject` from `AuthnRequest`.
- **Build**: fixed a CS0433 break by bumping Azure.Identity to 1.21.0; net10.0 Docker publish.

## [0.3.1] – [0.3.3], 2026-05-30 → 05-31

### Added

- **User registration & OAuth screens**: login-app gains self-service registration plus OAuth consent and device-authorization pages.
- **Multi-framework**: net9.0 and net10.0 target support.
- **`example.com` auto-confirm config** and **WebAuthn credential-uniqueness** enforcement; expanded security regression tests.

### Security

Hardening from a full security review (the "top 8"):

- **MFA always challenges**: enrolled users are always challenged; closes a path where MFA could be bypassed.
- **Federation account-takeover prevention**: upstream logins now require `email_verified` and enforce the provider's `AllowedDomains` before linking or provisioning, preventing takeover via unverified or out-of-domain emails.
- **SAML assertion replay fix**: incoming SAML assertions are checked against the replay cache so a captured assertion cannot be re-submitted.
- **SCIM group isolation**: SCIM group access is scoped to the requesting client, closing a cross-client IDOR.
- **Admin scope reservation**: the admin scope (`AdminApi:Scope`) can no longer be granted to an OAuth client nor issued through the impersonation endpoint, preventing privilege persistence.
- **Forwarded-header trust config**: `ForwardedHeaders:ForwardLimit` / `KnownNetworks` / `KnownProxies` control which proxy hops may set the client IP, so `X-Forwarded-For` can't be spoofed to forge the IP used for rate limiting and lockout.
- **Internal-endpoint authentication**: `/_internal/cluster/gossip` and `/_internal/backchannel-logout` require the `X-Cluster-Secret` header when `Cluster:Secret` is set, and otherwise only accept loopback / private source IPs.
- **Backup signing-key exclusion + restore integrity**: the `SigningKeys` table is excluded from backups by default (`Backup:IncludeSigningKeys` to opt in), and restores verify file SHA-256 hashes against the manifest before writing.

## [0.3.0], 2026-05

### Added

- **JIT provisioning control**: just-in-time user provisioning on federated login is now configurable.
- **Partial SAML connection updates**: `PUT /api/v1/saml/connections/{connectionId}` supports partial updates of SAML provider configuration.

## [0.2.6]

### Added

- **Client admin API**: runtime CRUD for OAuth clients under `/api/v1/clients`, guarded by an `IClientScopeGuard` so callers can only grant scopes they are entitled to. Secret hashes are never returned.
- **Provisioning app admin API**: runtime CRUD for downstream provisioning apps under `/api/v1/provisioning/apps`, with a configurable per-deployment quota (`IProvisioningAppQuota`) and a `/test` connectivity check.

## [0.2.5]

### Fixed

- **JWT signature encoding**: corrected signature encoding for issued tokens.

### Changed

- **Release workflow**: optimized the build/release pipeline.

## [0.2.4], 2026

### Changed

- **Vault Transit ES256**: refactored the HashiCorp Vault Transit signing integration for proper ES256 (ECDSA P-256) support.

## [0.2.3]

### Changed

- Version bump across packages and demos.

## [0.2.2]

### Changed

- **ES256 signing**: migrated JWT signing from RS256 (RSA-2048) to ES256 (ECDSA P-256).
- **`@authagonal` package scope**: npm packages published under the `@authagonal` scope; added TypeScript type annotations.

## [0.2.1]

### Changed

- **Table store partitioning**: integrated the environment-aware partitioner into the table stores for sandbox table isolation.

## [0.2.0]

### Added

- **Pushed Authorization Requests (PAR, RFC 9126)**: clients can POST authorize parameters to `/connect/par` and receive a short-lived `request_uri`. Per-client enforcement via `RequirePushedAuthorizationRequests`. See [PAR](par).
- **OIDC federation**: extended upstream OIDC federation: upstream claim propagation, scope forwarding, and upstream session caps.

## [0.1.86], 2026-04-17

### Added

- **Dark mode**: login app supports light, dark, and system theme preferences. System color scheme is detected via `prefers-color-scheme` and user selection persists across browser sessions. New `useDarkMode` hook drives the theme toggle; all UI primitives (Alert, Button, Card, Input, Label, Separator) and pages (Login, Register, Consent, Device, Grants, MfaSetup, ResetPassword) updated with dark-mode styling. Tenant branding CSS variables still override theme defaults.

### Changed

- **Grant storage**: `GrantByExpiryEntity` partitioning refined for more reliable TTL sweeping of expired grants.

## [0.1.85], 2026-04-17

### Added

- **OAuth scope management**: admin CRUD endpoints under `/api/v1/scopes` backed by a new `ScopeEntity` table. Scopes defined at runtime are advertised by the discovery document and available to the consent screen alongside built-in scopes. New `IScopeStore` / `TableScopeStore` abstractions.
- **Dynamic client registration (RFC 7591)**: `POST /connect/register` endpoint allows OAuth clients to self-register at runtime. Gated by `Auth:DynamicClientRegistrationEnabled` (default off) and advertised via `registration_endpoint` in the discovery document when enabled.
- **Front-channel logout (RFC 7711)**: `EndSessionEndpoint` now supports front-channel logout URIs with configurable session requirements per client. Logs out of the authorization server and notifies registered clients via the browser.
- **Token revocation tracking**: access tokens can be invalidated before their natural expiry. New `IRevokedTokenStore` / `TableRevokedTokenStore` back a persistent revocation list consulted by the introspection endpoint. `RevokedTokens` table added to backup defaults.
- **Custom user attributes**: `AuthUser` gained a `CustomAttributes` dictionary for tenant-specific user metadata.
- **Client audience**: `OAuthClient` gained an `Audience` field that flows into issued access tokens.

### Changed

- **Discovery document**: now advertises custom scopes and the registration endpoint dynamically based on configured state.

## [0.1.84], 2026-04-16

### Fixed

- **`TokenResponse` JSON serialization**: registered in `AuthagonalJsonContext` so token endpoint responses serialize correctly under Native AOT.

## [0.1.83], 2026-04-16

### Added

- **Table Storage backup whitepaper**: `docs/whitepaper-table-storage-backup.md` documents the backup strategy: full/incremental runs, tombstone tracking for deletes, merge/rollup operations, and restore procedures.

### Fixed

- **Auth request DTO JSON serialization**: registered `LoginRequest`, `RegisterRequest`, `ConfirmEmailRequest`, `ForgotPasswordRequest`, `ResetPasswordRequest`, `MfaVerifyRequest`, `TotpConfirmRequest`, and `WebAuthnConfirmRequest` in `AuthagonalJsonContext` for AOT compatibility.

## [0.1.82], 2026-04-15

### Added

- **SAML assertion replay detection**: IdP-initiated SAML flows now check incoming assertions against a replay cache (`SamlReplayCache`), rejecting re-submitted assertions.
- **Absolute session expiration**: sessions now enforce a 7-day hard cap regardless of sliding renewal.
- **Constant-time SCIM token comparison**: `ScimBearerAuthenticationHandler` uses constant-time comparison on token hashes to prevent timing attacks.

### Changed

- **Password reset tokens**: reset flows now use single-use persisted grants instead of the user security stamp, enabling explicit invalidation and preventing token reuse.
- **PBKDF2 iterations increased**: password hashing cost raised in `AuthOptions`.
- **Refresh token grace period reduced**: shorter window for accepting a refresh token after rotation.
- **SCIM endpoints scoped to client**: SCIM requests are now scoped to the requesting OAuth client with per-client rate limiting, preventing cross-client enumeration.
- **Consent data error handling**: improved logging and error paths when persisted consent data is malformed.

## [0.1.74], 2026-04-10

### Changed

- **Request DTOs as classes**: all request DTOs converted from records to classes for Native AOT compatibility.

## [0.1.73], 2026-04-10

### Changed

- **`ScimPatchOperation.Op` optional**: `Op` property on `ScimPatchOperation` is now optional, improving compatibility with SCIM clients that omit it for replace operations.

## [0.1.72], 2026-04-10

### Fixed

- **Card UI imports**: added missing Card component imports in login-app pages.

## [0.1.71], 2026-04-10

### Fixed

- **Unused imports**: removed unused imports across login-app source files.

## [0.1.70], 2026-04-10

### Added

- **Native AOT support**: enabled IL trimming and source-generated JSON serialization across all packages (`Authagonal.Server`, `Authagonal.Core`, `Authagonal.Storage`, `Authagonal.Backup`). `AuthagonalJsonContext`, `StorageJsonContext`, and `BackupJsonContext` provide trim-safe serialization for 40+ types. `EnableRequestDelegateGenerator` and `EnableConfigurationBindingGenerator` enabled for minimal API AOT compatibility.

### Changed

- **Slimmed NuGet dependencies**: removed unused packages and resolved a security vulnerability.

## [0.1.69], 2026-04-09

### Added

- **OAuth consent screen**: clients with `RequireConsent: true` prompt users to approve requested scopes before issuing an authorization code. Consent decisions are persisted (5-year TTL) and re-prompted only when new scopes are requested.
  - `GET /consent/info`, returns client name and requested scopes for the consent page.
  - `POST /consent`, records the user's allow/deny decision.
  - `GET /consent/grants`, lists all applications the user has authorized.
  - `DELETE /consent/grants/{clientId}`, revokes consent for a specific application.
  - `ConsentPage` and `GrantsPage` added to the login SPA with full i18n support.
- **Backup integrity verification**: `BackupManifest` now includes a `FileHashes` dictionary (SHA-256) populated during backup creation and verified during restore.
- **Enhanced login UI customization**: added `data-auth` attributes to all login form elements for CSS targeting and test automation. New CSS custom properties: `--auth-bg`, `--auth-card-bg`, `--auth-radius`, `--auth-font`, `--auth-heading`.

## [0.1.68], 2026-04-09

### Changed

- Package version bump.

## [0.1.67], 2026-04-09

### Changed

- **Email service: SendGrid → Resend**: the built-in `EmailService` now uses the [Resend](https://resend.com) API instead of SendGrid. Configuration keys changed from `Email:SendGridApiKey` to `Email:ResendApiKey`. The `IEmailService` interface is unchanged, custom implementations are unaffected.

## [0.1.66], 2026-04-09

### Added

- **OIDC/SAML provider configuration properties**: `OidcProviderConfig` and `SamlProviderConfig` models extended with new properties. OIDC and SAML endpoints updated to support provider-level configuration.

### Changed

- Upgraded `Authagonal.Server` and `Authagonal.Storage` to 0.1.66.

## [0.1.65], 2026-04-09

### Added

- **OIDC Back-Channel Logout**: implements Back-Channel Logout 1.0. When a user logs out, Authagonal sends logout tokens to each client's registered `BackChannelLogoutUri` (fire-and-forget).
- **OAuth 2.0 Token Introspection**: `POST /connect/introspect` (RFC 7662) allows resource servers to verify token validity and retrieve claims. Authenticated via client credentials.
- **`BackChannelLogoutUri` on `OAuthClient`**: new optional property for registering a client's back-channel logout endpoint.
- **Comprehensive test coverage**: new test suites for admin SSO endpoints, OIDC SSO flow, SAML flow, SCIM discovery endpoints, and token introspection. Added `OidcMockHandler` and `SamlTestHelper` test utilities.

### Changed

- Discovery endpoint now advertises `introspection_endpoint` and `backchannel_logout_supported`.
- `EndSessionEndpoint` sends back-channel logout notifications to all clients with a registered logout URI.

## [0.1.64], 2026-04-09

### Added

- **Admin role endpoint tests**: full CRUD and assign/unassign coverage.
- **Admin SCIM token endpoint tests**: create, list, and revoke token coverage.
- **Device authorization flow tests**: end-to-end device code grant coverage.
- **Token edge case tests**: expired tokens, invalid grants, scope handling.
- **End session tests**: logout and session cleanup coverage.

### Fixed

- **SCIM pagination offset**: `startIndex` now correctly zero-indexed internally, fixing off-by-one in paginated SCIM list responses.

### Changed

- **`VaultTransitClient` testability**: removed `sealed` modifier and made methods `virtual` so the client can be mocked in tests.

## [0.1.63], 2026-04-09

### Changed

- **Span-based signing**: `VaultTransitSignatureProvider` now overrides the `Sign(byte[], int, int)` span-based method for more efficient memory handling and better P/Invoke interop support.

## [0.1.62], 2026-04-08

### Added

- **HashiCorp Vault Transit integration**: `VaultTransitClient`, `VaultTransitCryptoProvider`, `VaultTransitSecurityKey`, and `VaultTransitSignatureProvider` enable remote cryptographic key signing via Vault's Transit secrets engine. Allows JWT signing without local private key access.

### Changed

- **PBKDF2 iterations**: reduced from 100,000 to 50,000 to balance security with performance.

## [0.1.60], 2026-04-08

### Added

- **OAuth Device Authorization Grant**: implements RFC 8628 for input-constrained devices (smart TVs, CLIs, IoT).
  - `POST /connect/deviceauthorization`, initiates a device flow, returns `device_code`, `user_code`, and `verification_uri`.
  - Device code approval page, authenticated users enter the user code to authorize the device.
  - `urn:ietf:params:oauth:grant-type:device_code` grant type on the token endpoint, devices poll until the user approves.
- **Device authorization UI**: `DevicePage` added to the login SPA for user code entry and approval.
- Discovery endpoint now advertises `device_authorization_endpoint`.

## [0.1.59], 2026-04-08

### Changed

- **Auto-confirm `@example.com` emails**: user registration automatically confirms email addresses ending with `@example.com`, simplifying demo and test workflows.

## [0.1.58], 2026-04-08

This release is a re-cut of 0.1.57 with package version bumps. See [0.1.57] for the full feature list.

## [0.1.57], 2026-04-08

### Added

- **Auto-provisioning on all creation endpoints**: TCC provisioning now triggers when users are created via admin API, self-service registration, SAML SSO, OIDC SSO, and SCIM. If any provisioning app rejects the user in the Try phase, the user is deleted and the endpoint returns `422`.
- **`IProvisioningAppProvider`**: new extensibility point for resolving available provisioning apps dynamically. Default `ConfigProvisioningAppProvider` reads from `appsettings.json`. Override to resolve apps per-tenant or from an external source.
- **`SigningKeyOps` utility**: extracted signing key operations (generate, rotate, build JWKS) into a reusable static class. Enables both single-tenant `KeyManager` and multi-tenant wrappers to share key logic.
- **`RegisterPage` and `App` exports**: `@drawboard/authagonal-login` now exports the built-in registration page and a standalone `App` component with full routing for consumers who want the complete SPA.
- **Enter key SSO check**: email field on the login page accepts Enter to trigger SSO domain check (no longer requires tab/blur).
- **Load testing framework**: k6-based test suite (`tests/load/`) with smoke, stress, and soak tests targeting the demo deployment. GitHub Actions workflow runs smoke after every deploy and soak daily.
- **`Authagonal.Backup` NuGet package**: backup library now published to nuget.org alongside Server, Core, and Storage.

### Changed

- **Multi-tenant dependency resolution**: `IClientStore`, `IScimTokenStore`, and `IUserProvisionStore` now resolved from `HttpContext.RequestServices` for per-request tenant isolation. `IKeyManager` supports scoped registration for per-tenant signing keys.
- **`AddAuthagonalCore()` / `AddAuthagonal()` split**: `AddAuthagonalCore()` registers services safe for both single and multi-tenant hosts. `AddAuthagonal()` adds the full single-tenant stack (stores, KeyManager, background services). Multi-tenant hosts call `AddAuthagonalCore()` and register their own equivalents.
- **Conditional background services**: `GrantReconciliationService` and `SigningKeyRotationService` only register in single-tenant mode. Multi-tenant hosts manage these per-tenant.
- **Demo login pages**: `AcmeLoginPage` and `RegisterPage` refactored to use the library's UI components (`Button`, `Input`, `Label`, `Alert`, `CardTitle`, `CardFooter`) instead of the old pre-Tailwind CSS classes.

## [0.1.42], 2026-04-06

### Fixed

- **npm package CSS**: login-app now imports `styles.css` as a side effect in the entry point, so Vite emits `dist/index.css` in library mode. Fixed export map to point to `dist/index.css`.
- **npm package types**: swapped build order to `vite build && tsc -b` so Vite's `emptyOutDir` doesn't wipe TypeScript declaration files.

## [0.1.38], 2026-04-05

### Changed

- **Compiled library**: `@drawboard/authagonal-login` now ships compiled JS + CSS in `dist/` instead of raw TypeScript source. Consumers no longer need Tailwind or the Vite build toolchain.

## [0.1.31], 2026-03-31

### Added

- **Self-service registration**: `POST /api/auth/register` endpoint with distributed per-IP rate limiting (5 registrations per IP per hour, configurable).
- **Distributed rate limiting**: CRDT G-Counter shared via gossip protocol across cluster instances.
- **MIT license**: switched from proprietary to MIT.

### Changed

- **Cluster node identity**: each instance generates a random hex node ID at startup for gossip protocol identification.
- **Cluster leader election**: `ClusterLeaderService` elects a single leader among discovered peers for cluster-wide coordination.

## [0.1.30], 2026-03-30

### Added

- **Role-based access control**: `Role` model, `IRoleStore`/`TableRoleStore`, and admin API endpoints at `/api/v1/roles/` for CRUD, assign, unassign, and user-role queries.
- **Multi-tenant abstractions**: `ITenantContext` and `IKeyManager` interfaces for per-tenant configuration and signing key isolation. `DefaultTenantContext` reads from `IConfiguration`.
- **Tailwind CSS**: login SPA migrated from custom CSS to Tailwind. Reusable UI components exported: `Button`, `Input`, `Label`, `Card`, `Alert`, `Separator`, `cn`.
- **Multiple IAuthHook support**: all registered `IAuthHook` implementations now run in registration order via `IEnumerable<IAuthHook>` pipeline.
- **Backup & restore library**: `src/Authagonal.Backup` with `BackupService`, `RestoreService`, tombstone-based delete tracking, merge and rollup for backup compaction.

## [0.1.26], 2026-03-30

### Fixed

- **SCIM base URL handler**: Entra ID hits `/scim/v2` directly during credential validation. Added a handler that returns `ServiceProviderConfig` instead of falling through to the SPA catch-all.

## [0.1.25], 2026-03-30

### Added

- **Entra SAML integration**: configured Entra ID enterprise app for SAML SSO in the demo environment.
- **SAML login hint passthrough**: email entered on the login page is now passed to the SAML IdP via both the `Subject/NameID` element in the AuthnRequest and the `login_hint` query parameter.
- **MFA back navigation**: MFA setup page accepts a `backUrl` parameter, allowing users to return to the originating app after managing MFA settings.
- **Sample app tab UI**: logged-in view now uses horizontal tabs (Profile, API Explorer, Token) with an MFA Settings link.

## [0.1.24], 2026-03-30

### Added

- **SCIM 2.0 provisioning**: full inbound user and group provisioning from enterprise identity providers (Microsoft Entra ID, Okta, OneLogin).
  - `POST /scim/v2/Users`, `GET /scim/v2/Users`, `GET /scim/v2/Users/{id}`, `PUT /scim/v2/Users/{id}`, `PATCH /scim/v2/Users/{id}`, `DELETE /scim/v2/Users/{id}`, user CRUD with soft-delete deactivation.
  - `POST /scim/v2/Groups`, `GET /scim/v2/Groups`, `GET /scim/v2/Groups/{id}`, `PUT /scim/v2/Groups/{id}`, `PATCH /scim/v2/Groups/{id}`, `DELETE /scim/v2/Groups/{id}`, group CRUD with member add/remove via PATCH.
  - `GET /scim/v2/ServiceProviderConfig`, `GET /scim/v2/Schemas`, `GET /scim/v2/ResourceTypes`, SCIM discovery endpoints.
  - SCIM filter support: `eq` on `userName`/`externalId`/`displayName`, `co` on `displayName`.
  - Paginated list responses with `startIndex` and `count`.
- **SCIM token authentication**: per-client static Bearer tokens (stored SHA-256 hashed). Custom `ScimBearer` authentication scheme and `ScimProvisioning` authorization policy.
  - `POST /api/v1/scim/tokens`, generate a SCIM token for a client (returns raw token once).
  - `GET /api/v1/scim/tokens?clientId={id}`, list tokens (metadata only).
  - `DELETE /api/v1/scim/tokens/{tokenId}?clientId={id}`, revoke a token.
- **SCIM group model**: `ScimGroup` with `DisplayName`, `ExternalId`, `OrganizationId`, and `MemberUserIds`. `IScimGroupStore` interface and `TableScimGroupStore` Azure Table Storage implementation.
- **User externalId**: `AuthUser.ExternalId` property for IdP-assigned identifiers. `UserExternalIds` table provides O(1) lookup by `(clientId, externalId)`.
- **IsActive guard**: deactivated users (`IsActive = false`) are rejected at password login, SAML SSO, OIDC SSO, refresh token exchange, and cookie validation.
- **SCIM-triggered TCC provisioning**: SCIM user creation triggers downstream TCC provisioning if the client has `ProvisioningApps` configured.
- **SCIM documentation**: `docs/scim.md` with IdP setup guides (Entra ID, Okta, OneLogin), endpoint reference, and attribute mapping. Localized stubs for de, es, fr, pt, vi, zh-Hans.

### Changed

- **`AuthUser` model**: added `ExternalId`, `IsActive` (default `true`), `ScimProvisionedByClientId` properties.
- **`IUserStore`**: added `FindByExternalIdAsync`, `ListAsync`, `SetExternalIdAsync`, `RemoveExternalIdAsync` methods.
- **`TableUserStore`**: now accepts a `userExternalIdsTable` parameter; implements new `IUserStore` methods.
- **`ServiceCollectionExtensions`**: registers 4 new tables (`UserExternalIds`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`) and 2 new stores (`IScimTokenStore`, `IScimGroupStore`).
- **`AuthagonalExtensions`**: adds `ScimBearer` auth scheme, `ScimProvisioning` policy, wires SCIM endpoints.
- **Cookie validation**: `OnValidatePrincipal` now rejects inactive users.
- **Token refresh**: `HandleRefreshTokenAsync` now rejects deactivated users.

### Fixed

- **QR code test**: `TotpSetup_ReturnsQrCodeAndSetupToken` test assertion updated from SVG to PNG to match actual QR code output format.

## [0.1.9], 2026-03-29

### Improved

- **Login UX**: added a "Continue" button after the email field instead of relying on a hidden blur-triggered SSO check. External provider buttons (e.g. Google) now collapse into a compact "Or sign in with..." link once the password field is shown, and can be expanded again by clicking the link.
- **Registration link always visible**: "Don't have an account? Create one" link is now shown below the form at all times, not just after the password field appears.
- **i18n completeness**: added `continue`, `noAccount`, `createAccount`, and `orSignInWith` translation keys across all 8 languages (en, de, es, fr, pt, vi, zh-Hans, tlh).

## [0.1.8], 2026-03-29

### Added

- **Multi-factor authentication**: TOTP (authenticator apps), WebAuthn/passkeys, and recovery codes. MFA is enforced per-client via `MfaPolicy` on the client configuration (`Disabled`, `Enabled`, `Required`).
  - `POST /api/auth/mfa/verify`, challenge verification (TOTP code, WebAuthn assertion, or recovery code)
  - `GET /api/auth/mfa/status`, enrolled methods for the current user
  - `POST /api/auth/mfa/totp/setup` + `POST /api/auth/mfa/totp/confirm`, TOTP enrollment with QR code
  - `POST /api/auth/mfa/webauthn/setup` + `POST /api/auth/mfa/webauthn/confirm`, passkey enrollment
  - `POST /api/auth/mfa/recovery/generate`, generate 10 one-time recovery codes
  - `DELETE /api/auth/mfa/credentials/{id}`, remove an enrolled method
- **MFA admin endpoints**: `GET /api/v1/profile/{userId}/mfa`, `DELETE /api/v1/profile/{userId}/mfa`, `DELETE /api/v1/profile/{userId}/mfa/{id}` for admin MFA management.
- **MFA policy hook**: `IAuthHook.ResolveMfaPolicyAsync` allows per-user/org override of the client's MFA policy. `IAuthHook.OnMfaVerifiedAsync` fires after successful MFA verification.
- **Setup token flow**: when `MfaPolicy=Required` and the user has no MFA enrolled, login returns a setup token. The MFA setup endpoints accept this token via `X-MFA-Setup-Token` header, allowing enrollment before cookie authentication.
- **MFA frontend**: `MfaChallengePage` (TOTP/passkey/recovery code entry) and `MfaSetupPage` (QR code scanning, passkey registration, recovery code generation) added to the login SPA.
- **Demo: self-service registration**: `POST /api/auth/register` endpoint for the demo server, plus a registration page in the demo login app.
- **Demo: user purge**: background service deletes demo users older than 24 hours.
- **Table Storage restore tool**: `tools/Authagonal.Restore/` reads `.jsonl` backups produced by `authagonal-backup` and restores to Table Storage. Supports `upsert`, `merge`, and `clean` modes.

### Changed

- **Login response**: `POST /api/auth/login` may now return `{ mfaRequired, challengeId, methods, webAuthn }` or `{ mfaSetupRequired, setupToken }` instead of setting a cookie directly.
- **Fido2.AspNet** dependency added for WebAuthn credential verification.
- **QRCoder** dependency added for server-side QR code generation.

## [0.1.5], 2026-03-29

### Added

- **Integration test suite**: 48 API endpoint tests covering health, discovery/JWKS, auth (login, session, logout, SSO, lockout, password reset), OAuth (client_credentials, authorization code + PKCE, refresh tokens, revocation, userinfo), and admin endpoints (user CRUD, external identity linking). All tests use an in-memory test server with no external dependencies.
- **ASP.NET Identity hash compatibility**: `PasswordHasher` now verifies ASP.NET Identity V3 hashes (PBKDF2 with SHA1/256/384/512, variable iterations and salt sizes). Migrated users are auto-upgraded to the native format on next login.
- **Configurable admin scope**: `AdminApi:Scope` setting (default `authagonal-admin`) controls which JWT scope grants admin access. Set to `projects-identity-admin` for IdentityServer migration compatibility.
- **`NullEmailService`**: no-op email service is now the default. Register a real `IEmailService` (e.g., the built-in SendGrid `EmailService`) before `AddAuthagonal()` to enable email delivery.

## [0.1.4], 2026-03-29

### Added

- **npm package README**: `@drawboard/authagonal-login` now includes a comprehensive README covering installation, quick start, page customization, full API client reference, branding, i18n, and exports.
- **Docs favicon**: documentation site now uses the Authagonal logo as its browser tab icon.

## [0.1.3], 2026-03-29

### Changed

- **Demo consumes published packages**: demo `CustomAuthServer` now references NuGet packages from nuget.org and `@drawboard/authagonal-login` from npm, instead of building from source. The Docker build no longer needs the login-app source tree.
- **CI release workflow**: consolidated `nuget.yml`, `npm.yml`, and tag-triggered Docker builds into a single `release.yml` with proper job ordering: publish NuGet → wait for indexing → publish npm → wait for indexing → build Docker images → deploy. Eliminates the race condition where Docker builds could start before packages were available on registries.
- **Docker workflow**: `docker.yml` now only triggers on `master` branch pushes (tag builds handled by `release.yml`).

## [0.1.1], 2026-03-29

### Fixed

- **i18n module duplication**: consumers importing `useTranslation` from their own `react-i18next` copy got a different instance than the one initialized by the base package. Fixed by re-exporting `useTranslation` from `@drawboard/authagonal-login`.
- **OAuth returnUrl dropped on SSO redirect**: the authorize endpoint generated a full URL as the returnUrl, which was rejected by both client-side `isSafeReturnUrl()` and server-side `SanitizeReturnUrl()` (both require relative paths). Fixed by using a relative path.
- **Language detection not persisting**: added `localStorage` to `i18next-browser-languagedetector` order and caches arrays.
- **OIDC error display**: login page now reads `error` / `error_description` from URL params and displays them.

### Added

- **Localizable branding strings**: `welcomeTitle` and `welcomeSubtitle` in `branding.json` now accept `LocalizedString` (a plain string or a `{ "en": "...", "es": "..." }` object). New `resolveLocalized()` helper resolves the best match for the current language.
- **Sign-out button**: login page detects existing sessions and shows a "Signed in as" view with a sign-out button, instead of showing the login form.
- **NuGet package READMEs**: `Authagonal.Server`, `Authagonal.Core`, and `Authagonal.Storage` now include README files displayed on nuget.org.
- **i18n keys**: added `signedInAs`, `signedInMessage`, `signOut`, `welcomeTitle`, `welcomeSubtitle`, `continueWith`, `or` to all 7 language files.

## [0.1.0], 2026-03-29

### Added

- **Docker packaging**: multi-stage `Dockerfile` builds the React SPA and .NET server into a single image. SPA served as static files from `wwwroot/` on the same origin as the API.
- **`Dockerfile.migration`**: separate image for the SQL Server → Table Storage migration tool.
- **`docker-compose.yml`**: local development setup with Azurite storage emulator.
- **Static file serving**: `UseDefaultFiles()`, `UseStaticFiles()`, and `MapFallbackToFile("index.html")` added to `Program.cs` for SPA hosting.
- **TCC provisioning system**: replaces the single-webhook provisioning with a Try-Confirm-Cancel pattern:
  - N provisioning apps defined in configuration (`ProvisioningApps` section) with callback URLs and API keys.
  - Clients declare which apps they provision into via `ProvisioningApps` field.
  - Provisioning runs at the authorize endpoint, before a code is issued, the user is provisioned into all required apps.
  - Per-user/per-app tracking in the `UserProvisions` table prevents re-provisioning on subsequent logins.
  - Try phase: calls each app's `/try` endpoint with user details, app can approve or reject.
  - Confirm phase: on all-approve, calls `/confirm` on each app to commit.
  - Cancel phase: on any failure, calls `/cancel` on successful tries to clean up.
  - Partial confirm failure: stores provision records for confirmed apps so only failed ones are retried.
- **`IProvisioningOrchestrator`** interface and `TccProvisioningOrchestrator` implementation.
- **`IUserProvisionStore`** interface and `TableUserProvisionStore` for Azure Table Storage.
- **`UserProvisionEntity`**: table entity keyed by `(userId, appId)`.
- **Deprovision on user delete**: `DELETE /api/v1/profile/{userId}` now calls `DeprovisionAllAsync` to notify all downstream apps.
- **Runtime branding**: login SPA reads `/branding.json` at startup for customization without rebuilding:
  - `appName`, header text and browser tab title.
  - `logoUrl`, replaces text header with an image.
  - `primaryColor`, buttons, links, focus rings (via CSS custom properties).
  - `showForgotPassword`, toggle the forgot password link.
  - `customCssUrl`, load additional CSS for deeper styling.
- **CSS custom properties**: primary color exposed as `--color-primary`, used throughout styles via `color-mix()` for hover/focus variants.
- **GitHub Pages documentation site**: overview, installation, quickstart, configuration, branding, provisioning, SAML, OIDC federation, admin API, auth API, and migration guides.
- **`IAuthHook` extensibility point**: lifecycle hooks for authentication events. Implementations are called on login success/failure, user creation, and token issuance. Throw from a hook to abort the operation (e.g., reject a login). Default implementation is a no-op (`NullAuthHook`).
  - `OnUserAuthenticatedAsync`, after password, SAML, or OIDC login.
  - `OnUserCreatedAsync`, after user creation via SSO or admin API.
  - `OnLoginFailedAsync`, on invalid credentials or lockout.
  - `OnTokenIssuedAsync`, when tokens are issued via the token endpoint.
- **Composable extension methods**: `AddAuthagonal()`, `UseAuthagonal()`, `MapAuthagonalEndpoints()` allow hosting Authagonal as a library in any ASP.NET Core application. Override `IEmailService`, `IAuthHook`, `IProvisioningOrchestrator`, or `ISecretProvider` by registering before `AddAuthagonal()`.
- **Demo: custom-server**: shows hosting Authagonal with custom `IAuthHook` (audit logging), custom `IEmailService` (console output), custom branding, and custom endpoints.
- **Demo: sample-app**: shows a client application (ASP.NET API + React SPA) authenticating via Authagonal using OIDC authorization code + PKCE, with protected API calls using JWT bearer tokens.
- **Demo: custom-server frontend**: custom login-app that installs `@drawboard/authagonal-login` as an npm dependency and overrides `LoginPage` (adds Terms of Service checkbox) and `AuthLayout` (adds branded footer), while reusing base `ForgotPasswordPage` and `ResetPasswordPage` as-is.
- **Configurable password policy**: `PasswordPolicy` configuration section controls min length, character requirements. `GET /api/auth/password-policy` endpoint exposes rules dynamically. Frontend fetches policy instead of hardcoding. Password validation now enforced on admin user registration too.
- **SAML/OIDC providers from configuration**: `SamlProviders` and `OidcProviders` config sections seed identity providers on startup (same pattern as `Clients`). SSO domain mappings are registered automatically from `AllowedDomains`.
- **`ProviderSeedService`**: `IHostedService` that seeds SAML and OIDC providers from configuration, with secret protection via `ISecretProvider`.
- **Login-app component library exports**: `@drawboard/authagonal-login` now exports all components, pages, branding hooks, API client, and types via `src/index.ts` with proper `exports` field in package.json. Consumers can `npm install` and import individual pieces.
- **CI/CD**: GitHub Actions workflows for Docker Hub publishing (`drawboardci/authagonal`, `drawboardci/authagonal-migration`) and npm publishing (`@drawboard/authagonal-login`).
- **i18n**: login SPA supports 7 languages (English, Chinese Simplified, German, French, Spanish, Vietnamese, Portuguese) with browser detection and language selector.
- **NuGet packaging**: `Authagonal.Server`, `Authagonal.Core`, `Authagonal.Storage` published to nuget.org.

### Changed

- **CSP header**: changed from `default-src 'none'` to `default-src 'self'; frame-ancestors 'none'; object-src 'none'` to allow the SPA to load resources from the same origin.
- **`OAuthClient` model**: added `ProvisioningApps` list field.
- **`ClientEntity`**: added `ProvisioningAppsJson` column (JSON-serialized string array, same pattern as other list fields).
- **`ServiceCollectionExtensions`**: registers `UserProvisions` table and `IUserProvisionStore`.
- **Authorize endpoint**: now injects `IUserStore` and `IProvisioningOrchestrator`, runs TCC provisioning between auth check and code issuance.
- **Admin `RegisterUser`**: no longer calls provisioning webhook; creates users with a generated GUID. Provisioning happens at authorize time.
- **Admin `DeleteUser`**: calls `IProvisioningOrchestrator.DeprovisionAllAsync` instead of `NotifyUserDeletedAsync`.
- **`Program.cs`**: refactored from inline setup to composable extension methods (`AddAuthagonal`, `UseAuthagonal`, `MapAuthagonalEndpoints`). Now 13 lines.
- **Token endpoint**: fires `IAuthHook.OnTokenIssuedAsync` on successful token issuance.
- **Auth endpoints**: fire `IAuthHook.OnUserAuthenticatedAsync` / `OnLoginFailedAsync`.
- **SAML/OIDC endpoints**: fire `IAuthHook.OnUserCreatedAsync` and `OnUserAuthenticatedAsync`.
- **Admin user endpoint**: fires `IAuthHook.OnUserCreatedAsync` on user registration; now validates password strength.
- **`PasswordValidator`**: refactored from hardcoded constants to accept a `PasswordPolicy` parameter.
- **`ResetPasswordPage`**: fetches password requirements from `GET /api/auth/password-policy` instead of hardcoding rules.
- **Scope rename**: `projects-identity-admin` renamed to `authagonal-admin`.

### Removed

- **`IUserProvisioningService`**: replaced by `IProvisioningOrchestrator`.
- **`UserProvisioningService`**: replaced by `TccProvisioningOrchestrator`.
- **Provisioning in SAML/OIDC flows**: SSO endpoints now just create users in Authagonal without external provisioning calls. Provisioning is deferred to the authorize endpoint where the client (and its required apps) are known.
- **`Provisioning:WebhookUrl` / `Provisioning:ApiKey` config**: replaced by per-app configuration under `ProvisioningApps`.

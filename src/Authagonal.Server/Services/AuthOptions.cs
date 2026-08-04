namespace Authagonal.Server.Services;

/// <summary>
/// Configuration for authentication, rate limiting, and token expiry settings.
/// Bound from the "Auth" configuration section.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>
    /// Allows the OAuth endpoints (<c>/connect/*</c>) to answer plaintext http requests. Default
    /// false: a non-https request to those endpoints is refused with <c>invalid_request</c>.
    /// </summary>
    /// <remarks>
    /// RFC 6749 §3.1 and §3.2 require TLS at the authorization and token endpoints, and the reason is
    /// not ceremonial — a plaintext exchange hands anyone on the path the authorization code, the
    /// client secret in the Basic header, and the access and refresh tokens that come back. The scheme
    /// is read after forwarded-header processing, so terminating TLS at a proxy that sends
    /// <c>X-Forwarded-Proto: https</c> satisfies the gate; only a genuinely plaintext deployment needs
    /// this switch. It is deliberately explicit rather than inferred from the environment name: the
    /// shipped docker-compose and the test harness both speak http and both set it, and an operator
    /// who runs plaintext anywhere else has to say so.
    /// </remarks>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Refuse to start when the .NET shared framework is older than the security floor this server
    /// requires (9.0.18 / 10.0.10). Default false: the mismatch is a Critical log and the server starts.
    /// </summary>
    /// <remarks>
    /// The floor exists because the fixes for GHSA-37gx-xxp4-5rgx and GHSA-w3x6-4m5h-cxqf — both
    /// reachable from the anonymous SAML ACS endpoint — ship in the runtime, not in a package this
    /// library can pin, so no dependency of yours can guarantee them. Default false because refusing
    /// would turn a version bump of this package into an outage on a fleet whose runtime is one patch
    /// behind; set it true where not starting is preferable to serving unauthenticated XML on an
    /// unpatched runtime.
    /// </remarks>
    public bool RequireMinimumRuntime { get; set; }

    /// <summary>
    /// Internal destinations this server may fetch from on the OPERATOR-configured outbound paths: upstream
    /// SAML metadata, upstream OIDC discovery (and the token / userinfo / JWKS endpoints that document
    /// names), and provisioning callbacks. Empty by default — every internal address is refused.
    /// </summary>
    /// <remarks>
    /// Server-initiated fetches refuse loopback, link-local, RFC1918 and ULA targets, at the URL and again
    /// at the socket, because a URL an attacker chose that names an internal host is SSRF. But federating
    /// with an IdP that is only reachable over a private network, or provisioning an app that runs in the
    /// same cluster, is an ordinary deployment for an auth product and it is refused by exactly the same
    /// rule. This is how an operator says which internal destinations are theirs.
    /// <para>
    /// It applies ONLY to targets that came from configuration or an admin API. A <c>jwks_uri</c>, a
    /// DCR-registered back-channel logout URI and every other registrant-supplied URL stay strict and
    /// cannot be widened by this or anything else, so opening a federation target does not also open the
    /// cloud metadata service to an anonymous <c>/connect/token</c> request.
    /// </para>
    /// <para>
    /// Entries: <c>idp.corp.internal</c> (that host and whatever it resolves to), <c>*.corp.internal</c>
    /// (any host under the suffix), <c>10.4.0.0/16</c> or <c>10.4.1.7</c> (that network or address, under
    /// any name). A malformed CIDR entry fails at startup rather than silently permitting nothing.
    /// </para>
    /// </remarks>
    public List<string> AllowedInternalTargets { get; set; } = [];

    /// <summary>
    /// Let the ambient HTTP proxy carry the operator-configured outbound fetches (SAML metadata, OIDC
    /// discovery, provisioning callbacks). Default false, because a proxied connection cannot be
    /// address-checked.
    /// </summary>
    /// <remarks>
    /// With a proxy in effect <c>SocketsHttpHandler</c> hands its <c>ConnectCallback</c> the PROXY's
    /// endpoint and never the target's, so the socket guard inspects the proxy, finds it routable, and
    /// permits everything — it fails OPEN, silently, in precisely the networks most likely to have a
    /// proxy. So the proxy is off on these clients unless an operator asks for it, and asking for it means
    /// accepting that only the URL check remains (scheme, literal addresses, internal name suffixes) and
    /// that a hostname resolving to an internal address is no longer caught.
    /// <para>
    /// This switch does not reach the registrant-supplied clients — the client <c>jwks_uri</c> fetch and
    /// back-channel logout delivery. Those targets are chosen by whoever registered the client, the
    /// address check is the only thing standing between an anonymous request and the internal network, and
    /// there is deliberately no configuration that turns it off. A deployment that must egress through a
    /// proxy needs an SSRF-filtering gateway for those, not a bypass here.
    /// </para>
    /// </remarks>
    public bool AllowOutboundProxy { get; set; }

    // --- Account lockout ---
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutDurationMinutes { get; set; } = 10;

    // --- Registration rate limiting ---
    /// <summary>
    /// Password attempts allowed per source address per <see cref="LoginWindowMinutes"/>.
    /// </summary>
    /// <remarks>
    /// Per-account lockout does not bound SPRAYING: one attempt each against ten thousand accounts never
    /// trips any account's counter. And because an unknown email is verified against a dummy hash to keep
    /// the response timing uniform (which is the right call — it prevents enumeration), every unauthenticated
    /// request pays a full PBKDF2, so the endpoint is a CPU amplifier as well. This is the bound on both.
    /// Generous enough for a household behind one NAT address.
    /// </remarks>
    public int MaxLoginAttemptsPerIp { get; set; } = 30;

    /// <summary>Window for <see cref="MaxLoginAttemptsPerIp"/>.</summary>
    public int LoginWindowMinutes { get; set; } = 5;

    public int MaxRegistrationsPerIp { get; set; } = 5;
    public int RegistrationWindowMinutes { get; set; } = 60;

    /// <summary>
    /// When true, registering an email that belongs to an existing account WITH NO LOCAL CREDENTIAL
    /// (a federated / JIT-provisioned account, PasswordHash null) STAGES a password on that account
    /// instead of returning the enumeration-neutral duplicate response. The staged credential and the
    /// claim's profile/attributes stay inert until the claimant clicks a fresh verification email — only
    /// then are they promoted and downstream provisioning re-runs — so merely KNOWING a federated
    /// account's email can't take it over. An account that ALREADY has a password is never affected —
    /// a re-register can never overwrite a real credential. Default OFF: a generic deployment treats
    /// every existing email as a duplicate.
    /// </summary>
    public bool AllowPasswordlessAccountClaim { get; set; }

    /// <summary>
    /// Custom-attribute keys a passwordless claim (<see cref="AllowPasswordlessAccountClaim"/>) may carry
    /// from the register request onto the claimed account. These reach downstream provisioning and can
    /// ride the real owner's tokens, so restricting them limits what a claim can inject. Empty (default)
    /// allows all keys — back-compat; list specific keys to restrict a claim to an expected set. Only
    /// consulted when a claim actually stages attributes.
    /// </summary>
    public List<string> ClaimAllowedAttributeKeys { get; set; } = [];

    // --- Password-reset rate limiting (per target email, so one address can't be email-bombed
    //     regardless of source IP) ---
    public int MaxPasswordResetsPerEmail { get; set; } = 3;
    public int PasswordResetWindowMinutes { get; set; } = 60;

    /// <summary>
    /// Forgot-password requests one source IP may make per <see cref="PasswordResetWindowMinutes"/>.
    /// </summary>
    /// <remarks>
    /// The per-email cap bounds mail to ONE victim; it does nothing about a caller working through an
    /// address list, which is unbounded anonymous mail delivery from the tenant's verified sending domain
    /// plus one user-store read per address (an unauthenticated enumeration and load primitive). Register
    /// has carried a per-IP cap all along; this is the same bound on the other mail-sending endpoint.
    /// Larger than <see cref="MaxRegistrationsPerIp"/> because a shared NAT egress legitimately produces
    /// several resets an hour, and unlike register the request is idempotent for the caller.
    /// </remarks>
    public int MaxPasswordResetsPerIp { get; set; } = 15;

    /// <summary>
    /// Email domains whose self-service registrations are auto-confirmed (skip the verification
    /// email). Empty by default — every registration must verify its email. Intended only for
    /// dev/test (e.g. <c>["example.com"]</c>); never list a domain that can receive real mail.
    /// </summary>
    public List<string> AutoConfirmEmailDomains { get; set; } = [];

    // --- Token / link expiry ---
    public int EmailVerificationExpiryHours { get; set; } = 24;
    public int PasswordResetExpiryMinutes { get; set; } = 60;
    public int MfaChallengeExpiryMinutes { get; set; } = 5;
    public int MfaSetupTokenExpiryMinutes { get; set; } = 15;

    /// <summary>
    /// Hosts permitted to act as a WebAuthn relying party. Empty (the default) accepts any host, which
    /// preserves existing deployments but leaves the origin and rpIdHash checks self-referential.
    /// </summary>
    /// <remarks>
    /// The RP ID and expected origin are derived from the request's own Host header, so without an
    /// allowlist the values a ceremony is validated AGAINST come from the request being validated.
    /// Set this to the real host list; on a multi-tenant deployment, to every tenant host.
    /// </remarks>
    public List<string> WebAuthnAllowedHosts { get; set; } = [];

    // --- Password hashing ---

    /// <summary>
    /// PBKDF2-HMAC-SHA256 work factor for newly written hashes. OWASP's 2026 Password Storage
    /// guidance is 600,000.
    /// </summary>
    /// <remarks>
    /// Raising this is now safe and is the intended way to keep pace: the cost is recorded in each
    /// hash (<c>PBKDF2v2$</c>), verification derives at the cost the hash carries, and a hash below
    /// this target is transparently re-written on the owner's next successful login. Previously the
    /// count was not stored, so verification re-derived at whatever this said and changing it
    /// invalidated every hash in the deployment at once — which is why the default sat at 100,000.
    /// </remarks>
    public int Pbkdf2Iterations { get; set; } = 600_000;

    /// <summary>
    /// Lower bound enforced at startup, so a typo or a copied dev config cannot silently weaken
    /// every password and client secret the deployment writes from then on.
    /// </summary>
    public const int MinimumPbkdf2Iterations = 100_000;

    /// <summary>
    /// Upper bound enforced at startup — the highest cost this server can still VERIFY.
    /// </summary>
    /// <remarks>
    /// The write path was uncapped and the verify path was capped, at different values, so raising
    /// <see cref="Pbkdf2Iterations"/> past this produced hashes the server refuses the moment it reads them
    /// back. <c>VerifyPbkdf2V2</c> returns <c>Failed</c> for a recorded count above this bound — correctly, since
    /// the count in a stored blob drives an uncancellable derivation reachable from an anonymous request — but
    /// the bound applied to this server's own freshly written hashes too, which is the one case it must not
    /// reject.
    /// <para>
    /// The consequence was silent and irreversible. Only a floor was validated at startup, so
    /// <c>Auth__Pbkdf2Iterations=2000000</c> (or a fat-fingered <c>6000000</c> for the documented 600,000) came
    /// up healthy and then wrote unverifiable hashes for every registration, password reset and admin
    /// set-password: those users could never log in, and each attempt incremented the lockout counter. Every
    /// DCR-issued and seeded client secret became permanently <c>invalid_client</c>. Fixing the configuration
    /// afterwards does not repair it — the cost is recorded in each stored blob, so the hashes written in the
    /// interim stay unverifiable and the credentials behind them have to be reset.
    /// </para>
    /// <para>
    /// A validated ceiling turns all of that into a refusal to start, naming the value.
    /// </para>
    /// </remarks>
    public const int MaximumPbkdf2Iterations = 1_000_000;

    /// <summary>
    /// Wall-clock floor, in milliseconds, that a failed login is held to before the uniform
    /// <c>invalid_credentials</c> response is written, measured from the start of the request.
    /// Closes the user-enumeration timing oracle: the no-such-user path verifies against a dummy
    /// hash in the native PBKDF2 format, but a real account may hold a bcrypt or ASP.NET Identity V3
    /// hash at a completely different cost, so equal work is not achievable and equal elapsed time
    /// is. Must sit above the slowest password hash the deployment holds or the pad is a no-op —
    /// raise it if you imported bcrypt hashes above cost 11 or raised <see cref="Pbkdf2Iterations"/>
    /// well past the default. A single warning is logged the first time a failed login overruns it.
    /// Set to 0 to disable the pad entirely (re-opens the oracle; intended only for load testing).
    /// </summary>
    public int FailedLoginMinimumMilliseconds { get; set; } = 250;

    // --- Refresh tokens ---
    /// <summary>
    /// Opt-in retry-race tolerance for refresh token rotation. When > 0, a consumed
    /// refresh token presented within this window of its consumption is treated as
    /// an idempotent retry: the successor tokens are re-delivered instead of
    /// triggering the replay-revoke policy. Set to 0 (default) to keep the strict
    /// "any reuse of a consumed token revokes everything" behaviour, which is the
    /// safer posture but causes user-visible logouts for clients that occasionally
    /// fire a second refresh with the same token (typically mobile apps with
    /// connectivity flaps). Matches Duende's ConsumedTokenUsageWindow concept.
    /// </summary>
    /// <summary>
    /// Window in which re-presenting an already-rotated refresh token returns its successor instead
    /// of being treated as replay. 0 (the default) means strict: any reuse revokes the grant family.
    /// </summary>
    /// <remarks>
    /// Left strict by default deliberately — it is the theft-detection behaviour, and weakening it
    /// for every deployment is not the right answer to one client topology. But a multi-instance BFF
    /// NEEDS a non-zero value: its refresh single-flight is per-process, so two replicas refreshing
    /// one session concurrently both redeem the same token, which strict mode reads as replay and
    /// answers by signing the user out everywhere. Set this (30 is the protocol layer's own default)
    /// when running more than one BFF instance, or any client that can refresh concurrently.
    /// </remarks>
    public int RefreshTokenReuseGraceSeconds { get; set; } = 0;

    // --- Signing keys ---
    public int SigningKeyLifetimeDays { get; set; } = 90;
    public int SigningKeyCacheRefreshMinutes { get; set; } = 60;

    // --- Key rotation ---
    public bool KeyRotationEnabled { get; set; }
    public int KeyRotationCheckIntervalMinutes { get; set; } = 360;
    public int KeyRotationLeadTimeDays { get; set; } = 14;

    /// <summary>
    /// Run the at-rest backfill once at startup, on the cluster leader. Default false.
    /// </summary>
    /// <remarks>
    /// Rewrites every existing user row and its profile-derived index rows to the current at-rest scheme —
    /// the migration path for enabling <c>IFieldCipher</c> / <c>IIndexTokenizer</c> on a deployment that
    /// already has data. The stores implemented it and nothing called it, so registering a cipher previously
    /// encrypted only rows written afterwards and left every existing row's PII in the clear.
    /// <para>
    /// Opt-in because it is real write volume and a migration rather than steady-state behaviour. Idempotent,
    /// so leaving it on costs one pass per restart; turn it off once the log reports a complete run.
    /// </para>
    /// </remarks>
    public bool AtRestBackfillEnabled { get; set; }

    // --- Cookie validation ---
    public int SecurityStampRevalidationMinutes { get; set; } = 30;

    // --- Dynamic client registration (RFC 7591) ---
    /// <summary>
    /// Enable the /connect/register endpoint. Off by default because open registration
    /// can be abused in multi-tenant deployments.
    /// </summary>
    public bool DynamicClientRegistrationEnabled { get; set; }

    // --- SCIM provisioning limits ---

    /// <summary>
    /// Most SCIM groups one provisioning client may own. Refuses the create past the cap.
    /// </summary>
    /// <remarks>
    /// Group storage is unindexed on every backend, so a list is a full scan — and worse,
    /// <c>GetGroupsByUserIdAsync</c> is that same scan and runs on EVERY token mint and every
    /// /connect/userinfo call for the tenant whenever a group→role mapping exists. Without a cap, a SCIM
    /// token could inflate its group table without bound and make every login in the tenant pay for it;
    /// the rate limiter only paces that, it does not bound the table. This is the bound. It is generous
    /// — far above any real directory — because its job is to stop amplification, not to price a plan.
    /// </remarks>
    public int MaxScimGroupsPerClient { get; set; } = 5_000;

    /// <summary>
    /// Most members one SCIM group may carry. Refuses the create/replace/patch past the cap.
    /// </summary>
    /// <remarks>
    /// Membership is stored as one uncapped list on the group row and every member is re-verified
    /// against the user store on write, so an unbounded array is both an unbounded row and an unbounded
    /// number of point reads inside one request.
    /// </remarks>
    public int MaxScimGroupMembers { get; set; } = 10_000;

    /// <summary>
    /// Scopes an anonymous registrant may put in its own <c>AllowedScopes</c>, on top of the four
    /// OIDC built-ins (<c>openid</c>, <c>profile</c>, <c>email</c>, <c>offline_access</c>) which are
    /// always registrable. Empty — the default — means the built-ins and nothing else.
    /// </summary>
    /// <remarks>
    /// The only test used to be "does this scope exist in the store", so a registrant could declare
    /// every API scope the deployment had ever defined — <c>billing.write</c>, <c>orders.read</c> —
    /// simply because they existed. Role-gated scopes were later refused, but the documented default
    /// for <see cref="Core.Models.Scope.AllowedRoles"/> is empty ("every scope until an operator says
    /// otherwise"), so the ungated majority stayed self-assignable. The per-user gate still means an
    /// unentitled user is not granted them, but the client should not be able to declare them, and a
    /// user staring at a consent screen listing <c>billing.write</c> next to an unvetted client_name
    /// has been handed a decision nobody vetted. An allowlist inverts the default: registration
    /// reaches what an operator named, not what happens to exist.
    /// </remarks>
    public List<string> DynamicClientRegistrationScopes { get; set; } = [];
}

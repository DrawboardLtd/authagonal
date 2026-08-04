using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Authagonal.Core.Authority;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Protocol.Services;

public sealed class ProtocolTokenService(
    IGrantStore grantStore,
    IClientStore clientStore,
    IScopeStore scopeStore,
    IKeyManager keyManager,
    ITenantContext tenantContext,
    IOidcSubjectResolver subjectResolver,
    ITokenExchangeSubjectTransformer exchangeTransformer,
    IOptions<AuthagonalProtocolOptions> protocolOptions,
    ILogger<ProtocolTokenService> logger,
    // Agentic seams — optional so hosts without agent storage (and existing manual
    // constructions) keep working; absent means "no client is an agent".
    IAgentProfileStore? agentProfileStore = null,
    IConnectorCatalog? connectorCatalog = null,
    IEnumerable<IAuthHook>? authHooks = null,
    // Optional for the same reason as the seams above (hand-constructed hosts), but wired everywhere
    // real: without it a REVOKED subject_token can still be exchanged for a fresh one.
    IRevokedTokenStore? revokedTokenStore = null,
    // Throttles the approval poll (RFC 8628-style slow_down). AddAuthagonalProtocol registers one, so
    // this is present in every composed host; optional only so a hand-constructed service still works,
    // and a host that registers its own gets that one's scoping (the Server's is per tenant).
    IRateLimiter? rateLimiter = null) : IProtocolTokenService
{
    /// <summary>
    /// Process-wide fallback for the approval-poll throttle when the host registers no
    /// <see cref="IRateLimiter"/>. Static because this service is SCOPED: a per-instance limiter would be
    /// a fresh (and therefore always-empty) window on every request, which is a throttle that cannot
    /// throttle.
    /// </summary>
    private static readonly IRateLimiter FallbackPollLimiter = new InProcessRateLimiter();

    private IRateLimiter PollLimiter => rateLimiter ?? FallbackPollLimiter;

    /// <summary>Per-user scope entitlement, re-applied at refresh. Built over the injected scope
    /// store rather than taken from DI so hosts that construct this service by hand keep working.</summary>
    private readonly IScopeRoleGate _scopeRoleGate = new ScopeRoleGate(scopeStore);

    private const int RefreshTokenSizeBytes = 64;
    private TimeSpan RefreshTokenReuseGraceWindow =>
        TimeSpan.FromSeconds(protocolOptions.Value.RefreshTokenReuseGraceSeconds);

    private string Issuer => tenantContext.Issuer;
    private IEnumerable<IAuthHook> Hooks => authHooks ?? [];

    // Protocol-level claims that custom attributes / additional claims must never shadow —
    // even if a scope lists them in UserClaims. Overriding these would let configuration
    // rewrite the OAuth/OIDC contract.
    private static readonly HashSet<string> ReservedClaimNames = new(StringComparer.Ordinal)
    {
        "iss", "sub", "aud", "exp", "nbf", "iat", "jti",
        "scope", "client_id", "nonce", "auth_time", "acr", "amr",
        "roles", "groups", "sid",
        AuthorityClaims.AuthorizationDetails, AuthorityClaims.Actor,

        // org_id is emitted as a first-class claim, but only when the subject actually has an
        // organization — so an account with none left an empty slot that a self-chosen custom
        // attribute could fill, given any scope in the tenant listing org_id in its UserClaims.
        "org_id",

        // The marker SAML/OIDC just-in-time provisioning writes to record that an account came from
        // a trusted upstream. Only the federation callbacks may assert it; from user-controlled
        // storage it is a forged provenance claim.
        "federated_connection",
    };

    public async Task<string> CreateAccessTokenAsync(
        OidcSubject? subject,
        OAuthClient client,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources = null,
        string? authorizationDetailsJson = null,
        string? actorJson = null,
        DateTimeOffset? notAfter = null,
        CancellationToken ct = default)
    {
        var minted = await MintAccessTokenAsync(
            subject, client, scopes, resources, authorizationDetailsJson, actorJson, notAfter, ct);
        return minted.Token;
    }

    /// <summary>
    /// The access-token mint, surfacing the <c>jti</c> and expiry the public
    /// <see cref="CreateAccessTokenAsync"/> discards.
    /// </summary>
    /// <remarks>
    /// Callers that also issue a refresh token record these on the refresh grant, so revoking the
    /// refresh token can revoke the access tokens minted under it — a self-contained JWT has no
    /// other kill switch. See <see cref="RefreshTokenData.AccessTokens"/>. The public signature is
    /// left alone because it is shipped surface on <see cref="IProtocolTokenService"/>.
    /// </remarks>
    private async Task<MintedAccessToken> MintAccessTokenAsync(
        OidcSubject? subject,
        OAuthClient client,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources = null,
        string? authorizationDetailsJson = null,
        string? actorJson = null,
        DateTimeOffset? notAfter = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var scopeList = scopes.ToList();

        // Last line of defence for the reserved-scope guards, at the one place every path converges.
        //
        // The `scope` claim is a SPACE-DELIMITED string (RFC 6749 §3.3) and every consumer splits it —
        // including the IdentityAdmin policy, which admits any token whose split scope claim contains
        // the administrative scope. Every ingress guard, by contrast, compares whole list ELEMENTS, so
        // a single stored entry "openid authagonal-admin" passed each of them and then flattened back
        // into two scopes right here. Rather than trust that no ingress will ever miss the split again,
        // the emission point refuses to write a scope claim that would parse back into more scopes than
        // it was handed.
        if (AdminScopeReservation.FindMalformedScope(scopeList) is { } malformed)
            throw new InvalidOperationException(
                $"Refusing to mint a token for client '{client.ClientId}': scope entry '{malformed}' is not a " +
                "single scope token. A scope containing whitespace expands into several scopes in the claim.");

        var jti = Guid.NewGuid().ToString("N");

        var claims = new Dictionary<string, object>
        {
            ["client_id"] = client.ClientId,
            ["scope"] = string.Join(' ', scopeList),
            ["jti"] = jti,
            ["iat"] = now.ToUnixTimeSeconds()
        };

        if (subject is not null)
        {
            claims["sub"] = subject.SubjectId;

            if (subject.Roles is { Count: > 0 })
                claims["roles"] = subject.Roles.ToArray();

            if (subject.Groups is { Count: > 0 })
                claims["groups"] = subject.Groups.ToArray();

            // The same §5.4 sets the id_token carries, under the same scope gates — because this host's
            // userinfo answers from the access token by design and had nothing to answer with.
            // AlwaysIncludeUserClaimsInIdToken is deliberately NOT honoured here: it is an id_token opt-out
            // whose name says so, and letting it widen an ACCESS token would release claims to resource
            // servers on a setting that never mentioned them.
            AddScopedIdentityClaims(claims, subject, scopeList, always: false);
        }

        // Scope-gated custom attributes — emitted only when a requested scope's UserClaims
        // whitelist releases them. Protocol/reserved claims always win.
        var allowedCustomClaims = await GetAllowedCustomClaimNamesAsync(scopeList, ct);
        if (subject?.CustomAttributes is not null)
        {
            MergeCustomClaims(claims, subject.CustomAttributes, allowedCustomClaims, overwriteExisting: false);
        }

        // Federation claims layered on top — same scope gate, and they FILL GAPS ONLY.
        //
        // These come verbatim from an upstream id_token, filtered by nothing but the protocol-reserved
        // list at the federation callback. They used to overwrite, on the reasoning that they describe
        // the authoritative state of the upstream-issued session — but the values they were overwriting
        // are this server's own: attributes an admin set on the user record, released by a scope this
        // deployment defined. So for any claim name a scope lists in UserClaims, a customer-controlled
        // IdP could restate it about their user and win, and a resource server reading that claim for
        // authorization (a plan, a tier, a department) was taking the upstream's word over the store's.
        // The upstream is authoritative only where this server holds nothing.
        if (subject?.FederationClaims is not null)
        {
            MergeCustomClaims(claims, subject.FederationClaims, allowedCustomClaims, overwriteExisting: false);
        }

        // Ungated additional claims — forced onto the token regardless of scope. Used for
        // bounded-scope tokens where the claim is the whole point (e.g. share-link tokens).
        if (subject?.AdditionalClaims is not null)
        {
            foreach (var (key, value) in subject.AdditionalClaims)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (ReservedClaimNames.Contains(key)) continue;
                claims[key] = value;
            }
        }

        // Agentic claims are first-class and reserved: authorization_details is a JSON array
        // and act a JSON object, so neither can ride the string-valued claim bags — and
        // An AGENT client with no authority claim at all is the same inversion as `[]` below, one step
        // earlier — and it was reachable on three grants.
        //
        // AgentMode was enforced in exactly two places: HandleClientCredentialsAsync refuses a
        // Delegated-only profile, HandleTokenExchangeAsync refuses a Service-mode one. No other mint path
        // consulted IAgentProfileStore, so authorization_code, refresh_token and device_code minted for an
        // agent exactly as for any other client: no authorization_details, no `act` chain, no
        // MaxTokenLifetimeSeconds clamp. AuthorityEvaluator.FromPrincipal returns Unrestricted for zero
        // claims (legacy scope-only compatibility), and AuthoritySet.Permits short-circuits on
        // IsUnrestricted — so the most tightly configured agent yielded the broadest possible token, from a
        // plain authorization_code flow against its own registered redirect URI. The ceiling, the standing
        // consent at /consent/agents, the ask-gate, the act provenance chain and MaxDelegationDepth were all
        // bypassed at once, and `action_policies` has no scope equivalent, so no amount of scope checking at
        // the resource server could recover the approval requirement.
        //
        // Applied HERE rather than at the three handlers because this is the one point every mint converges
        // on — the same reasoning as the reserved-scope guard above and the empty-array guard below. A
        // caller that already computed an authority (the exchange and client-credentials paths) is left
        // alone; this only fills the vacuum.
        //
        // Ask degrades to deny for the same reason it does on client_credentials: these paths have no one to
        // ask and no approval to consume. A ceiling that grants nothing unattended therefore has no safe
        // token to mint, and refusing is the only correct answer — omitting the claim would read as
        // unrestricted, which is the defect.
        if (string.IsNullOrEmpty(authorizationDetailsJson) && agentProfileStore is not null)
        {
            var agentProfile = await agentProfileStore.GetAsync(client.ClientId, ct);
            if (agentProfile is not null)
            {
                var ceiling = await ApplyHighRiskDefaultsAsync(
                    agentProfile.Ceiling, agentProfile.HighRiskDefault, ct);
                var unattended = MapAskPolicies(ceiling, ActionPolicy.Deny);

                if (AuthorityJson.SerializesToNothing(unattended))
                    throw new ProtocolTokenException("unauthorized_client",
                        $"Client '{client.ClientId}' is a registered agent whose ceiling grants nothing "
                        + "without human approval, and this grant has no way to obtain one. Use token "
                        + "exchange (which can park a pending approval), or widen the agent's ceiling.");

                authorizationDetailsJson = AuthorityJson.Serialize(unattended);

                var agentCap = now.AddSeconds(agentProfile.MaxTokenLifetimeSeconds);
                notAfter = notAfter is null
                    ? agentCap
                    : notAfter.Value < agentCap ? notAfter.Value : agentCap;
            }
        }

        // nothing in those bags can shadow them.
        if (!string.IsNullOrEmpty(authorizationDetailsJson))
        {
            using var doc = JsonDocument.Parse(authorizationDetailsJson);

            // The last line of defence, at the one point every mint passes through. An empty array must never
            // be signed: it flattens to zero claims, and zero claims is how AuthorityEvaluator recognises a
            // coarse scope-based token — so `[]` reads as UNRESTRICTED at every resource server, inverting the
            // meaning of the narrowest possible grant. Omitting the claim would read the same way, so there is
            // no safe token to issue here; the callers that can produce this refuse with a protocol error and
            // this exists to catch the next caller that forgets.
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() == 0)
                throw new InvalidOperationException(
                    "Refusing to mint a token with an empty authorization_details array: it evaluates as "
                    + "unrestricted authority. Refuse the request instead — see AuthorityJson.SerializesToNothing.");

            claims[AuthorityClaims.AuthorizationDetails] = doc.RootElement.Clone();
        }
        if (!string.IsNullOrEmpty(actorJson))
        {
            using var doc = JsonDocument.Parse(actorJson);
            claims[AuthorityClaims.Actor] = doc.RootElement.Clone();
        }

        var expires = EffectiveAccessTokenExpiry(subject, client, notAfter, now);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            IssuedAt = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = keyManager.GetSigningCredentials(),
            // RFC 9068 §2.1: an OAuth 2.0 access token in JWT form carries typ "at+jwt". Without it every
            // token this server signs had the identical header `typ: JWT` — access tokens, id_tokens and
            // both logout tokens — so nothing downstream could tell them apart by inspection. That is what
            // let an id_token or a back-channel logout token be presented as `subject_token` at
            // /connect/token and exchanged for a live access token. See TokenTypes.AccessTokenJwt.
            TokenType = TokenTypes.AccessTokenJwt,
            Claims = claims
        };

        // RFC 8707 — narrow aud to caller-specified resources when present, otherwise fall
        // back to the client's configured audiences, else client_id.
        var resourceList = resources?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (resourceList is { Count: > 0 })
        {
            claims["aud"] = resourceList.Count == 1 ? (object)resourceList[0] : resourceList.ToArray();
        }
        else if (client.Audiences.Count > 0)
        {
            claims["aud"] = client.Audiences.Count == 1 ? (object)client.Audiences[0] : client.Audiences.ToArray();
        }
        else
        {
            descriptor.Audience = client.ClientId;
        }

        var handler = new JsonWebTokenHandler();
        return new MintedAccessToken(handler.CreateToken(descriptor), jti, now, expires);
    }

    /// <summary>An access token together with the facts needed to revoke it and to describe it.</summary>
    private readonly record struct MintedAccessToken(
        string Token, string Jti, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)
    {
        /// <summary>
        /// RFC 6749 §5.1 <c>expires_in</c>: the lifetime of THIS token, not the client's configured
        /// ceiling.
        /// </summary>
        /// <remarks>
        /// Measured as <c>exp - iat</c> — the token's own stamped lifetime — rather than against a
        /// later clock read, so an unclamped token reports exactly its configured lifetime instead of
        /// one second less because a few milliseconds elapsed between minting and responding.
        /// </remarks>
        public int ExpiresInSeconds =>
            Math.Max(0, (int)Math.Round((ExpiresAt - IssuedAt).TotalSeconds));
    }

    /// <summary>
    /// The expiry an access token minted now would actually carry: the client's configured lifetime,
    /// clamped by the subject's federated session cap and by any per-issuance ceiling.
    /// </summary>
    /// <remarks>
    /// Public because callers outside the mint need the same answer to report <c>expires_in</c>
    /// honestly. The clamp was applied to the JWT's <c>exp</c> but the response builders all reported
    /// the unclamped <c>client.AccessTokenLifetimeSeconds</c>, so a client whose federated session had
    /// four minutes left was told it held a thirty-minute token — and scheduled its refresh
    /// accordingly, i.e. after the token had already died.
    /// </remarks>
    public static DateTimeOffset EffectiveAccessTokenExpiry(
        OidcSubject? subject, OAuthClient client, DateTimeOffset? notAfter, DateTimeOffset now)
    {
        var expires = now.AddSeconds(client.AccessTokenLifetimeSeconds);
        if (subject?.SessionMaxExpiresAt is { } sessionCap && sessionCap < expires)
            expires = sessionCap;
        if (notAfter is { } issuanceCap && issuanceCap < expires)
            expires = issuanceCap;
        return expires;
    }

    public async Task<string> CreateIdTokenAsync(
        OidcSubject subject,
        OAuthClient client,
        IEnumerable<string> scopes,
        string? nonce = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var scopeList = scopes.ToList();

        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject.SubjectId,
            ["iat"] = now.ToUnixTimeSeconds()
        };

        if (!string.IsNullOrEmpty(nonce))
            claims["nonce"] = nonce;

        if (!string.IsNullOrEmpty(subject.SessionId))
            claims["sid"] = subject.SessionId;

        // OIDC Core §2: REQUIRED whenever max_age was requested, and the claim the RP uses to verify
        // its demand was met. Emitted whenever it is known rather than only under max_age, since
        // claims_supported has always advertised it.
        if (subject.AuthTime is { } authTime)
            claims["auth_time"] = authTime.ToUnixTimeSeconds();

        // OIDC Core §5.4 binds each standard claim set to a scope. Both the email and the profile
        // branch used to fire on `openid` as well — and `openid` is mandatory on every OIDC request,
        // so `scope=openid` alone returned email, email_verified, given_name, family_name, name,
        // phone_number and locale. There was no request a client could make that did NOT release
        // them, so the consent screen's scope list described something the token did not honour.
        //
        // AlwaysIncludeUserClaimsInIdToken is the documented opt-out, and honouring it here is what
        // makes it mean anything: it was persisted, seeded and migrated but read nowhere, so
        // operators had a knob that implied this was already gated.
        var always = client.AlwaysIncludeUserClaimsInIdToken;

        AddScopedIdentityClaims(claims, subject, scopeList, always);

        if ((always || scopeList.Contains(StandardScopes.Roles)) && subject.Roles is { Count: > 0 })
            claims["roles"] = subject.Roles.ToArray();

        if ((always || scopeList.Contains(StandardScopes.Groups)) && subject.Groups is { Count: > 0 })
            claims["groups"] = subject.Groups.ToArray();

        var allowedCustomClaims = await GetAllowedCustomClaimNamesAsync(scopeList, ct);
        if (subject.CustomAttributes is not null)
        {
            MergeCustomClaims(claims, subject.CustomAttributes, allowedCustomClaims, overwriteExisting: false);
        }
        // Gap-fill only, exactly as on the access token — see the note there. An upstream id_token
        // must not restate a claim this server derives from its own user store.
        if (subject.FederationClaims is not null)
        {
            MergeCustomClaims(claims, subject.FederationClaims, allowedCustomClaims, overwriteExisting: false);
        }

        // Ungated additional claims ride the id_token too (not just the access token): for an
        // embedded provider federating into a full host, the id_token is the transport the
        // downstream host reads claims from (federated:* capture) — an access-token-only claim
        // like a share-link token would otherwise vanish at the federation boundary.
        if (subject.AdditionalClaims is not null)
        {
            foreach (var (key, value) in subject.AdditionalClaims)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (ReservedClaimNames.Contains(key)) continue;
                claims[key] = value;
            }
        }

        var expires = now.AddSeconds(client.IdentityTokenLifetimeSeconds);
        if (subject.SessionMaxExpiresAt is { } sessionCap && sessionCap < expires)
            expires = sessionCap;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = client.ClientId,
            IssuedAt = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = keyManager.GetSigningCredentials(),
            Claims = claims
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    public Task<string> CreateRefreshTokenAsync(
        OidcSubject subject,
        OAuthClient client,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources = null,
        DateTimeOffset? originalCreatedAt = null,
        CancellationToken ct = default) =>
        CreateRefreshTokenAsync(subject, client, scopes, resources, originalCreatedAt, null, ct);

    /// <inheritdoc />
    public async Task<(string AccessToken, string RefreshToken)> CreateTrackedTokenPairAsync(
        OidcSubject subject,
        OAuthClient client,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources = null,
        CancellationToken ct = default)
    {
        var scopeList = scopes.ToList();
        var accessToken = await MintAccessTokenAsync(subject, client, scopeList, resources, ct: ct);
        var refreshToken = await CreateRefreshTokenAsync(
            subject, client, scopeList, resources, originalCreatedAt: null,
            [new IssuedAccessToken { Jti = accessToken.Jti, ExpiresAt = accessToken.ExpiresAt }], ct);
        return (accessToken.Token, refreshToken);
    }

    /// <summary>
    /// Issues a refresh grant, recording the access tokens that must die with it.
    /// </summary>
    /// <param name="inheritedAccessTokens">The still-live access tokens this family has minted —
    /// the predecessor's surviving set on a rotation, plus the token being issued alongside this
    /// one. Pruned and capped here rather than at the call sites so no caller can forget.</param>
    private async Task<string> CreateRefreshTokenAsync(
        OidcSubject subject,
        OAuthClient client,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources,
        DateTimeOffset? originalCreatedAt,
        IEnumerable<IssuedAccessToken>? inheritedAccessTokens,
        CancellationToken ct)
    {
        var handle = GenerateRefreshTokenHandle();
        var now = DateTimeOffset.UtcNow;
        var scopeList = scopes.ToList();

        // Absolute mode: cap measured from original issuance; rotation preserves cap.
        // Sliding: window extends by Sliding on each rotation, capped at absolute.
        var origin = originalCreatedAt ?? now;
        var absoluteCap = origin.AddSeconds(client.AbsoluteRefreshTokenLifetimeSeconds);
        DateTimeOffset expiresAt = client.RefreshTokenExpiration switch
        {
            RefreshTokenExpiration.Sliding =>
                new[] { now.AddSeconds(client.SlidingRefreshTokenLifetimeSeconds), absoluteCap }.Min(),
            _ => absoluteCap,
        };

        // Upstream session cap clamps refresh expiry so tokens can't outlive the federated session.
        if (subject.SessionMaxExpiresAt is { } sessionCap && sessionCap < expiresAt)
            expiresAt = sessionCap;

        var grant = new PersistedGrant
        {
            Key = handle,
            Type = "refresh_token",
            SubjectId = subject.SubjectId,
            ClientId = client.ClientId,
            // Which sign-in session this refresh family belongs to, so ending ONE session can revoke it.
            // Without it, the account page's "Log out other devices" could end the OP cookie and nothing
            // else — grant removal was only expressible as subject-wide, and revoking every session would
            // have logged the user out of the device they chose to keep. Null when the grant has no
            // interactive session behind it, and never matched by a session-scoped removal in that case.
            SessionId = subject.SessionId,
            Data = JsonSerializer.Serialize(new RefreshTokenData
            {
                Scopes = scopeList,
                Resources = resources?.ToList(),
                SubjectId = subject.SubjectId,
                ClientId = client.ClientId,
                CreatedAt = now,
                OriginalCreatedAt = origin,
                SessionMaxExpiresAt = subject.SessionMaxExpiresAt,
                Subject = subject,
                AccessTokens = PruneAccessTokens(inheritedAccessTokens, now),
            }, ProtocolJsonContext.Default.RefreshTokenData),
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

        await grantStore.StoreAsync(grant, ct);

        return handle;
    }

    public async Task<TokenResponse> HandleAuthorizationCodeAsync(
        string code,
        string clientId,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct = default)
    {
        var grant = await grantStore.GetAsync(code, ct);
        if (grant is null || grant.Type != "authorization_code")
            throw new InvalidOperationException("Invalid authorization code");

        // Single-use, with the row KEPT and marked consumed rather than deleted.
        //
        // TryConsumeAsync deleted it, which made a sequential replay indistinguishable from a bogus
        // code: the second request found nothing and answered "invalid", so the server never learned
        // that a code it had issued was being presented twice. That is the one signal RFC 6749 §4.1.2
        // asks it to act on — "deny the request and SHOULD revoke ... all tokens previously issued
        // based on that authorization code" — and it was being thrown away. Refresh tokens have kept
        // a consumed marker for exactly this reason; codes now do too, and the row expires on its own
        // schedule as before.
        async Task RevokeForReplayAsync()
        {
            if (grant.SubjectId is null) return;

            logger.LogError(
                "Authorization code replay detected. Revoking tokens for the pair. Client: {ClientId}, Subject: {SubjectId}",
                grant.ClientId, grant.SubjectId);

            await GrantRevocation.RevokeClientGrantsAsync(
                grantStore, revokedTokenStore, grant.SubjectId, grant.ClientId,
                PersistedGrantTypes.SessionBound, logger, ct);
        }

        if (grant.ConsumedAt is not null)
        {
            await RevokeForReplayAsync();
            throw new InvalidOperationException("Authorization code has already been used");
        }

        grant.Key = code;
        grant.ConsumedAt = DateTimeOffset.UtcNow;

        // Conditional, so two concurrent redemptions cannot both win — the loser is a replay too.
        if (!await grantStore.TryMarkConsumedAsync(grant, ct))
        {
            await RevokeForReplayAsync();
            throw new InvalidOperationException("Authorization code has already been used");
        }

        if (grant.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Authorization code has expired");

        var authCode = JsonSerializer.Deserialize(grant.Data, ProtocolJsonContext.Default.ProtocolAuthorizationCode)
            ?? throw new InvalidOperationException("Failed to deserialize authorization code");

        if (!string.Equals(authCode.ClientId, clientId, StringComparison.Ordinal))
            throw new InvalidOperationException("Client ID mismatch");

        if (!string.Equals(authCode.RedirectUri, redirectUri, StringComparison.Ordinal))
            throw new InvalidOperationException("Redirect URI mismatch");

        var client = await clientStore.GetAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client '{clientId}' not found");

        if (client.RequirePkce && string.IsNullOrEmpty(authCode.CodeChallenge))
            throw new InvalidOperationException("PKCE is required for this client but no code_challenge was present");

        if (!string.IsNullOrEmpty(authCode.CodeChallenge))
        {
            // No `?? "plain"` fallback: RFC 7636 makes a missing method mean plain, and plain is not
            // accepted, so a challenge stored without one must fail rather than quietly downgrade.
            if (!PkceValidator.ValidateCodeVerifier(codeVerifier, authCode.CodeChallenge, authCode.CodeChallengeMethod))
                throw new InvalidOperationException("PKCE validation failed");
        }

        var subject = authCode.Subject;

        var accessToken = await MintAccessTokenAsync(subject, client, authCode.Scopes, authCode.Resources, ct: ct);

        string? idToken = null;
        if (authCode.Scopes.Contains(StandardScopes.OpenId))
        {
            idToken = await CreateIdTokenAsync(subject, client, authCode.Scopes, authCode.Nonce, ct);
        }

        string? refreshToken = null;
        if (authCode.Scopes.Contains(StandardScopes.OfflineAccess) && client.AllowOfflineAccess)
        {
            // The access token just minted is recorded on the refresh grant, so revoking the refresh
            // token revokes it too rather than leaving it live for its full lifetime.
            refreshToken = await CreateRefreshTokenAsync(
                subject, client, authCode.Scopes, authCode.Resources, null,
                [new IssuedAccessToken { Jti = accessToken.Jti, ExpiresAt = accessToken.ExpiresAt }], ct);
        }

        logger.LogInformation(
            "Authorization code exchanged for tokens. Client: {ClientId}, Subject: {SubjectId}",
            clientId, subject.SubjectId);

        return new TokenResponse
        {
            AccessToken = accessToken.Token,
            // RFC 6749 §5.1: the lifetime of this token. Reporting the configured ceiling instead
            // overstated it whenever a federated session cap clamped the JWT's own exp.
            ExpiresIn = accessToken.ExpiresInSeconds,
            IdToken = idToken,
            RefreshToken = refreshToken,
            Scope = string.Join(' ', authCode.Scopes)
        };
    }

    public async Task<TokenResponse> HandleRefreshTokenAsync(
        string refreshToken,
        string clientId,
        IEnumerable<string>? resources = null,
        CancellationToken ct = default)
    {
        var grant = await grantStore.GetAsync(refreshToken, ct);
        if (grant is null || grant.Type != "refresh_token")
            throw new InvalidOperationException("Invalid refresh token");

        if (!string.Equals(grant.ClientId, clientId, StringComparison.Ordinal))
            throw new InvalidOperationException("Client ID mismatch for refresh token");

        var now = DateTimeOffset.UtcNow;

        if (grant.ExpiresAt <= now)
            throw new InvalidOperationException("Refresh token has expired");

        var data = JsonSerializer.Deserialize(grant.Data, ProtocolJsonContext.Default.RefreshTokenData)
            ?? throw new InvalidOperationException("Failed to deserialize refresh token data");

        // Replay handling — reuse inside grace window replays the successor idempotently,
        // reuse outside it (or of a missing successor) revokes the whole family.
        if (grant.ConsumedAt.HasValue)
        {
            if (RefreshTokenReuseGraceWindow > TimeSpan.Zero &&
                !string.IsNullOrEmpty(data.SuccessorKey) &&
                now - grant.ConsumedAt.Value <= RefreshTokenReuseGraceWindow)
            {
                var successor = await grantStore.GetAsync(data.SuccessorKey, ct);
                if (successor is not null &&
                    successor.Type == "refresh_token" &&
                    !successor.ConsumedAt.HasValue &&
                    successor.ExpiresAt > now &&
                    // Null means the successor was consumed or revoked underneath us, which puts this
                    // presentation outside the grace window after all — fall through to replay handling.
                    await ReissueFromSuccessorAsync(successor, data.SuccessorKey, resources, ct)
                        is { } reissued)
                {
                    return reissued;
                }
            }

            logger.LogError(
                "Refresh token replay detected! Revoking all tokens for subject. Client: {ClientId}, Subject: {SubjectId}",
                clientId, grant.SubjectId);

            if (grant.SubjectId is not null)
            {
                // Theft detection, so this must reach the thief's ACCESS token too — that is the one
                // credential nobody but the thief has ever seen, and killing only the refresh family
                // left it working for up to AccessTokenLifetimeSeconds.
                //
                // Scoped to the session-bound types rather than every grant for the pair: the previous
                // RemoveAllBySubjectAndClientAsync also deleted the user's standing agent consent and
                // any pending approvals, which are long-lived records of the user's decisions, not
                // token authority, and are not what a stolen refresh token compromises.
                await GrantRevocation.RevokeClientGrantsAsync(
                    grantStore, revokedTokenStore, grant.SubjectId, clientId,
                    PersistedGrantTypes.SessionBound, logger, ct);
            }

            throw new InvalidOperationException("Refresh token has been revoked (replay detected)");
        }

        var client = await clientStore.GetAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client '{clientId}' not found");

        // Re-engage the host's subject resolver so it can re-check session validity
        // (deactivation, revoked share links, role changes, etc.).
        var context = new OidcSubjectResolutionContext(clientId, data.Scopes, data.Resources ?? []);
        var resolved = await subjectResolver.ResolveRefreshAsync(data.Subject, context, ct);

        OidcSubject freshSubject = resolved switch
        {
            OidcSubjectResult.Allowed a => a.Subject,
            OidcSubjectResult.Rejected r => throw new InvalidOperationException(
                $"Subject resolver rejected refresh: {r.Reason}{(r.Description is null ? "" : $" ({r.Description})")}"),
            _ => throw new InvalidOperationException("Unknown subject resolver result"),
        };

        // Re-apply per-user scope entitlement against the FRESHLY resolved roles. This is where
        // revoking a role actually takes effect: the grant still records the scopes approved at
        // authorize, so without this a refresh chain would keep re-minting a gated scope for as long
        // as the refresh token lived. Dropping to nothing ends the chain rather than issuing an empty
        // token the client cannot use.
        var entitledScopes = await _scopeRoleGate.FilterAsync(data.Scopes, freshSubject.Roles, ct);
        if (entitledScopes.Count < data.Scopes.Count)
        {
            if (entitledScopes.Count == 0)
                throw new InvalidOperationException(
                    "The subject is no longer entitled to any of this grant's scopes");

            logger.LogInformation("Dropping role-gated scopes on refresh for {SubjectId} on client {ClientId}: {Dropped}",
                grant.SubjectId, clientId, string.Join(',', data.Scopes.Except(entitledScopes, StringComparer.Ordinal)));

            data.Scopes = [.. entitledScopes];
        }

        // And re-apply the CLIENT's own AllowedScopes, which this path never re-checked.
        //
        // The per-user role gate above is the same idea one level down — "this is where revoking a role
        // actually takes effect" — and the client-level equivalent was missing, so removing a scope from a
        // client was not a way to stop that client using it. Responding to an incident by PUTting
        // /api/v1/clients/{id} without `billing.write` refused every NEW authorization request naming it while
        // every existing refresh chain kept re-minting it, for as long as the absolute refresh lifetime allowed
        // — 30 days on the defaults. The operator is told the permission is gone and the tokens say otherwise.
        //
        // Dropped rather than refused, exactly as the role gate does, so a client that lost one scope keeps
        // working with the rest; dropping to nothing ends the chain.
        var clientAllowedScopes = new HashSet<string>(client.AllowedScopes, StringComparer.Ordinal);
        if (data.Scopes.Any(sc => !clientAllowedScopes.Contains(sc)))
        {
            var stillAllowed = data.Scopes.Where(clientAllowedScopes.Contains).ToList();

            if (stillAllowed.Count == 0)
                throw new InvalidOperationException(
                    "This client is no longer allowed any of this grant's scopes");

            logger.LogInformation(
                "Dropping scopes no longer allowed for client {ClientId} on refresh for {SubjectId}: {Dropped}",
                clientId, grant.SubjectId,
                string.Join(',', data.Scopes.Except(stillAllowed, StringComparer.Ordinal)));

            data.Scopes = stillAllowed;
        }

        // RFC 8707: refresh-time resources must be a subset of the original grant's resources
        // (or client.Audiences if none recorded).
        var requestedResources = resources?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        List<string>? tokenResources;
        if (requestedResources is { Count: > 0 })
        {
            var allowed = data.Resources is { Count: > 0 } ? data.Resources : client.Audiences;
            foreach (var r in requestedResources)
            {
                if (!allowed.Contains(r, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Resource '{r}' is not permitted for this refresh token");
            }
            tokenResources = requestedResources;
        }
        else
        {
            tokenResources = data.Resources;
        }

        // Minted before the successor so the successor grant can record its jti: the successor is the
        // live refresh token from here on, so it is the one whose revocation must kill this access
        // token. Losing the consume race below discards both, and the orphan successor holding the
        // jti expires with it — the token is never returned to any caller.
        var accessToken = await MintAccessTokenAsync(freshSubject, client, data.Scopes, tokenResources, ct: ct);

        // ReUse clients keep their refresh token; only OneTime clients rotate.
        //
        // RefreshTokenUsage was persisted, seeded, migrated and documented — and read nowhere, so
        // every client got OneTime regardless. For a client configured ReUse that is worse than
        // ignoring a preference: its second, entirely normal refresh presents the same token again,
        // which strict rotation reads as REPLAY and answers by revoking the user's whole grant
        // family. An operator's explicit configuration therefore produced a sign-out.
        if (client.RefreshTokenUsage == RefreshTokenUsage.ReUse)
        {
            logger.LogInformation(
                "Refresh token reused (client is configured ReUse). Client: {ClientId}, Subject: {SubjectId}",
                clientId, data.SubjectId);

            // Record the jti on the grant that governs it, exactly as every other issuance path does.
            //
            // An access token is a self-contained ES256 JWT with no reference mode, so the ONLY way to kill
            // one before its exp is an IRevokedTokenStore entry keyed by jti — and RefreshTokenData.AccessTokens
            // is where RevokeRefreshTokenAsync and GrantRevocation.RevokeClientGrantsAsync look to find them.
            // The authorization-code path passes the fresh jti into CreateRefreshTokenAsync, the rotation path
            // carries the predecessor's list forward plus the new one, the device path does it, and the
            // grace-window path appends it with an explicit write whose comment says exactly why. This branch
            // did not. So for a client configured ReUse — whose refresh token never rotates, and which
            // therefore accumulates every access token it will ever mint under one grant — revoking the
            // refresh token killed nothing: every access token issued against it stayed valid to its own exp.
            data.AccessTokens = PruneAccessTokens(
                [.. data.AccessTokens ?? [], new IssuedAccessToken { Jti = accessToken.Jti, ExpiresAt = accessToken.ExpiresAt }],
                now);
            // Grants read back from storage carry no Key — only its hash is persisted — so it has to be
            // re-set before writing. See the rotation note below.
            grant.Key = refreshToken;
            grant.Data = JsonSerializer.Serialize(data, ProtocolJsonContext.Default.RefreshTokenData);
            await grantStore.StoreAsync(grant, ct);

            string? reuseIdToken = null;
            if (data.Scopes.Contains(StandardScopes.OpenId))
                reuseIdToken = await CreateIdTokenAsync(freshSubject, client, data.Scopes, ct: ct);

            return new TokenResponse
            {
                AccessToken = accessToken.Token,
                ExpiresIn = accessToken.ExpiresInSeconds,
                IdToken = reuseIdToken,
                RefreshToken = refreshToken,
                Scope = string.Join(' ', data.Scopes),
            };
        }

        // Rotation: issue successor, mark old consumed with successor key recorded.
        var originalCreatedAt = data.OriginalCreatedAt ?? data.CreatedAt;
        var newRefreshToken = await CreateRefreshTokenAsync(
            freshSubject, client, data.Scopes, data.Resources, originalCreatedAt,
            // The predecessor's surviving access tokens carry forward, so a revocation after several
            // rotations still reaches every token of this family that is genuinely still live.
            [.. data.AccessTokens ?? [], new IssuedAccessToken { Jti = accessToken.Jti, ExpiresAt = accessToken.ExpiresAt }],
            ct);

        data.SuccessorKey = newRefreshToken;
        // GetAsync returns Key empty (the raw handle is never persisted — only its hash is the
        // partition key), so it MUST be re-set before the store re-hashes it. An empty key writes
        // the consumed-marker to the SHA-256("") partition instead of the real row, which silently
        // disables rotation: the old token replays forever and replay revocation never fires.
        grant.Key = refreshToken;
        grant.ConsumedAt = now;
        grant.Data = JsonSerializer.Serialize(data, ProtocolJsonContext.Default.RefreshTokenData);

        // Atomic rotation (F32): only ONE concurrent redemption of this refresh token may consume it.
        // Two requests whose reads both saw ConsumedAt==null (a window spanning the resolve + client
        // lookup + successor mint) would otherwise both write a consumed marker and both mint valid
        // successors, and neither would enter replay revocation — defeating strict rotation. The
        // ETag-conditional mark lets exactly one win; the loser abandons its just-minted (orphan,
        // soon-expiring) successor and re-evaluates through the replay/grace path below, which either
        // serves the winner's successor (concurrent double-submit within the grace window) or revokes
        // the family (a genuine stolen-token replay).
        var consumed = await grantStore.TryMarkConsumedAsync(grant, ct);
        if (!consumed)
        {
            logger.LogWarning(
                "Refresh-token rotation lost the consume race; re-evaluating as replay/grace. Client: {ClientId}, Subject: {SubjectId}",
                clientId, grant.SubjectId);
            return await HandleRefreshTokenAsync(refreshToken, clientId, resources, ct);
        }

        string? idToken = null;
        if (data.Scopes.Contains(StandardScopes.OpenId))
        {
            idToken = await CreateIdTokenAsync(freshSubject, client, data.Scopes, ct: ct);
        }

        logger.LogInformation(
            "Refresh token rotated. Client: {ClientId}, Subject: {SubjectId}",
            clientId, data.SubjectId);

        return new TokenResponse
        {
            AccessToken = accessToken.Token,
            // RFC 6749 §5.1: the lifetime of this token. Reporting the configured ceiling instead
            // overstated it whenever a federated session cap clamped the JWT's own exp.
            ExpiresIn = accessToken.ExpiresInSeconds,
            IdToken = idToken,
            RefreshToken = newRefreshToken,
            Scope = string.Join(' ', data.Scopes)
        };
    }

    /// <summary>
    /// Serves a retry from the successor of an already-consumed refresh token, or null when it cannot.
    /// </summary>
    /// <remarks>
    /// Null means the successor was consumed or revoked while this retry was being served, so the caller must
    /// fall through to replay handling: at that point this presentation of the OLD token really is out of the
    /// safe window — either the legitimate client has moved on past the successor too, or the family is
    /// already being revoked — and both are what the replay branch is for.
    /// </remarks>
    private async Task<TokenResponse?> ReissueFromSuccessorAsync(
        PersistedGrant successor,
        string successorKey, // successor.Key is empty on grants read back from storage
        IEnumerable<string>? resources,
        CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize(successor.Data, ProtocolJsonContext.Default.RefreshTokenData)
            ?? throw new InvalidOperationException("Failed to deserialize successor refresh token data");

        var client = await clientStore.GetAsync(successor.ClientId, ct)
            ?? throw new InvalidOperationException($"Client '{successor.ClientId}' not found");

        var requestedResources = resources?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        List<string>? tokenResources;
        if (requestedResources is { Count: > 0 })
        {
            var allowed = data.Resources is { Count: > 0 } ? data.Resources : client.Audiences;
            foreach (var r in requestedResources)
            {
                if (!allowed.Contains(r, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Resource '{r}' is not permitted for this refresh token");
            }
            tokenResources = requestedResources;
        }
        else
        {
            tokenResources = data.Resources;
        }

        var accessToken = await MintAccessTokenAsync(data.Subject, client, data.Scopes, tokenResources, ct: ct);

        // The grace path mints against a successor that already exists, so the jti has to be appended
        // to it rather than passed in at creation. Without this write, an access token handed out on a
        // retry would survive revocation of the very refresh token it was issued against — and the
        // grace window is exactly where a stolen token racing the legitimate client gets served.
        data.AccessTokens = PruneAccessTokens(
            [.. data.AccessTokens ?? [], new IssuedAccessToken { Jti = accessToken.Jti, ExpiresAt = accessToken.ExpiresAt }],
            DateTimeOffset.UtcNow);
        successor.Key = successorKey; // grants read back from storage carry no key; see the rotation note
        successor.Data = JsonSerializer.Serialize(data, ProtocolJsonContext.Default.RefreshTokenData);

        // Conditional on the successor still being live, NOT an unconditional upsert.
        //
        // This was grantStore.StoreAsync — a full-row upsert on every provider. The instance written carries
        // ConsumedAt = null, and on DynamoDB and SQL the write also DROPS the top-level consumedAt guard
        // attribute that TryMarkConsumedAsync conditions on. So any consume or delete landing between the read
        // at the top of this method and this write was silently undone: a revoked refresh grant came back, and
        // rotation-replay detection stopped seeing the marker it depends on. The same
        // read-modify-blind-write shape already fixed for the device-poll timestamp, for
        // RecordSuccessfulLoginAsync, and for the profile-revert compensation.
        //
        // Losing the race means the successor was consumed or revoked while this retry was being served, so
        // the retry must not be served: the access token just minted would be untracked by any live grant and
        // therefore unrevokable. Refusing is also the right answer for the case this window exists to
        // tolerate — a stolen token racing the legitimate client.
        if (!await grantStore.TryUpdateDataIfUnconsumedAsync(successor, ct))
        {
            logger.LogWarning(
                "Grace-window retry for client {ClientId} lost the race to record its access token: the "
                + "successor grant was consumed or revoked concurrently. Refusing the retry.",
                successor.ClientId);
            return null;
        }

        string? idToken = null;
        if (data.Scopes.Contains(StandardScopes.OpenId))
        {
            idToken = await CreateIdTokenAsync(data.Subject, client, data.Scopes, ct: ct);
        }

        logger.LogInformation(
            "Refresh token retry served from grace window. Client: {ClientId}, Subject: {SubjectId}",
            successor.ClientId, data.SubjectId);

        return new TokenResponse
        {
            AccessToken = accessToken.Token,
            // RFC 6749 §5.1: the lifetime of this token. Reporting the configured ceiling instead
            // overstated it whenever a federated session cap clamped the JWT's own exp.
            ExpiresIn = accessToken.ExpiresInSeconds,
            IdToken = idToken,
            RefreshToken = successorKey,
            Scope = string.Join(' ', data.Scopes)
        };
    }

    public async Task<TokenResponse> HandleClientCredentialsAsync(
        string clientId,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources = null,
        CancellationToken ct = default)
    {
        var client = await clientStore.GetAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client '{clientId}' not found");

        if (!client.AllowedGrantTypes.Contains(GrantTypes.ClientCredentials))
            throw new InvalidOperationException($"Client '{clientId}' does not support client_credentials grant type");

        var scopeList = scopes.ToList();

        foreach (var scope in scopeList)
        {
            if (!client.AllowedScopes.Contains(scope))
                // Typed, so the OAuth error code does not depend on this sentence starting with "Scope '".
                // It did: TokenGrantHandlers maps the code by matching the message prefix, and only the
                // token-EXCHANGE handler carried that match — so an undeclared scope on client_credentials
                // answered invalid_grant where RFC 6749 §5.2 requires invalid_scope, telling a client its
                // grant was bad when the truth was that it had asked for a scope it does not hold. The
                // exchange path had the mapping and its sibling did not, which is how it went unnoticed.
                throw new ProtocolTokenException("invalid_scope",
                    $"Scope '{scope}' is not allowed for client '{clientId}'");
        }

        var resourceList = resources?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (resourceList is { Count: > 0 })
        {
            // Same rule as the authorize path and the exchange path, from the one function all three read
            // — see ResourceAudiencePolicy for why an empty list is permissive only on a client that
            // predates AudiencesDeclared.
            foreach (var r in resourceList)
            {
                // Typed, not a bare InvalidOperationException. TokenGrantHandlers used to derive the error
                // CODE by string-matching the message prefix ("Resource '"), and ResourceAudiencePolicy's
                // message begins with a lowercase "resource '" — so moving this site onto the shared policy
                // had already silently downgraded RFC 8707's invalid_target to invalid_grant here, with no
                // test to notice. Routing a protocol error code through prose is the defect; this removes it.
                if (ResourceAudiencePolicy.RejectResource(client, r) is { } rejected)
                    throw new ProtocolTokenException("invalid_target", rejected);
            }
        }

        // Agent service mode: the ceiling applies alone (no user, so no floor), ask degrades
        // to deny (an approval has no one to ask), and the profile's lifetime cap clamps.
        var profile = agentProfileStore is null ? null : await agentProfileStore.GetAsync(clientId, ct);
        string? authorityJson = null;
        DateTimeOffset? notAfter = null;
        var expiresIn = client.AccessTokenLifetimeSeconds;
        if (profile is not null)
        {
            if (profile.Mode == AgentMode.Delegated)
                throw new ProtocolTokenException("unauthorized_client",
                    $"Client '{clientId}' is registered as a delegated-only agent; client_credentials is not permitted");

            var authority = await ApplyHighRiskDefaultsAsync(profile.Ceiling, profile.HighRiskDefault, ct);
            var unattended = MapAskPolicies(authority, ActionPolicy.Deny);

            // Ask degrades to deny here because an unattended grant has no one to ask — so a ceiling whose
            // actions are ALL ask (or whose high-risk default makes them so) permits nothing in this mode, and
            // every grant is dropped on serialization. That minted `authorization_details: []`, which the
            // resource side reads as UNRESTRICTED: the most tightly configured service agent got the
            // broadest token, from valid credentials and no attacker input at all. This path had no emptiness
            // guard of any kind.
            if (AuthorityJson.SerializesToNothing(unattended))
                throw new ProtocolTokenException("unauthorized_client",
                    $"Agent '{clientId}' has no authority available unattended: every action in its ceiling "
                    + "requires approval, and client_credentials has no user to ask");

            authorityJson = AuthorityJson.Serialize(unattended);
            notAfter = DateTimeOffset.UtcNow.AddSeconds(profile.MaxTokenLifetimeSeconds);
            expiresIn = Math.Min(expiresIn, profile.MaxTokenLifetimeSeconds);

            await Hooks.RunOnTokenIssuingAsync(
                new TokenIssuanceContext(clientId, null, GrantTypes.ClientCredentials, scopeList, authorityJson)
                {
                    EffectiveAuthorityJson = authorityJson,
                }, ct);
        }

        var accessToken = await CreateAccessTokenAsync(
            null, client, scopeList, resourceList, authorityJson, null, notAfter, ct);

        logger.LogInformation("Client credentials token issued for client {ClientId}", clientId);

        return new TokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = expiresIn,
            Scope = string.Join(' ', scopeList),
            AuthorizationDetails = ToElement(authorityJson),
        };
    }

    public async Task<TokenResponse> HandleTokenExchangeAsync(
        string clientId,
        string subjectToken,
        string subjectTokenType,
        string? requestedTokenType = null,
        IEnumerable<string>? scopes = null,
        IEnumerable<string>? resources = null,
        IEnumerable<string>? audiences = null,
        IReadOnlyDictionary<string, string>? extraParameters = null,
        string? actorToken = null,
        string? actorTokenType = null,
        string? authorizationDetailsJson = null,
        string? approvalId = null,
        CancellationToken ct = default)
    {
        if (subjectTokenType is not (TokenTypeIdentifiers.AccessToken or TokenTypeIdentifiers.Jwt))
            throw new InvalidOperationException(
                $"subject_token_type '{subjectTokenType}' is not supported (only this server's own access tokens can be exchanged)");

        if (requestedTokenType is not null and not (TokenTypeIdentifiers.AccessToken or TokenTypeIdentifiers.Jwt))
            throw new InvalidOperationException(
                $"requested_token_type '{requestedTokenType}' is not supported");

        var client = await clientStore.GetAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client '{clientId}' not found");

        if (!client.AllowedGrantTypes.Contains(GrantTypes.TokenExchange, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Client '{clientId}' does not support token-exchange grant type");

        // Validate the subject token exactly like userinfo does: our issuer, our signing keys,
        // live lifetime — any audience, because the AS accepts its own tokens regardless of the
        // client they were minted for. The EXCHANGING client's authorization is what the grant
        // check above and the scope narrowing below enforce.
        var keys = keyManager.GetSecurityKeys().Select(ProtocolSigningKeyOps.JwkToSecurityKey).ToList();
        var validation = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidateIssuer = true,
            ValidateAudience = true,
            AudienceValidator = (auds, _, _) => auds?.Any() == true,
            ValidateLifetime = true,
            IssuerSigningKeys = keys,
            ValidateIssuerSigningKey = true,
            // We signed this token ourselves, so accept only what we sign with.
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512],
            // Pin the token KIND, not just its signature. All four JWT mint sites in this server share one
            // issuer and one key, so "we signed it" says nothing about what it is. Without this an id_token
            // or a back-channel logout token passed every check here and was exchanged for a live access
            // token carrying the victim's sub and roles — and neither carries a jti, so the revocation
            // check below silently degraded to a no-op and RevocationEndpoint (which requires client_id and
            // jti) could not revoke them at all, leaving no operator remedy short of key rotation.
            ValidTypes = [TokenTypes.AccessTokenJwt],
            ClockSkew = TimeSpan.FromSeconds(60),
        };

        var handler = new JsonWebTokenHandler();
        var validated = await handler.ValidateTokenAsync(subjectToken, validation);
        if (!validated.IsValid)
            throw new InvalidOperationException("subject_token is not a valid access token issued by this server");

        var tokenClaims = validated.Claims;

        // Belt and braces for tokens minted BEFORE the typ header was stamped, which are still inside their
        // lifetime and would otherwise carry no typ and be rejected — or, on a handler that treats a missing
        // typ as acceptable, be let through. Only an access token has both of these: an id_token has
        // neither, and a logout token has neither. Requiring jti additionally guarantees the revocation
        // check above is meaningful rather than skipped.
        if (!tokenClaims.TryGetValue("client_id", out var clientIdProbe)
            || clientIdProbe is not string { Length: > 0 })
            throw new InvalidOperationException("subject_token carries no client_id and is not an access token");

        if (!tokenClaims.TryGetValue("jti", out var jtiProbe)
            || jtiProbe is not string { Length: > 0 })
            throw new InvalidOperationException("subject_token carries no jti and is not an access token");

        // An id_token carries nonce; a logout token carries events. Neither is ever an access token.
        if (tokenClaims.ContainsKey("events") || tokenClaims.ContainsKey("nonce"))
            throw new InvalidOperationException("subject_token is not an access token");

        // Revocation has to be honoured HERE, not only at the resource server. A revoked access token
        // keeps a valid signature and a live exp — that is the whole point of revoking before expiry —
        // so without this check the token can be handed to /connect/token and exchanged for a fresh,
        // unrevoked one. Revoking in response to a compromise would end the token's use at APIs while
        // leaving it able to mint successors.
        if (revokedTokenStore is not null
            && tokenClaims.TryGetValue("jti", out var subjectJti)
            && subjectJti is string subjectJtiValue
            && !string.IsNullOrEmpty(subjectJtiValue)
            && await revokedTokenStore.IsRevokedAsync(subjectJtiValue, ct))
        {
            throw new InvalidOperationException("subject_token has been revoked");
        }

        // Delegation of a user identity only: a client-credentials token has no sub to act for.
        if (!tokenClaims.TryGetValue("sub", out var subValue) || subValue is not string sub || string.IsNullOrEmpty(sub))
            throw new InvalidOperationException("subject_token carries no subject (sub) and cannot be exchanged");

        // An agent profile is what turns this exchange into a composite delegation; without
        // one the method behaves exactly as it always has (plus optional RAR narrowing below).
        var agentProfile = agentProfileStore is null ? null : await agentProfileStore.GetAsync(clientId, ct);

        // The profile makes this client's identity an ASSERTION — it becomes an entry in the `act` chain
        // and the subject of every approval recorded against the agent. Token exchange deliberately does
        // not require a confidential client in general (RFC 8693 imposes no such rule, and the BFF's
        // context-bound exchange is a public client doing exactly this), so the admin endpoint refuses to
        // attach a profile to a public client. That check does not survive the client: flip
        // RequireClientSecret off afterwards, delete the last secret, or seed the client public, and the
        // profile stays attached to a client_id anyone who knows it can present. Re-checking at mint binds
        // the invariant to the moment the assertion is made, so no write path can leave it broken.
        if (agentProfile is not null && !client.IsConfidential)
            throw new ProtocolTokenException("invalid_client",
                $"Client '{clientId}' carries an agent profile but is not a confidential client; the " +
                "agent identity asserted in the act chain must be authenticated");

        if (actorToken is not null)
        {
            if (agentProfile is null)
                throw new ProtocolTokenException("invalid_request", "actor_token is not supported for this client");
            if (actorTokenType is not (TokenTypeIdentifiers.AccessToken or TokenTypeIdentifiers.Jwt))
                throw new ProtocolTokenException("invalid_request",
                    $"actor_token_type '{actorTokenType}' is not supported");

            // D2: the actor IS the authenticated client. An actor token may corroborate that
            // identity (RFC 8693 conformance) but can never substitute a different one.
            var actorValidated = await handler.ValidateTokenAsync(actorToken, validation);
            if (!actorValidated.IsValid)
                throw new ProtocolTokenException("invalid_grant",
                    "actor_token is not a valid access token issued by this server");
            if (!actorValidated.Claims.TryGetValue("client_id", out var actorClientValue) ||
                actorClientValue is not string actorClientId ||
                !string.Equals(actorClientId, clientId, StringComparison.Ordinal))
                throw new ProtocolTokenException("invalid_grant",
                    "actor_token was issued to a different client than the one authenticating");
        }

        var subjectScopes = tokenClaims.TryGetValue("scope", out var scopeValue) && scopeValue is string scopeStr
            ? scopeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : [];

        // Downscoping only, never escalation: explicit requests must sit inside BOTH the subject
        // token's scopes and the exchanging client's allowed scopes. No request → the intersection,
        // minus offline_access (an exchange never issues a refresh token).
        var requestedScopes = scopes?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        List<string> grantedScopes;
        if (requestedScopes is { Count: > 0 })
        {
            foreach (var s in requestedScopes)
            {
                if (!subjectScopes.Contains(s))
                    throw new ProtocolTokenException("invalid_scope",
                        $"Scope '{s}' exceeds the subject token's scopes");
                if (!client.AllowedScopes.Contains(s))
                    throw new ProtocolTokenException("invalid_scope",
                        $"Scope '{s}' is not allowed for client '{clientId}'");
            }
            grantedScopes = requestedScopes;
        }
        else
        {
            grantedScopes = subjectScopes
                .Where(s => client.AllowedScopes.Contains(s) && s != StandardScopes.OfflineAccess)
                .ToList();
        }

        // RFC 8707 resource + RFC 8693 audience both narrow aud; both must be pre-registered on
        // the exchanging client. resource values must additionally be absolute URIs.
        // On the EXCHANGE path an empty Audiences list denies rather than meaning "unset".
        //
        // The "empty means unset, not deny-all" convention is deliberate at authorize and
        // client_credentials, where it exists so a dynamically-registered client — which RFC 7591
        // gives no way to declare audiences — can still name a resource. But the consequence differs
        // in kind here: a client with no registered audiences could aim the exchanged token at ANY
        // audience it named, and the subject token's own `aud` is never consulted (the subject-token
        // validator accepts any non-empty audience), so the value landed verbatim in the minted
        // token's `aud`. A client permitted to exchange tokens must declare the targets it may aim
        // them at.
        // Through ResourceAudiencePolicy, like the authorize and client_credentials paths.
        //
        // This kept its own copy of the rule, and the copy was the defective version: `Uri.TryCreate(r,
        // UriKind.Absolute, …)` SUCCEEDS on a bare path on Unix, because the runtime infers a `file:`
        // scheme — so `resource=/admin` passed the shape check, and a client whose stored Audiences held a
        // bare path (a row written before the policy existed, or one a config seeder wrote without going
        // through it) then passed the membership check too and received a tenant-signed token whose `aud`
        // was `/admin`. The policy's IsAbsoluteUriWithWrittenScheme is what refuses that.
        //
        // The RFC 8693 `audience` values got no shape check at all here, only membership. Same rule now
        // applies to both lists: naming a target is a client's declaration either way.
        var targetAudiences = new List<string>();
        foreach (var value in (resources ?? []).Concat(audiences ?? [])
                     .Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (Authagonal.Core.Services.ResourceAudiencePolicy.RejectResource(client, value) is { } why)
                throw new ProtocolTokenException("invalid_target", why);
            targetAudiences.Add(value);
        }

        // Rebuild the subject from the validated token rather than the user store: the exchange
        // is a projection of an existing session, not a fresh sign-in. roles/groups map back to
        // their first-class slots; every other non-protocol claim goes through CustomAttributes so
        // the NEW scope set's UserClaims gating decides what the downscoped token re-releases.
        var customAttributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in tokenClaims)
        {
            if (ReservedClaimNames.Contains(key)) continue;
            var stringValue = value switch
            {
                string s => s,
                bool b => b ? "true" : "false",
                int or long or double or decimal => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                _ => null, // arrays/objects other than roles/groups don't round-trip
            };
            if (stringValue is not null)
                customAttributes[key] = stringValue;
        }

        var subjectExpiry = tokenClaims.TryGetValue("exp", out var expValue)
            ? DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(expValue, System.Globalization.CultureInfo.InvariantCulture))
            : DateTimeOffset.UtcNow;

        var subject = new OidcSubject
        {
            SubjectId = sub,
            Roles = ExtractStringList(tokenClaims, "roles"),
            Groups = ExtractStringList(tokenClaims, "groups"),
            CustomAttributes = customAttributes.Count > 0 ? customAttributes : null,
            // The exchanged token may never outlive the token it was derived from — that cap is
            // what makes "short-lived downscoped token" true by construction, and it composes
            // with any upstream session cap already clamped into the subject token's exp.
            SessionMaxExpiresAt = subjectExpiry,
        };

        // Host seam for context-bound exchanges (e.g. project/workspace tokens): the transformer
        // validates any extra request parameters against the host's own authority and forces the
        // resulting binding claims onto the subject. Rejection surfaces as invalid_target.
        // The subject token's own client_id — the application the user authorized, as opposed to the
        // client performing this exchange. It is stripped from CustomAttributes as a reserved claim,
        // so the transformer has no other way to reach it.
        var subjectClientId = tokenClaims.TryGetValue("client_id", out var subjectClientValue)
            ? subjectClientValue as string
            : null;

        var transformed = await exchangeTransformer.TransformAsync(
            subject, client, grantedScopes, extraParameters ?? EmptyExtraParameters,
            new TokenExchangeContext(subjectClientId), ct);
        switch (transformed)
        {
            case OidcSubjectResult.Rejected rejected:
                throw new InvalidOperationException(
                    $"Exchange rejected: {rejected.Description ?? rejected.Reason.ToString()}");
            case OidcSubjectResult.Allowed allowed:
                subject = allowed.Subject;
                break;
        }

        // The transformer may shorten the lifetime but never lengthen it past the subject token.
        var effectiveExpiry = subject.SessionMaxExpiresAt is { } transformedCap && transformedCap < subjectExpiry
            ? transformedCap
            : subjectExpiry;

        // ── Fine-grained authority ────────────────────────────────────────────────────────
        // Two operands are always available: the subject token's own authorization_details
        // claim (absent = unrestricted, garbled = empty — a narrow token must never widen)
        // and the request's authorization_details parameter.
        var requestedAuthority = AuthoritySet.Unrestricted;
        if (authorizationDetailsJson is not null &&
            !AuthorityJson.TryParse(authorizationDetailsJson, out requestedAuthority))
            throw new ProtocolTokenException("invalid_authorization_details",
                "authorization_details must be an RFC 9396 array of objects with a string 'type'");

        // RFC 9396 §5: the AS must reject a `type` it does not understand. Unknown types were parsed,
        // carried through the intersection and RE-EMITTED into the signed claim, so the token asserted
        // authority over a resource this AS has no definition for — a resource server that trusts the
        // issuer would honour a grant nobody here could evaluate or audit. Only enforced when a catalog is
        // registered; a host with no catalog has no vocabulary to check against.
        if (connectorCatalog is not null && !requestedAuthority.IsUnrestricted)
        {
            foreach (var grant in requestedAuthority.Grants)
            {
                var descriptor = await connectorCatalog.GetAsync(grant.Type, ct);
                if (descriptor is null)
                    throw new ProtocolTokenException("invalid_authorization_details",
                        $"unknown authorization_details type '{grant.Type}'");

                if (descriptor.Actions is { Count: > 0 })
                {
                    var unknown = grant.Actions.FirstOrDefault(a =>
                        !descriptor.Actions.Any(d => string.Equals(d.Name, a, StringComparison.Ordinal)));
                    if (unknown is not null)
                        throw new ProtocolTokenException("invalid_authorization_details",
                            $"unknown action '{unknown}' for authorization_details type '{grant.Type}'");
                }
            }
        }

        var subjectAuthority = ReadAuthorityClaim(subjectToken);

        AuthoritySet? effective = null;
        string? actorJson = null;
        string? consumedApprovalId = null;
        IReadOnlyList<string> priorChain = [];

        if (agentProfile is not null)
        {
            if (agentProfile.Mode == AgentMode.Service)
                throw new ProtocolTokenException("unauthorized_client",
                    $"Client '{clientId}' is registered as a service-mode agent; token exchange is not permitted");

            // The floor: no standing consent, no delegation — the ceiling alone grants nothing.
            var consentGrant = await grantStore.GetAsync(AgentConsent.Key(sub, clientId), ct);
            AuthoritySet floor = AuthoritySet.Empty;
            // SubjectId and ClientId are re-checked on the retrieved record, not inferred from the key
            // that found it. Trusting the key alone makes consent only as strong as key construction:
            // any future change to the format, or a subject id containing the separator, could make one
            // user's standing consent answer for another's — and standing agent consent is what lets an
            // agent mint delegated tokens without further interaction. The approval path already
            // re-checks both; this one did not.
            var hasConsent = consentGrant is not null
                && consentGrant.Type == AgentConsent.GrantType
                && consentGrant.ExpiresAt > DateTimeOffset.UtcNow
                && string.Equals(consentGrant.SubjectId, sub, StringComparison.Ordinal)
                && string.Equals(consentGrant.ClientId, clientId, StringComparison.Ordinal)
                && AgentConsent.TryParse(consentGrant.Data, out floor, out _);
            if (!hasConsent)
                throw new ProtocolTokenException("invalid_grant",
                    "consent_required: the subject has not granted this agent standing consent");

            // Sub-delegation depth: every actor already in the chain must have budget for one
            // more hop beneath it. An actor without a registered profile has budget 0.
            priorChain = ReadActorChain(subjectToken);
            for (var i = 0; i < priorChain.Count; i++)
            {
                var actorProfile = await agentProfileStore!.GetAsync(priorChain[i], ct);
                var budget = actorProfile?.MaxDelegationDepth ?? 0;
                if (i + 1 > budget)
                    throw new ProtocolTokenException("invalid_grant",
                        $"delegation depth exceeded: agent '{priorChain[i]}' permits {budget} hop(s) of sub-delegation");
            }

            // The invariant, literally: ceiling ∩ consent ∩ request ∩ what the subject token
            // itself already carried (which is what makes each further hop attenuate).
            var granted = agentProfile.Ceiling.Intersect(floor);

            // RFC 9396 §5, the member half. The type/action half above is only checked when a connector
            // catalog is registered; there is no schema for MEMBERS at all, so anything the parser did
            // not recognise became a "constraint" — and a constraint on one side of a meet carries over.
            // Refused here, against what the ceiling and consent actually granted, so an invented member
            // cannot ride the intersection into the signed claim.
            if (granted.FindUngrantedConstraint(requestedAuthority) is { } ungranted)
                throw new ProtocolTokenException("invalid_authorization_details",
                    $"'{ungranted.Member}' is not a member of the granted authority for type " +
                    $"'{ungranted.Type}'; only members the ceiling and consent define may be requested");

            effective = granted
                // The request and the subject token are supplied by the requesting party, so neither DECIDES
                // action policy — either may raise one (narrowing), but neither may create the explicit entry
                // that suppresses the profile's high-risk default. Without this an agent suppressed its own
                // approval gate with "action_policies": {"transfer": "auto"} in its own request. See
                // AuthoritySet.Intersect.
                .Intersect(requestedAuthority, merger: null, otherDecidesPolicy: false)
                .Intersect(subjectAuthority, merger: null, otherDecidesPolicy: false);
            effective = await ApplyHighRiskDefaultsAsync(effective, agentProfile.HighRiskDefault, ct);

            // Explicitly requested authority that the intersection denied is a hard error, not
            // a silent narrowing — an agent must not believe it holds authority it lacks.
            if (!requestedAuthority.IsUnrestricted)
            {
                var denied = requestedAuthority.Grants
                    .SelectMany(g => g.Actions.Select(a => (g.Type, Action: a)))
                    .Where(p => effective.PolicyFor(p.Type, p.Action) == ActionPolicy.Deny)
                    .Select(p => $"{p.Type}:{p.Action}")
                    .ToList();
                // RFC 9396 §5 defines invalid_authorization_details for authorization_details the AS
                // will not grant. invalid_target is RFC 8707's code for an unacceptable `resource` —
                // a different parameter — so a client that handled the two separately (retry with a
                // narrower resource vs. re-request the authority) was told to do the wrong thing.
                if (denied.Count > 0)
                    throw new ProtocolTokenException("invalid_authorization_details",
                        $"requested authority is not grantable: {string.Join(", ", denied)}");
            }

            if (effective.Grants.Count == 0)
                throw new ProtocolTokenException("invalid_authorization_details",
                    "the intersection of ceiling, consent and request grants no authority");

            // Ask-gate: any ask-policy action in the slice parks the exchange on a pending
            // approval; approval_id resumes it. The hash binds the approval to this exact
            // request shape — and to the CURRENT policy state, so an admin edit between park
            // and poll invalidates the approval instead of minting stale authority.
            var askActions = effective.Grants
                .SelectMany(g => g.Actions
                    .Where(a => g.PolicyFor(a) == ActionPolicy.Ask)
                    .Select(a => $"{g.Type}:{a}"))
                .ToList();
            if (askActions.Count > 0)
            {
                var requestHash = ComputeRequestHash(
                    clientId, sub, grantedScopes, targetAudiences, AuthorityJson.Serialize(effective),
                    extraParameters);
                if (approvalId is not null)
                {
                    var approvedSlice = await RedeemApprovalAsync(approvalId, clientId, sub, requestHash, ct);
                    // Re-guard against the live ceiling/floor, then mark the asked-and-answered
                    // actions auto so the resource side doesn't gate them a second time.
                    effective = MapAskPolicies(
                        approvedSlice.Intersect(agentProfile.Ceiling).Intersect(floor),
                        ActionPolicy.Auto);
                    consumedApprovalId = approvalId;
                }
                else
                {
                    var id = await CreatePendingApprovalAsync(
                        clientId, sub, effective, askActions, requestHash,
                        extraParameters ?? EmptyExtraParameters, ct);
                    throw new ApprovalPendingException(id, Approval.PollIntervalSeconds);
                }
            }

            // Composite identity, never impersonation: sub stays the user; this agent goes on
            // top of the act chain, prior actors nest inside (RFC 8693 §4.1).
            actorJson = BuildActorClaim(clientId, subjectToken);

            // Profile lifetime cap composes with the subject-token remainder and session caps.
            var profileCap = DateTimeOffset.UtcNow.AddSeconds(agentProfile.MaxTokenLifetimeSeconds);
            if (profileCap < effectiveExpiry)
                effectiveExpiry = profileCap;
        }
        else if (!requestedAuthority.IsUnrestricted || !subjectAuthority.IsUnrestricted)
        {
            // No profile — plain exchange. Authority may only NARROW here, never originate.
            //
            // The subject token has authority to attenuate only if it carries an authorization_details
            // claim. When it does not, ReadAuthorityClaim yields Unrestricted, and
            // Unrestricted.Intersect(requested) returns `requested` VERBATIM (AuthoritySet.cs:62-63) — so
            // the client's request became the claim the AS signed. That is authority forgery, not
            // narrowing: any client holding the exchange grant could mint an issuer-signed token asserting
            // fine-grained authority (payment initiation, and so on) that no admin ceiling, no consent
            // record and no user interaction ever produced. And it is the universal case, because no token
            // issued via the authorization-code, refresh, device or profile-less client-credentials paths
            // carries authorization_details at all.
            //
            // A client with nothing to attenuate has nothing to request.
            if (subjectAuthority.IsUnrestricted && !requestedAuthority.IsUnrestricted)
                throw new ProtocolTokenException("invalid_authorization_details",
                    "the subject token carries no authorization_details to narrow, so this exchange cannot " +
                    "request any; an agent profile is required to originate authority");

            // Same member check as the profile branch: the subject token's own claim is the vocabulary
            // an attenuating request may narrow within, and a member it never carried was granted by
            // nobody — see AuthoritySet.FindUngrantedConstraint.
            if (subjectAuthority.FindUngrantedConstraint(requestedAuthority) is { } ungrantedMember)
                throw new ProtocolTokenException("invalid_authorization_details",
                    $"'{ungrantedMember.Member}' is not a member of the subject token's authority for type " +
                    $"'{ungrantedMember.Type}'; an exchange may narrow the authority it holds, not add to it");

            // Same provenance rule as the profile branch: the request is the requesting party's.
            effective = subjectAuthority.Intersect(requestedAuthority, merger: null, otherDecidesPolicy: false);
            if (effective.Grants.Count == 0)
                throw new ProtocolTokenException("invalid_authorization_details",
                    "the requested authorization_details are not within the subject token's authority");
        }

        // Delegation provenance survives an exchange by a client that has NO agent profile. Both the act
        // chain and the sub-delegation budget used to be handled only inside the profile branch above, so an
        // unprofiled exchange client stripped the RFC 8693 `act` chain and skipped MaxDelegationDepth
        // entirely — laundering a delegated token into one that looks first-party, with no record of the
        // agents it passed through and no bound on how many more hops it could take.
        if (agentProfile is null && actorJson is null && !string.IsNullOrEmpty(subjectToken))
        {
            var carriedChain = ReadActorChain(subjectToken);
            if (carriedChain.Count > 0)
            {
                if (agentProfileStore is not null)
                {
                    for (var i = 0; i < carriedChain.Count; i++)
                    {
                        var actorProfile = await agentProfileStore.GetAsync(carriedChain[i], ct);
                        var budget = actorProfile?.MaxDelegationDepth ?? 0;
                        if (i + 1 > budget)
                            throw new ProtocolTokenException("invalid_grant",
                                $"delegation depth exceeded: agent '{carriedChain[i]}' permits {budget} hop(s) of sub-delegation");
                    }
                }

                // This client IS acting on the subject's behalf, profile or not — record it on top of the
                // existing chain rather than discarding the chain.
                actorJson = BuildActorClaim(clientId, subjectToken);
            }
        }

        subject = subject with { SessionMaxExpiresAt = effectiveExpiry };

        // Asked of the WIRE form, not the structural set. The Grants.Count checks above cannot see this: a
        // grant whose constraint met to nothing is still IN the set and PolicyFor reports its actions
        // grantable, but ToNode drops it — and `authorization_details: []` evaluates as UNRESTRICTED at the
        // resource server. See AuthorityJson.SerializesToNothing.
        if (effective is not null && AuthorityJson.SerializesToNothing(effective))
            throw new ProtocolTokenException("invalid_authorization_details",
                "the granted authority permits nothing once denied actions and unsatisfiable constraints are "
                + "removed; no token can express it");

        var effectiveJson = effective is null ? null : AuthorityJson.Serialize(effective);

        if (agentProfile is not null)
        {
            // Called here, AFTER `effective` is computed and before the token is created, so the gate
            // sees what is actually being granted rather than what was asked for.
            await Hooks.RunOnTokenIssuingAsync(new TokenIssuanceContext(
                clientId, sub, GrantTypes.TokenExchange, grantedScopes, authorizationDetailsJson)
            {
                EffectiveAuthorityJson = effectiveJson,
            }, ct);
        }

        var accessToken = await CreateAccessTokenAsync(
            subject, client, grantedScopes, targetAudiences.Count > 0 ? targetAudiences : null,
            effectiveJson, actorJson, null, ct);

        var expiresIn = (int)Math.Max(0, Math.Min(
            client.AccessTokenLifetimeSeconds,
            (effectiveExpiry - DateTimeOffset.UtcNow).TotalSeconds));

        if (agentProfile is not null)
        {
            await Hooks.RunOnDelegationMintedAsync(new DelegationAudit(
                sub, [clientId, .. priorChain], effectiveJson!, effectiveExpiry, consumedApprovalId), ct);
            logger.LogInformation(
                "Delegation minted. Agent: {ClientId}, Subject: {SubjectId}, Chain depth: {Depth}, Approval: {ApprovalId}",
                clientId, sub, priorChain.Count + 1, consumedApprovalId ?? "none");
        }
        else
        {
            logger.LogInformation(
                "Token exchange issued downscoped token. Client: {ClientId}, Subject: {SubjectId}, Scopes: {Scopes}",
                clientId, sub, string.Join(' ', grantedScopes));
        }

        return new TokenResponse
        {
            AccessToken = accessToken,
            IssuedTokenType = TokenTypeIdentifiers.AccessToken,
            ExpiresIn = expiresIn,
            Scope = string.Join(' ', grantedScopes),
            AuthorizationDetails = ToElement(effectiveJson),
        };
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyExtraParameters =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // ── Agentic delegation helpers ───────────────────────────────────────────────────────

    /// <summary>The subject token's authorization_details claim: absent = unrestricted,
    /// garbled = empty — a corrupt narrow token must never evaluate wider than minted.</summary>
    private static AuthoritySet ReadAuthorityClaim(string token)
    {
        var jwt = new JsonWebToken(token);
        if (!jwt.TryGetPayloadValue<JsonElement>(AuthorityClaims.AuthorizationDetails, out var element))
            return AuthoritySet.Unrestricted;
        return AuthorityJson.TryParse(element.GetRawText(), out var set) ? set : AuthoritySet.Empty;
    }

    /// <summary>Walks the RFC 8693 act chain of a token: outermost (most recent actor) first.</summary>
    private static IReadOnlyList<string> ReadActorChain(string token)
    {
        var jwt = new JsonWebToken(token);
        if (!jwt.TryGetPayloadValue<JsonElement>(AuthorityClaims.Actor, out var act))
            return [];

        var chain = new List<string>();
        while (act.ValueKind == JsonValueKind.Object)
        {
            if (!act.TryGetProperty("sub", out var actorSub) || actorSub.ValueKind != JsonValueKind.String)
                break;
            chain.Add(actorSub.GetString()!);
            if (!act.TryGetProperty(AuthorityClaims.Actor, out var nested))
                break;
            act = nested;
        }
        return chain;
    }

    /// <summary>New act claim: this actor on top, the subject token's chain nested inside.</summary>
    private static string BuildActorClaim(string actorClientId, string subjectToken)
    {
        var node = new JsonObject { ["sub"] = actorClientId };
        var jwt = new JsonWebToken(subjectToken);
        if (jwt.TryGetPayloadValue<JsonElement>(AuthorityClaims.Actor, out var prior) &&
            prior.ValueKind == JsonValueKind.Object)
        {
            node[AuthorityClaims.Actor] = JsonNode.Parse(prior.GetRawText());
        }
        return node.ToJsonString();
    }

    /// <summary>
    /// Applies the profile's high-risk default to catalog-flagged actions that neither the
    /// ceiling nor the consent pinned explicitly (an explicit per-action entry — even auto —
    /// is a deliberate admin/user decision and wins).
    /// </summary>
    private async Task<AuthoritySet> ApplyHighRiskDefaultsAsync(
        AuthoritySet set, ActionPolicy highRiskDefault, CancellationToken ct)
    {
        if (highRiskDefault == ActionPolicy.Auto || connectorCatalog is null || set.IsUnrestricted)
            return set;

        List<AuthorityGrant>? rebuilt = null;
        foreach (var grant in set.Grants)
        {
            var descriptor = await connectorCatalog.GetAsync(grant.Type, ct);
            if (descriptor?.Actions is null) continue;

            Dictionary<string, ActionPolicy>? policies = null;
            foreach (var action in grant.Actions)
            {
                if (grant.ActionPolicies.ContainsKey(action)) continue;
                if (!descriptor.Actions.Any(a =>
                        a.HighRisk && string.Equals(a.Name, action, StringComparison.Ordinal)))
                    continue;
                policies ??= new Dictionary<string, ActionPolicy>(grant.ActionPolicies, StringComparer.Ordinal);
                policies[action] = highRiskDefault;
            }
            if (policies is null) continue;

            rebuilt ??= [.. set.Grants];
            rebuilt[rebuilt.IndexOf(grant)] = grant with { ActionPolicies = policies };
        }
        return rebuilt is null ? set : AuthoritySet.From(rebuilt);
    }

    /// <summary>Rewrites every ask policy to <paramref name="askBecomes"/>: deny for service
    /// mints (no user to ask), auto after a consumed approval (asked and answered).</summary>
    private static AuthoritySet MapAskPolicies(AuthoritySet set, ActionPolicy askBecomes)
    {
        if (set.IsUnrestricted) return set;
        var rebuilt = set.Grants.Select(grant =>
        {
            if (!grant.ActionPolicies.Values.Contains(ActionPolicy.Ask)) return grant;
            var policies = grant.ActionPolicies.ToDictionary(
                p => p.Key,
                p => p.Value == ActionPolicy.Ask ? askBecomes : p.Value,
                StringComparer.Ordinal);
            return grant with { ActionPolicies = policies };
        }).ToList();
        return AuthoritySet.From(rebuilt);
    }

    /// <summary>Binds an approval to the exact request shape (and current policy state) it was
    /// minted for — a retry with different scopes, audiences or authority cannot spend it.</summary>
    /// <param name="extraParameters">
    /// The host extension parameters — every non-protocol form field, which the exchange forwards to
    /// <c>ITokenExchangeSubjectTransformer</c>.
    /// </param>
    /// <remarks>
    /// These were omitted from the hash, and they are exactly the dimension a context-bound exchange
    /// is scoped by: the transformer is the documented seam for project/workspace tokens, and it
    /// "forces the resulting binding claims onto the subject". Because it runs before the authority
    /// section and nothing covered its inputs, the request parked for approval and the request that
    /// redeemed it could differ in precisely the parameter that decides WHICH tenant the approved
    /// authority binds to — while the documentation claimed approvals are "bound to the exact request
    /// shape". The approval UI shows the client, the pending type:action pairs and the authority
    /// slice, never the context binding, so a human could not compensate for the gap either.
    /// <para>
    /// Every component is length-prefixed rather than newline-joined. The previous encoding was
    /// ambiguous on its own terms — a clientId or subjectId containing a newline could impersonate a
    /// field boundary — and adding free-form host parameters to a delimiter-joined string would have
    /// made that reachable rather than theoretical.
    /// </para>
    /// </remarks>
    private static string ComputeRequestHash(
        string clientId, string subjectId, IEnumerable<string> scopes,
        IEnumerable<string> audiences, string effectiveAuthorityJson,
        IReadOnlyDictionary<string, string>? extraParameters = null)
    {
        var builder = new StringBuilder();

        void Append(string value)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('|');
        }

        Append(clientId);
        Append(subjectId);
        Append(string.Join(' ', scopes.Order(StringComparer.Ordinal)));
        Append(string.Join(' ', audiences.Order(StringComparer.Ordinal)));
        Append(effectiveAuthorityJson);

        foreach (var (name, value) in (extraParameters ?? EmptyExtraParameters).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Append(name);
            Append(value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// How many distinct approvals one agent may leave awaiting a single user's decision. A human queue is
    /// a scarce resource; without a cap an agent could bury a genuine request under noise.
    /// </summary>
    private const int MaxOutstandingApprovalsPerAgent = 20;

    private async Task<string> CreatePendingApprovalAsync(
        string clientId, string subjectId, AuthoritySet slice,
        IReadOnlyList<string> pendingActions, string requestHash,
        IReadOnlyDictionary<string, string> context, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Idempotency + a flood bound. Nothing previously stopped an agent with standing consent from
        // parking an unbounded number of approvals in a human's queue: every retry of the SAME request
        // created another one, and a loop could bury a genuine request under noise (or exhaust grant
        // storage). RequestHash already identifies the exact request, so an identical pending approval is
        // returned rather than duplicated, and distinct ones are capped per (agent, subject).
        var existing = await grantStore.GetBySubjectAsync(subjectId, ct);
        var pending = existing
            .Where(g => g.Type == Approval.GrantType
                        && string.Equals(g.ClientId, clientId, StringComparison.Ordinal)
                        && g.ExpiresAt > now)
            .ToList();

        foreach (var g in pending)
        {
            var parsed = Approval.Parse(g.Data);
            if (parsed is { Status: ApprovalStatus.Pending }
                && string.Equals(parsed.RequestHash, requestHash, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Reusing pending approval {ApprovalId} for an identical request from agent {ClientId}",
                    parsed.Id, clientId);
                return parsed.Id;
            }
        }

        if (pending.Count >= MaxOutstandingApprovalsPerAgent)
            throw new ProtocolTokenException("invalid_grant",
                $"too many approvals are already awaiting this user's decision for this agent " +
                $"(limit {MaxOutstandingApprovalsPerAgent}); resolve or let them expire before requesting more");

        var id = Guid.NewGuid().ToString("N");
        var data = new ApprovalData
        {
            Id = id,
            ClientId = clientId,
            SubjectId = subjectId,
            Slice = slice,
            PendingActions = pendingActions,
            RequestHash = requestHash,
            Context = context,
            Status = ApprovalStatus.Pending,
            CreatedAt = now,
        };
        await grantStore.StoreAsync(new PersistedGrant
        {
            Key = Approval.Key(id),
            Type = Approval.GrantType,
            SubjectId = subjectId,
            ClientId = clientId,
            Data = Approval.Serialize(data),
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(protocolOptions.Value.ApprovalLifetimeSeconds),
        }, ct);

        await Hooks.RunOnApprovalRequestedAsync(
            new ApprovalAudit(id, clientId, subjectId, pendingActions, "pending"), ct);

        logger.LogInformation(
            "Delegation parked on approval {ApprovalId}. Agent: {ClientId}, Subject: {SubjectId}, Actions: {Actions}",
            id, clientId, subjectId, string.Join(", ", pendingActions));
        return id;
    }

    /// <summary>Device-flow vocabulary throughout: pending → authorization_pending (with
    /// slow_down throttling), denied → access_denied, gone/expired → expired_token. A win
    /// consumes atomically — two concurrent polls cannot both mint.</summary>
    private async Task<AuthoritySet> RedeemApprovalAsync(
        string approvalId, string clientId, string subjectId, string requestHash, CancellationToken ct)
    {
        var key = Approval.Key(approvalId);
        var grant = await grantStore.GetAsync(key, ct);
        if (grant is null || grant.Type != Approval.GrantType)
            throw new ProtocolTokenException("expired_token", "approval not found or expired");
        if (grant.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new ProtocolTokenException("expired_token", "approval has expired");
        if (grant.ConsumedAt is not null)
            throw new ProtocolTokenException("invalid_grant", "approval has already been used");

        var data = Approval.Parse(grant.Data)
            ?? throw new ProtocolTokenException("expired_token", "approval not found or expired");

        if (!string.Equals(data.ClientId, clientId, StringComparison.Ordinal) ||
            !string.Equals(data.SubjectId, subjectId, StringComparison.Ordinal))
            throw new ProtocolTokenException("invalid_grant", "approval was issued to a different request");
        if (!string.Equals(data.RequestHash, requestHash, StringComparison.Ordinal))
            throw new ProtocolTokenException("invalid_grant", "approval does not match this request");

        switch (data.Status)
        {
            case ApprovalStatus.Denied:
                throw new ProtocolTokenException("access_denied", "the user denied the request");

            case ApprovalStatus.Pending:
                // RFC 8628 §3.5 slow_down, through the rate limiter keyed on the approval handle — the
                // poll writes NOTHING back. This is the same move the device flow already made
                // (Server/Endpoints/TokenEndpoint.cs), for the same two reasons.
                //
                // A poll used to persist LastPolledAt with an unconditional StoreAsync of the WHOLE
                // payload, Status included, that it had read moments earlier — so a poll racing the
                // user's decision wrote the stale `Pending` back over an approve or a DENY: the agent's
                // own polling undoing the answer it was waiting for. Re-reading first narrowed that
                // window but could not close it, because read-check-write is not atomic and IGrantStore
                // has no conditional write for the payload. And the throttle it bought did not survive
                // concurrency anyway: two parallel polls both read the old timestamp, both passed, and
                // both wrote, so the interval bound was defeated by the very traffic pattern it exists
                // to bound. The limiter's check-and-increment is atomic, so parallel polls spend one
                // shared budget — and it is keyed on the approval handle, so one agent's polling cannot
                // throttle another's.
                if (await PollLimiter.IsRateLimitedAsync(
                        $"approval-poll|{approvalId}", 1, TimeSpan.FromSeconds(Approval.PollIntervalSeconds), ct))
                    throw new ProtocolTokenException("slow_down",
                        "Polling too frequently. Increase your interval and try again.");

                throw new ApprovalPendingException(approvalId, Approval.PollIntervalSeconds);

            default:
                // Approved — consume atomically; the loser of a concurrent poll race gets
                // invalid_grant instead of a second token minted from the same approval.
                grant.Key = key;
                grant.ConsumedAt = DateTimeOffset.UtcNow;
                if (!await grantStore.TryMarkConsumedAsync(grant, ct))
                    throw new ProtocolTokenException("invalid_grant", "approval has already been used");
                return data.Slice;
        }
    }

    private static JsonElement? ToElement(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<string>? ExtractStringList(
        IDictionary<string, object> claims, string name)
    {
        if (!claims.TryGetValue(name, out var value)) return null;
        var list = value switch
        {
            string s => [s],
            IEnumerable<object> items => items.OfType<string>().ToList(),
            _ => new List<string>(),
        };
        return list.Count > 0 ? list : null;
    }

    public async Task<TokenResponse> HandleDeviceCodeAsync(
        OidcSubject subject,
        OAuthClient client,
        IReadOnlyList<string> scopes,
        CancellationToken ct = default)
    {
        var scopeList = scopes.ToList();

        var accessToken = await MintAccessTokenAsync(subject, client, scopeList, ct: ct);

        string? refreshToken = null;
        if (scopeList.Contains(StandardScopes.OfflineAccess) && client.AllowOfflineAccess)
        {
            refreshToken = await CreateRefreshTokenAsync(
                subject, client, scopeList, null, null,
                [new IssuedAccessToken { Jti = accessToken.Jti, ExpiresAt = accessToken.ExpiresAt }], ct);
        }

        string? idToken = null;
        if (scopeList.Contains(StandardScopes.OpenId))
        {
            idToken = await CreateIdTokenAsync(subject, client, scopeList, ct: ct);
        }

        logger.LogInformation(
            "Device code token issued for subject {SubjectId} via client {ClientId}",
            subject.SubjectId, client.ClientId);

        return new TokenResponse
        {
            AccessToken = accessToken.Token,
            RefreshToken = refreshToken,
            IdToken = idToken,
            ExpiresIn = accessToken.ExpiresInSeconds,
            Scope = string.Join(' ', scopeList)
        };
    }

    public async Task<bool> RevokeRefreshTokenAsync(string token, string clientId, CancellationToken ct = default)
    {
        var grant = await grantStore.GetAsync(token, ct);

        if (grant is null || grant.Type != "refresh_token")
            return false;

        if (!string.Equals(grant.ClientId, clientId, StringComparison.Ordinal))
            return false;

        // RFC 7009 §2.1: an AS that supports access-token revocation SHOULD also invalidate the access
        // tokens issued under the same grant. This one does support it — IRevokedTokenStore is enforced
        // at userinfo, introspection, the JwtBearer scheme and the token-exchange subject check — so the
        // SHOULD is engaged. Before this, revocation killed the refresh token and left the access token
        // minted from it valid for up to AccessTokenLifetimeSeconds, so incident response silently
        // failed to do what the operator believed it had done.
        await GrantRevocation.RevokeTrackedAccessTokensAsync(revokedTokenStore, grant, logger, ct);

        await grantStore.RemoveAsync(token, ct);

        logger.LogInformation("Refresh token revoked for client {ClientId}", clientId);
        return true;
    }

    /// <summary>
    /// Drops access-token entries that have expired on their own and caps what remains, newest first.
    /// </summary>
    /// <remarks>
    /// An expired jti needs no revocation entry — every enforcement point already rejects the token on
    /// <c>exp</c> — so pruning here is what keeps the tracked set at one or two entries in steady
    /// state instead of growing with the family's 30-day absolute life. Returns null rather than an
    /// empty list so grants that track nothing serialize without the member.
    /// </remarks>
    private static List<IssuedAccessToken>? PruneAccessTokens(
        IEnumerable<IssuedAccessToken>? tokens, DateTimeOffset now)
    {
        if (tokens is null) return null;

        var live = tokens
            .Where(t => t.ExpiresAt > now)
            .OrderByDescending(t => t.ExpiresAt)
            .Take(RefreshTokenData.MaxTrackedAccessTokens)
            .ToList();

        return live.Count > 0 ? live : null;
    }

    private static string GenerateRefreshTokenHandle()
    {
        var bytes = RandomNumberGenerator.GetBytes(RefreshTokenSizeBytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// The OIDC Core §5.4 standard claim sets, gated on the scopes that release them.
    /// </summary>
    /// <remarks>
    /// Shared by the id_token and the access token, because <c>/connect/userinfo</c> in this host answers from
    /// the ACCESS token — "userinfo returns whatever claims the access token carried … relying parties should
    /// call userinfo for a snapshot, not fresh re-resolution" — and the mint path never wrote any of them. So
    /// its <c>CopyIfScoped("email", …)</c> / <c>CopyIfScoped("profile", …)</c> lines had nothing to copy: the
    /// endpoint returned <c>sub</c> and nothing else, while discovery advertised the full set. Those lines only
    /// make sense if the claims are on the token, which is the design this restores.
    /// <para>
    /// One projection rather than two, so the id_token and userinfo cannot answer differently about the same
    /// subject and scopes — a client comparing them would otherwise see a name in the id_token and silence
    /// from userinfo with no way to tell which was authoritative.
    /// </para>
    /// <para>
    /// The gating is unchanged and is what bounds the exposure: nothing is released for a scope the client was
    /// not granted, so a resource server sees exactly what the user consented to and no more.
    /// </para>
    /// </remarks>
    private static void AddScopedIdentityClaims(
        Dictionary<string, object> claims, OidcSubject subject, IReadOnlyList<string> scopeList, bool always)
    {
        if (always || scopeList.Contains(StandardScopes.Email))
        {
            if (!string.IsNullOrEmpty(subject.Email))
                claims["email"] = subject.Email;

            claims["email_verified"] = subject.EmailVerified;
        }

        if (always || scopeList.Contains(StandardScopes.Profile))
        {
            if (!string.IsNullOrEmpty(subject.GivenName))
                claims["given_name"] = subject.GivenName;

            if (!string.IsNullOrEmpty(subject.FamilyName))
                claims["family_name"] = subject.FamilyName;

            var fullName = subject.Name ?? BuildFullName(subject.GivenName, subject.FamilyName);
            if (!string.IsNullOrEmpty(fullName))
                claims["name"] = fullName;

            if (!string.IsNullOrEmpty(subject.Locale))
                claims["locale"] = subject.Locale;

            // org_id describes the account's placement, so it travels with the profile set.
            if (!string.IsNullOrEmpty(subject.OrganizationId))
                claims["org_id"] = subject.OrganizationId;
        }

        // §5.4 assigns the phone claims their own scope. They rode `profile` before, which is both the wrong
        // binding and one the user was never shown.
        if ((always || scopeList.Contains(StandardScopes.Phone)) && !string.IsNullOrEmpty(subject.Phone))
            claims["phone_number"] = subject.Phone;
    }

    private static string? BuildFullName(string? firstName, string? lastName)
    {
        return (firstName, lastName) switch
        {
            (not null, not null) => $"{firstName} {lastName}",
            (not null, null) => firstName,
            (null, not null) => lastName,
            _ => null
        };
    }

    private async Task<HashSet<string>> GetAllowedCustomClaimNamesAsync(
        IEnumerable<string> scopes, CancellationToken ct)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scopeName in scopes)
        {
            if (scopeName is StandardScopes.OpenId or StandardScopes.Profile
                or StandardScopes.Email or StandardScopes.OfflineAccess)
                continue;

            var scope = await scopeStore.GetAsync(scopeName, ct);
            if (scope is null) continue;

            foreach (var claim in scope.UserClaims)
                allowed.Add(claim);
        }
        return allowed;
    }

    private static void MergeCustomClaims(
        IDictionary<string, object> claims,
        IEnumerable<KeyValuePair<string, string>>? source,
        HashSet<string> allowedNames,
        bool overwriteExisting)
    {
        if (source is null) return;
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (ReservedClaimNames.Contains(key)) continue;
            if (!allowedNames.Contains(key)) continue;
            if (!overwriteExisting && claims.ContainsKey(key)) continue;
            claims[key] = value;
        }
    }
}

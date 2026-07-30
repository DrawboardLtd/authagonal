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
    IRevokedTokenStore? revokedTokenStore = null) : IProtocolTokenService
{
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
        var now = DateTimeOffset.UtcNow;
        var scopeList = scopes.ToList();

        var claims = new Dictionary<string, object>
        {
            ["client_id"] = client.ClientId,
            ["scope"] = string.Join(' ', scopeList),
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["iat"] = now.ToUnixTimeSeconds()
        };

        if (subject is not null)
        {
            claims["sub"] = subject.SubjectId;

            if (subject.Roles is { Count: > 0 })
                claims["roles"] = subject.Roles.ToArray();

            if (subject.Groups is { Count: > 0 })
                claims["groups"] = subject.Groups.ToArray();
        }

        // Scope-gated custom attributes — emitted only when a requested scope's UserClaims
        // whitelist releases them. Protocol/reserved claims always win.
        var allowedCustomClaims = await GetAllowedCustomClaimNamesAsync(scopeList, ct);
        if (subject?.CustomAttributes is not null)
        {
            MergeCustomClaims(claims, subject.CustomAttributes, allowedCustomClaims, overwriteExisting: false);
        }

        // Federation claims layered on top — same scope gate, but federation values
        // win on collision because they describe the authoritative state of the
        // upstream-issued session.
        if (subject?.FederationClaims is not null)
        {
            MergeCustomClaims(claims, subject.FederationClaims, allowedCustomClaims, overwriteExisting: true);
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
        // nothing in those bags can shadow them.
        if (!string.IsNullOrEmpty(authorizationDetailsJson))
        {
            using var doc = JsonDocument.Parse(authorizationDetailsJson);
            claims[AuthorityClaims.AuthorizationDetails] = doc.RootElement.Clone();
        }
        if (!string.IsNullOrEmpty(actorJson))
        {
            using var doc = JsonDocument.Parse(actorJson);
            claims[AuthorityClaims.Actor] = doc.RootElement.Clone();
        }

        // Clamp lifetime by session cap if present.
        var expires = now.AddSeconds(client.AccessTokenLifetimeSeconds);
        if (subject?.SessionMaxExpiresAt is { } sessionCap && sessionCap < expires)
            expires = sessionCap;
        if (notAfter is { } issuanceCap && issuanceCap < expires)
            expires = issuanceCap;

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
        return handler.CreateToken(descriptor);
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

        if (scopeList.Contains(StandardScopes.Email) || scopeList.Contains(StandardScopes.OpenId))
        {
            if (!string.IsNullOrEmpty(subject.Email))
                claims["email"] = subject.Email;

            claims["email_verified"] = subject.EmailVerified;
        }

        if (scopeList.Contains(StandardScopes.Profile) || scopeList.Contains(StandardScopes.OpenId))
        {
            if (!string.IsNullOrEmpty(subject.GivenName))
                claims["given_name"] = subject.GivenName;

            if (!string.IsNullOrEmpty(subject.FamilyName))
                claims["family_name"] = subject.FamilyName;

            var fullName = subject.Name ?? BuildFullName(subject.GivenName, subject.FamilyName);
            if (!string.IsNullOrEmpty(fullName))
                claims["name"] = fullName;

            if (!string.IsNullOrEmpty(subject.Phone))
                claims["phone_number"] = subject.Phone;

            if (!string.IsNullOrEmpty(subject.Locale))
                claims["locale"] = subject.Locale;
        }

        if (!string.IsNullOrEmpty(subject.OrganizationId))
            claims["org_id"] = subject.OrganizationId;

        if (subject.Roles is { Count: > 0 })
            claims["roles"] = subject.Roles.ToArray();

        if (subject.Groups is { Count: > 0 })
            claims["groups"] = subject.Groups.ToArray();

        var allowedCustomClaims = await GetAllowedCustomClaimNamesAsync(scopeList, ct);
        if (subject.CustomAttributes is not null)
        {
            MergeCustomClaims(claims, subject.CustomAttributes, allowedCustomClaims, overwriteExisting: false);
        }
        if (subject.FederationClaims is not null)
        {
            MergeCustomClaims(claims, subject.FederationClaims, allowedCustomClaims, overwriteExisting: true);
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

    public async Task<string> CreateRefreshTokenAsync(
        OidcSubject subject,
        OAuthClient client,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources = null,
        DateTimeOffset? originalCreatedAt = null,
        CancellationToken ct = default)
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

        // Atomic single-use: only the request that wins the conditional delete may redeem the code,
        // so two concurrent token requests with the same code can't both succeed.
        if (!await grantStore.TryConsumeAsync(code, ct))
            throw new InvalidOperationException("Authorization code has already been used");

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

        var accessToken = await CreateAccessTokenAsync(subject, client, authCode.Scopes, authCode.Resources, ct: ct);

        string? idToken = null;
        if (authCode.Scopes.Contains(StandardScopes.OpenId))
        {
            idToken = await CreateIdTokenAsync(subject, client, authCode.Scopes, authCode.Nonce, ct);
        }

        string? refreshToken = null;
        if (authCode.Scopes.Contains(StandardScopes.OfflineAccess) && client.AllowOfflineAccess)
        {
            refreshToken = await CreateRefreshTokenAsync(
                subject, client, authCode.Scopes, authCode.Resources, ct: ct);
        }

        logger.LogInformation(
            "Authorization code exchanged for tokens. Client: {ClientId}, Subject: {SubjectId}",
            clientId, subject.SubjectId);

        return new TokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = client.AccessTokenLifetimeSeconds,
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
                    successor.ExpiresAt > now)
                {
                    return await ReissueFromSuccessorAsync(successor, data.SuccessorKey, resources, ct);
                }
            }

            logger.LogError(
                "Refresh token replay detected! Revoking all tokens for subject. Client: {ClientId}, Subject: {SubjectId}",
                clientId, grant.SubjectId);

            if (grant.SubjectId is not null)
            {
                await grantStore.RemoveAllBySubjectAndClientAsync(grant.SubjectId, clientId, ct);
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

        // Rotation: issue successor, mark old consumed with successor key recorded.
        var originalCreatedAt = data.OriginalCreatedAt ?? data.CreatedAt;
        var newRefreshToken = await CreateRefreshTokenAsync(
            freshSubject, client, data.Scopes, data.Resources, originalCreatedAt, ct);

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

        var accessToken = await CreateAccessTokenAsync(freshSubject, client, data.Scopes, tokenResources, ct: ct);

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
            AccessToken = accessToken,
            ExpiresIn = client.AccessTokenLifetimeSeconds,
            IdToken = idToken,
            RefreshToken = newRefreshToken,
            Scope = string.Join(' ', data.Scopes)
        };
    }

    private async Task<TokenResponse> ReissueFromSuccessorAsync(
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

        var accessToken = await CreateAccessTokenAsync(data.Subject, client, data.Scopes, tokenResources, ct: ct);

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
            AccessToken = accessToken,
            ExpiresIn = client.AccessTokenLifetimeSeconds,
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
                throw new InvalidOperationException($"Scope '{scope}' is not allowed for client '{clientId}'");
        }

        var resourceList = resources?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (resourceList is { Count: > 0 })
        {
            foreach (var r in resourceList)
            {
                if (!Uri.TryCreate(r, UriKind.Absolute, out var u) || !string.IsNullOrEmpty(u.Fragment))
                    throw new InvalidOperationException($"Resource '{r}' is not a valid absolute URI");
                // Empty Audiences means unset, not deny-all — see AuthorizeRequestSupport.
                if (client.Audiences.Count > 0 && !client.Audiences.Contains(r, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Resource '{r}' is not registered for this client");
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
            authorityJson = AuthorityJson.Serialize(MapAskPolicies(authority, ActionPolicy.Deny));
            notAfter = DateTimeOffset.UtcNow.AddSeconds(profile.MaxTokenLifetimeSeconds);
            expiresIn = Math.Min(expiresIn, profile.MaxTokenLifetimeSeconds);

            await Hooks.RunOnTokenIssuingAsync(
                new TokenIssuanceContext(clientId, null, GrantTypes.ClientCredentials, scopeList, authorityJson), ct);
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
                    throw new InvalidOperationException($"Scope '{s}' exceeds the subject token's scopes");
                if (!client.AllowedScopes.Contains(s))
                    throw new InvalidOperationException($"Scope '{s}' is not allowed for client '{clientId}'");
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
        var targetAudiences = new List<string>();
        foreach (var r in resources?.Where(r => !string.IsNullOrWhiteSpace(r)) ?? [])
        {
            if (!Uri.TryCreate(r, UriKind.Absolute, out var u) || !string.IsNullOrEmpty(u.Fragment))
                throw new InvalidOperationException($"Resource '{r}' is not a valid absolute URI");
            if (client.Audiences.Count > 0 && !client.Audiences.Contains(r, StringComparer.Ordinal))
                throw new InvalidOperationException($"Resource '{r}' is not registered for this client");
            targetAudiences.Add(r);
        }
        foreach (var a in audiences?.Where(a => !string.IsNullOrWhiteSpace(a)) ?? [])
        {
            if (client.Audiences.Count > 0 && !client.Audiences.Contains(a, StringComparer.Ordinal))
                throw new InvalidOperationException($"Resource '{a}' is not registered for this client");
            targetAudiences.Add(a);
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
            var hasConsent = consentGrant is not null
                && consentGrant.Type == AgentConsent.GrantType
                && consentGrant.ExpiresAt > DateTimeOffset.UtcNow
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
            effective = agentProfile.Ceiling
                .Intersect(floor)
                .Intersect(requestedAuthority)
                .Intersect(subjectAuthority);
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
                if (denied.Count > 0)
                    throw new ProtocolTokenException("invalid_target",
                        $"requested authority is not grantable: {string.Join(", ", denied)}");
            }

            if (effective.Grants.Count == 0)
                throw new ProtocolTokenException("invalid_target",
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
                    clientId, sub, grantedScopes, targetAudiences, AuthorityJson.Serialize(effective));
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
                        clientId, sub, effective, askActions, requestHash, ct);
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

            effective = subjectAuthority.Intersect(requestedAuthority);
            if (effective.Grants.Count == 0)
                throw new ProtocolTokenException("invalid_target",
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
        var effectiveJson = effective is null ? null : AuthorityJson.Serialize(effective);

        if (agentProfile is not null)
        {
            await Hooks.RunOnTokenIssuingAsync(new TokenIssuanceContext(
                clientId, sub, GrantTypes.TokenExchange, grantedScopes, authorizationDetailsJson), ct);
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
    private static string ComputeRequestHash(
        string clientId, string subjectId, IEnumerable<string> scopes,
        IEnumerable<string> audiences, string effectiveAuthorityJson)
    {
        var canonical = string.Join('\n',
            clientId,
            subjectId,
            string.Join(' ', scopes.Order(StringComparer.Ordinal)),
            string.Join(' ', audiences.Order(StringComparer.Ordinal)),
            effectiveAuthorityJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// How many distinct approvals one agent may leave awaiting a single user's decision. A human queue is
    /// a scarce resource; without a cap an agent could bury a genuine request under noise.
    /// </summary>
    private const int MaxOutstandingApprovalsPerAgent = 20;

    private async Task<string> CreatePendingApprovalAsync(
        string clientId, string subjectId, AuthoritySet slice,
        IReadOnlyList<string> pendingActions, string requestHash, CancellationToken ct)
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
                var now = DateTimeOffset.UtcNow;
                if (data.LastPolledAt is { } lastPolled &&
                    now - lastPolled < TimeSpan.FromSeconds(Approval.PollIntervalSeconds))
                    throw new ProtocolTokenException("slow_down",
                        "Polling too frequently. Increase your interval and try again.");
                // Best-effort poll marker, mirroring the device flow — a lost update just
                // means one un-throttled poll.
                data.LastPolledAt = now;
                grant.Key = key;
                grant.Data = Approval.Serialize(data);
                await grantStore.StoreAsync(grant, ct);
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

        var accessToken = await CreateAccessTokenAsync(subject, client, scopeList, ct: ct);

        string? refreshToken = null;
        if (scopeList.Contains(StandardScopes.OfflineAccess) && client.AllowOfflineAccess)
        {
            refreshToken = await CreateRefreshTokenAsync(subject, client, scopeList, ct: ct);
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
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            IdToken = idToken,
            ExpiresIn = client.AccessTokenLifetimeSeconds,
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

        await grantStore.RemoveAsync(token, ct);

        logger.LogInformation("Refresh token revoked for client {ClientId}", clientId);
        return true;
    }

    private static string GenerateRefreshTokenHandle()
    {
        var bytes = RandomNumberGenerator.GetBytes(RefreshTokenSizeBytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
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

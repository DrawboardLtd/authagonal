using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Services;

public sealed class TokenService(
    IGrantStore grantStore,
    IClientStore clientStore,
    IUserStore userStore,
    IScimGroupStore scimGroupStore,
    IKeyManager keyManager,
    ITenantContext tenantContext,
    IOptions<AuthOptions> authOptions,
    ILogger<TokenService> logger) : ITokenService
{
    private const int RefreshTokenSizeBytes = 64;
    private TimeSpan RefreshTokenReuseGraceWindow => TimeSpan.FromSeconds(authOptions.Value.RefreshTokenReuseGraceSeconds);

    private string Issuer => tenantContext.Issuer;

    public async Task<string> CreateAccessTokenAsync(
        AuthUser? user,
        OAuthClient client,
        IEnumerable<string> scopes,
        IDictionary<string, string>? additionalClaims = null,
        IEnumerable<string>? resources = null,
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

        if (user is not null)
        {
            claims["sub"] = user.Id;

            if (user.Roles.Count > 0)
                claims["roles"] = user.Roles.ToArray();

            if (client.IncludeGroupsInTokens)
            {
                var groups = await scimGroupStore.GetGroupsByUserIdAsync(user.Id, ct);
                if (groups.Count > 0)
                    claims["groups"] = groups.Select(g => g.DisplayName).ToArray();
            }
        }

        if (additionalClaims is not null)
        {
            foreach (var (key, value) in additionalClaims)
            {
                claims[key] = value;
            }
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            IssuedAt = now.UtcDateTime,
            Expires = now.AddSeconds(client.AccessTokenLifetimeSeconds).UtcDateTime,
            SigningCredentials = keyManager.GetSigningCredentials(),
            Claims = claims
        };

        // RFC 8707: if the caller specified resources for this token, narrow aud to that subset
        // (already validated upstream as a subset of client.Audiences). Otherwise fall back to
        // all configured audiences, or the client_id if none are configured.
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
        AuthUser user,
        OAuthClient client,
        IEnumerable<string> scopes,
        string? nonce = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var scopeList = scopes.ToList();

        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.Id,
            ["iat"] = now.ToUnixTimeSeconds()
        };

        // Add nonce if provided (required for implicit/hybrid flows, optional for code flow)
        if (!string.IsNullOrEmpty(nonce))
        {
            claims["nonce"] = nonce;
        }

        // Include profile claims when profile or openid scope is requested
        if (scopeList.Contains(StandardScopes.Email) || scopeList.Contains(StandardScopes.OpenId))
        {
            if (!string.IsNullOrEmpty(user.Email))
                claims["email"] = user.Email;

            claims["email_verified"] = user.EmailConfirmed;
        }

        if (scopeList.Contains(StandardScopes.Profile) || scopeList.Contains(StandardScopes.OpenId))
        {
            if (!string.IsNullOrEmpty(user.FirstName))
                claims["given_name"] = user.FirstName;

            if (!string.IsNullOrEmpty(user.LastName))
                claims["family_name"] = user.LastName;

            var fullName = BuildFullName(user.FirstName, user.LastName);
            if (!string.IsNullOrEmpty(fullName))
                claims["name"] = fullName;

            if (!string.IsNullOrEmpty(user.Phone))
                claims["phone_number"] = user.Phone;
        }

        if (!string.IsNullOrEmpty(user.OrganizationId))
        {
            claims["org_id"] = user.OrganizationId;
        }

        if (user.Roles.Count > 0)
            claims["roles"] = user.Roles.ToArray();

        if (client.IncludeGroupsInTokens)
        {
            var groups = await scimGroupStore.GetGroupsByUserIdAsync(user.Id, ct);
            if (groups.Count > 0)
                claims["groups"] = groups.Select(g => g.DisplayName).ToArray();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = client.ClientId,
            IssuedAt = now.UtcDateTime,
            Expires = now.AddSeconds(client.IdentityTokenLifetimeSeconds).UtcDateTime,
            SigningCredentials = keyManager.GetSigningCredentials(),
            Claims = claims
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    public async Task<string> CreateRefreshTokenAsync(
        AuthUser user,
        OAuthClient client,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources = null,
        DateTimeOffset? originalCreatedAt = null,
        DateTimeOffset? sessionMaxExpiresAt = null,
        CancellationToken ct = default)
    {
        var handle = GenerateRefreshTokenHandle();
        var now = DateTimeOffset.UtcNow;
        var scopeList = scopes.ToList();

        // Absolute mode: hard cap measured from the original issuance; rotation preserves
        // the cap so long-lived clients can't refresh forever.
        // Sliding mode: window extends by Sliding on each rotation, but never past the
        // absolute cap from original issuance.
        var origin = originalCreatedAt ?? now;
        var absoluteCap = origin.AddSeconds(client.AbsoluteRefreshTokenLifetimeSeconds);
        DateTimeOffset expiresAt = client.RefreshTokenExpiration switch
        {
            RefreshTokenExpiration.Sliding =>
                new[] { now.AddSeconds(client.SlidingRefreshTokenLifetimeSeconds), absoluteCap }.Min(),
            _ => absoluteCap,
        };

        // Clamp by upstream session cap when present so tokens cannot outlive the
        // federated session (e.g. an SSO IdP that caps subject sessions).
        if (sessionMaxExpiresAt is { } sessionCap && sessionCap < expiresAt)
            expiresAt = sessionCap;

        var grant = new PersistedGrant
        {
            Key = handle,
            Type = "refresh_token",
            SubjectId = user.Id,
            ClientId = client.ClientId,
            Data = JsonSerializer.Serialize(new RefreshTokenData
            {
                Scopes = scopeList,
                Resources = resources?.ToList(),
                SubjectId = user.Id,
                ClientId = client.ClientId,
                CreatedAt = now,
                OriginalCreatedAt = origin,
                SessionMaxExpiresAt = sessionMaxExpiresAt,
            }, AuthagonalJsonContext.Default.RefreshTokenData),
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
        // Look up the authorization code grant
        var grant = await grantStore.GetAsync(code, ct);
        if (grant is null || grant.Type != "authorization_code")
            throw new InvalidOperationException("Invalid authorization code");

        // Delete the code immediately (single-use)
        await grantStore.RemoveAsync(code, ct);

        if (grant.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Authorization code has expired");

        var authCode = JsonSerializer.Deserialize(grant.Data, AuthagonalJsonContext.Default.AuthorizationCode)
            ?? throw new InvalidOperationException("Failed to deserialize authorization code");

        // Validate client
        if (!string.Equals(authCode.ClientId, clientId, StringComparison.Ordinal))
            throw new InvalidOperationException("Client ID mismatch");

        // Validate redirect URI
        if (!string.Equals(authCode.RedirectUri, redirectUri, StringComparison.Ordinal))
            throw new InvalidOperationException("Redirect URI mismatch");

        var client = await clientStore.GetAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client '{clientId}' not found");

        // Validate PKCE
        if (client.RequirePkce && string.IsNullOrEmpty(authCode.CodeChallenge))
            throw new InvalidOperationException("PKCE is required for this client but no code_challenge was present");

        if (!string.IsNullOrEmpty(authCode.CodeChallenge))
        {
            var method = authCode.CodeChallengeMethod ?? "plain";
            if (!PkceValidator.ValidateCodeVerifier(codeVerifier, authCode.CodeChallenge, method))
                throw new InvalidOperationException("PKCE validation failed");
        }

        var user = await userStore.GetAsync(authCode.SubjectId, ct)
            ?? throw new InvalidOperationException($"User '{authCode.SubjectId}' not found");

        // Issue tokens — propagate any resource indicators captured at authorization time
        var accessToken = await CreateAccessTokenAsync(user, client, authCode.Scopes, resources: authCode.Resources, ct: ct);

        string? idToken = null;
        if (authCode.Scopes.Contains(StandardScopes.OpenId))
        {
            idToken = await CreateIdTokenAsync(user, client, authCode.Scopes, authCode.Nonce, ct);
        }

        string? refreshToken = null;
        if (authCode.Scopes.Contains(StandardScopes.OfflineAccess) && client.AllowOfflineAccess)
        {
            refreshToken = await CreateRefreshTokenAsync(
                user, client, authCode.Scopes, authCode.Resources,
                sessionMaxExpiresAt: authCode.SessionMaxExpiresAt, ct: ct);
        }

        logger.LogInformation(
            "Authorization code exchanged for tokens. Client: {ClientId}, Subject: {SubjectId}",
            clientId, authCode.SubjectId);

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

        // Validate client
        if (!string.Equals(grant.ClientId, clientId, StringComparison.Ordinal))
            throw new InvalidOperationException("Client ID mismatch for refresh token");

        var now = DateTimeOffset.UtcNow;

        // Check expiration
        if (grant.ExpiresAt <= now)
            throw new InvalidOperationException("Refresh token has expired");

        var data = JsonSerializer.Deserialize(grant.Data, AuthagonalJsonContext.Default.RefreshTokenData)
            ?? throw new InvalidOperationException("Failed to deserialize refresh token data");

        // Consumed-token handling:
        //   * grace window disabled (default) → any reuse of a consumed token is treated
        //     as replay and revokes the family (safest posture)
        //   * grace window enabled → a reuse within N seconds of consumption, when the
        //     successor still exists and is unconsumed, is treated as an idempotent retry
        //     and we re-issue fresh access/id tokens pointing at the same successor handle.
        //     Anything outside the window, a missing/consumed successor, or a replay after
        //     successful rotation still triggers the revoke-all policy.
        if (grant.ConsumedAt.HasValue)
        {
            var graceWindow = RefreshTokenReuseGraceWindow;
            if (graceWindow > TimeSpan.Zero &&
                !string.IsNullOrEmpty(data.SuccessorKey) &&
                now - grant.ConsumedAt.Value <= graceWindow)
            {
                var successor = await grantStore.GetAsync(data.SuccessorKey, ct);
                if (successor is not null &&
                    successor.Type == "refresh_token" &&
                    !successor.ConsumedAt.HasValue &&
                    successor.ExpiresAt > now)
                {
                    return await ReissueFromSuccessorAsync(successor, resources, ct);
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

        var user = await userStore.GetAsync(data.SubjectId, ct)
            ?? throw new InvalidOperationException($"User '{data.SubjectId}' not found");

        if (!user.IsActive)
            throw new InvalidOperationException("User account has been deactivated");

        // RFC 8707 §2.2: refresh can request a narrower audience than the original grant.
        // If the client specifies resources on the refresh call, they must be a subset of what
        // was recorded on the original grant (or if none were recorded, a subset of client.Audiences).
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

        // Rotation order: issue the successor first, then atomically mark the old grant as
        // consumed and record the successor handle via a single upsert. If the client retries
        // with the original handle inside the grace window, the consumed-branch above can then
        // locate the successor and replay the exact same successor to them.
        // Pass OriginalCreatedAt so the absolute cap stays anchored to first issuance and
        // SessionMaxExpiresAt so any upstream federation cap survives rotations.
        var originalCreatedAt = data.OriginalCreatedAt ?? data.CreatedAt;
        var newRefreshToken = await CreateRefreshTokenAsync(
            user, client, data.Scopes, data.Resources, originalCreatedAt, data.SessionMaxExpiresAt, ct);

        data.SuccessorKey = newRefreshToken;
        grant.ConsumedAt = now;
        grant.Data = JsonSerializer.Serialize(data, AuthagonalJsonContext.Default.RefreshTokenData);
        await grantStore.StoreAsync(grant, ct);

        // Issue new access token (narrowed to requested resources, if any)
        var accessToken = await CreateAccessTokenAsync(user, client, data.Scopes, resources: tokenResources, ct: ct);

        // Issue new ID token if openid scope
        string? idToken = null;
        if (data.Scopes.Contains(StandardScopes.OpenId))
        {
            idToken = await CreateIdTokenAsync(user, client, data.Scopes, ct: ct);
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

    /// <summary>
    /// Idempotent re-delivery for a retry arriving inside the grace window: mint a fresh
    /// access token (and id token, if the original grant was OIDC) anchored to the successor
    /// grant, but keep the successor handle and its expiry unchanged. No second rotation.
    /// </summary>
    private async Task<TokenResponse> ReissueFromSuccessorAsync(
        PersistedGrant successor,
        IEnumerable<string>? resources,
        CancellationToken ct)
    {
        var data = JsonSerializer.Deserialize(successor.Data, AuthagonalJsonContext.Default.RefreshTokenData)
            ?? throw new InvalidOperationException("Failed to deserialize successor refresh token data");

        var client = await clientStore.GetAsync(successor.ClientId, ct)
            ?? throw new InvalidOperationException($"Client '{successor.ClientId}' not found");

        var user = await userStore.GetAsync(data.SubjectId, ct)
            ?? throw new InvalidOperationException($"User '{data.SubjectId}' not found");

        if (!user.IsActive)
            throw new InvalidOperationException("User account has been deactivated");

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

        var accessToken = await CreateAccessTokenAsync(user, client, data.Scopes, resources: tokenResources, ct: ct);

        string? idToken = null;
        if (data.Scopes.Contains(StandardScopes.OpenId))
        {
            idToken = await CreateIdTokenAsync(user, client, data.Scopes, ct: ct);
        }

        logger.LogInformation(
            "Refresh token retry served from grace window. Client: {ClientId}, Subject: {SubjectId}",
            successor.ClientId, data.SubjectId);

        return new TokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = client.AccessTokenLifetimeSeconds,
            IdToken = idToken,
            RefreshToken = successor.Key,
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

        // Validate requested scopes against allowed scopes
        foreach (var scope in scopeList)
        {
            if (!client.AllowedScopes.Contains(scope))
                throw new InvalidOperationException($"Scope '{scope}' is not allowed for client '{clientId}'");
        }

        // RFC 8707: validate requested resources against the client's registered audiences.
        var resourceList = resources?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (resourceList is { Count: > 0 })
        {
            foreach (var r in resourceList)
            {
                if (!Uri.TryCreate(r, UriKind.Absolute, out var u) || !string.IsNullOrEmpty(u.Fragment))
                    throw new InvalidOperationException($"Resource '{r}' is not a valid absolute URI");
                if (!client.Audiences.Contains(r, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Resource '{r}' is not registered for this client");
            }
        }

        // Client credentials: access token only (no refresh token, no ID token)
        var accessToken = await CreateAccessTokenAsync(null, client, scopeList, resources: resourceList, ct: ct);

        logger.LogInformation("Client credentials token issued for client {ClientId}", clientId);

        return new TokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = client.AccessTokenLifetimeSeconds,
            Scope = string.Join(' ', scopeList)
        };
    }

    public async Task<TokenResponse> HandleDeviceCodeAsync(
        string subjectId,
        string clientId,
        IReadOnlyList<string> scopes,
        CancellationToken ct = default)
    {
        var client = await clientStore.GetAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client '{clientId}' not found");

        var user = await userStore.GetAsync(subjectId, ct)
            ?? throw new InvalidOperationException($"User '{subjectId}' not found");

        var scopeList = scopes.ToList();
        var accessToken = await CreateAccessTokenAsync(user, client, scopeList, ct: ct);

        string? refreshToken = null;
        if (scopeList.Contains("offline_access") && client.AllowOfflineAccess)
        {
            refreshToken = await CreateRefreshTokenAsync(user, client, scopeList, ct: ct);
        }

        string? idToken = null;
        if (scopeList.Contains("openid"))
        {
            idToken = await CreateIdTokenAsync(user, client, scopeList, ct: ct);
        }

        logger.LogInformation("Device code token issued for user {UserId} via client {ClientId}", subjectId, clientId);

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

    /// <summary>
    /// Internal model for refresh token data serialized in PersistedGrant.Data
    /// </summary>
    internal sealed class RefreshTokenData
    {
        public required List<string> Scopes { get; set; }
        public List<string>? Resources { get; set; }
        public required string SubjectId { get; set; }
        public required string ClientId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        // Tracks first issuance so the absolute refresh lifetime cap survives rotations.
        // Nullable so pre-upgrade grants deserialize cleanly; rotation falls back to CreatedAt.
        public DateTimeOffset? OriginalCreatedAt { get; set; }
        // Handle of the refresh token that superseded this one. Set at rotation time
        // alongside ConsumedAt so a retry arriving within the grace window can locate
        // the successor and idempotently re-issue fresh access/id tokens.
        public string? SuccessorKey { get; set; }
        // Upper bound on token lifetime driven by an upstream federated session
        // (e.g. SSO IdP max session). Preserved across rotations so the cap cannot be
        // lifted by refreshing. Null means no cap.
        public DateTimeOffset? SessionMaxExpiresAt { get; set; }
    }
}

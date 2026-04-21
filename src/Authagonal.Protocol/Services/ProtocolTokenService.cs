using System.Security.Cryptography;
using System.Text.Json;
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
    IOptions<AuthagonalProtocolOptions> protocolOptions,
    ILogger<ProtocolTokenService> logger) : IProtocolTokenService
{
    private const int RefreshTokenSizeBytes = 64;
    private TimeSpan RefreshTokenReuseGraceWindow =>
        TimeSpan.FromSeconds(protocolOptions.Value.RefreshTokenReuseGraceSeconds);

    private string Issuer => tenantContext.Issuer;

    // Protocol-level claims that custom attributes / additional claims must never shadow —
    // even if a scope lists them in UserClaims. Overriding these would let configuration
    // rewrite the OAuth/OIDC contract.
    private static readonly HashSet<string> ReservedClaimNames = new(StringComparer.Ordinal)
    {
        "iss", "sub", "aud", "exp", "nbf", "iat", "jti",
        "scope", "client_id", "nonce", "auth_time", "acr", "amr",
        "roles", "groups", "sid",
    };

    public async Task<string> CreateAccessTokenAsync(
        OidcSubject? subject,
        OAuthClient client,
        IEnumerable<string> scopes,
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

        // Clamp lifetime by session cap if present.
        var expires = now.AddSeconds(client.AccessTokenLifetimeSeconds);
        if (subject?.SessionMaxExpiresAt is { } sessionCap && sessionCap < expires)
            expires = sessionCap;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            IssuedAt = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = keyManager.GetSigningCredentials(),
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

        await grantStore.RemoveAsync(code, ct);

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
            var method = authCode.CodeChallengeMethod ?? "plain";
            if (!PkceValidator.ValidateCodeVerifier(codeVerifier, authCode.CodeChallenge, method))
                throw new InvalidOperationException("PKCE validation failed");
        }

        var subject = authCode.Subject;

        var accessToken = await CreateAccessTokenAsync(subject, client, authCode.Scopes, authCode.Resources, ct);

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
        grant.ConsumedAt = now;
        grant.Data = JsonSerializer.Serialize(data, ProtocolJsonContext.Default.RefreshTokenData);
        await grantStore.StoreAsync(grant, ct);

        var accessToken = await CreateAccessTokenAsync(freshSubject, client, data.Scopes, tokenResources, ct);

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

        var accessToken = await CreateAccessTokenAsync(data.Subject, client, data.Scopes, tokenResources, ct);

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
                if (!client.Audiences.Contains(r, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Resource '{r}' is not registered for this client");
            }
        }

        var accessToken = await CreateAccessTokenAsync(null, client, scopeList, resourceList, ct);

        logger.LogInformation("Client credentials token issued for client {ClientId}", clientId);

        return new TokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = client.AccessTokenLifetimeSeconds,
            Scope = string.Join(' ', scopeList)
        };
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

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
    KeyManager keyManager,
    IConfiguration configuration,
    IOptions<AuthOptions> authOptions,
    ILogger<TokenService> logger) : ITokenService
{
    private const int RefreshTokenSizeBytes = 64;
    private TimeSpan RefreshTokenReuseGraceWindow => TimeSpan.FromSeconds(authOptions.Value.RefreshTokenReuseGraceSeconds);

    private string Issuer => configuration["Oidc:Issuer"]
        ?? throw new InvalidOperationException("Oidc:Issuer is not configured");

    public async Task<string> CreateAccessTokenAsync(
        AuthUser? user,
        OAuthClient client,
        IEnumerable<string> scopes,
        IDictionary<string, string>? additionalClaims = null,
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
            Audience = client.ClientId,
            IssuedAt = now.UtcDateTime,
            Expires = now.AddSeconds(client.AccessTokenLifetimeSeconds).UtcDateTime,
            SigningCredentials = keyManager.GetSigningCredentials(),
            Claims = claims
        };

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
        CancellationToken ct = default)
    {
        var handle = GenerateRefreshTokenHandle();
        var now = DateTimeOffset.UtcNow;
        var scopeList = scopes.ToList();

        var grant = new PersistedGrant
        {
            Key = handle,
            Type = "refresh_token",
            SubjectId = user.Id,
            ClientId = client.ClientId,
            Data = JsonSerializer.Serialize(new RefreshTokenData
            {
                Scopes = scopeList,
                SubjectId = user.Id,
                ClientId = client.ClientId,
                CreatedAt = now
            }),
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(client.AbsoluteRefreshTokenLifetimeSeconds)
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

        var authCode = JsonSerializer.Deserialize<AuthorizationCode>(grant.Data)
            ?? throw new InvalidOperationException("Failed to deserialize authorization code");

        // Validate client
        if (!string.Equals(authCode.ClientId, clientId, StringComparison.Ordinal))
            throw new InvalidOperationException("Client ID mismatch");

        // Validate redirect URI
        if (!string.Equals(authCode.RedirectUri, redirectUri, StringComparison.Ordinal))
            throw new InvalidOperationException("Redirect URI mismatch");

        // Validate PKCE
        if (!string.IsNullOrEmpty(authCode.CodeChallenge))
        {
            var method = authCode.CodeChallengeMethod ?? "plain";
            if (!PkceValidator.ValidateCodeVerifier(codeVerifier, authCode.CodeChallenge, method))
                throw new InvalidOperationException("PKCE validation failed");
        }

        var client = await clientStore.GetAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client '{clientId}' not found");

        var user = await userStore.GetAsync(authCode.SubjectId, ct)
            ?? throw new InvalidOperationException($"User '{authCode.SubjectId}' not found");

        // Issue tokens
        var accessToken = await CreateAccessTokenAsync(user, client, authCode.Scopes, ct: ct);

        string? idToken = null;
        if (authCode.Scopes.Contains(StandardScopes.OpenId))
        {
            idToken = await CreateIdTokenAsync(user, client, authCode.Scopes, authCode.Nonce, ct);
        }

        string? refreshToken = null;
        if (authCode.Scopes.Contains(StandardScopes.OfflineAccess) && client.AllowOfflineAccess)
        {
            refreshToken = await CreateRefreshTokenAsync(user, client, authCode.Scopes, ct);
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

        // Check if token has been consumed (one-time rotation)
        if (grant.ConsumedAt.HasValue)
        {
            // Token replay attack — consumed token is being reused.
            // Even within a grace window, issuing new tokens is unsafe (both attacker
            // and client get valid tokens). Revoke everything for this subject+client.
            logger.LogError(
                "Refresh token replay detected! Revoking all tokens for subject. Client: {ClientId}, Subject: {SubjectId}",
                clientId, grant.SubjectId);

            if (grant.SubjectId is not null)
            {
                await grantStore.RemoveAllBySubjectAndClientAsync(grant.SubjectId, clientId, ct);
            }

            throw new InvalidOperationException("Refresh token has been revoked (replay detected)");
        }

        var data = JsonSerializer.Deserialize<RefreshTokenData>(grant.Data)
            ?? throw new InvalidOperationException("Failed to deserialize refresh token data");

        var client = await clientStore.GetAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client '{clientId}' not found");

        var user = await userStore.GetAsync(data.SubjectId, ct)
            ?? throw new InvalidOperationException($"User '{data.SubjectId}' not found");

        if (!user.IsActive)
            throw new InvalidOperationException("User account has been deactivated");

        // Consume the old token (mark as used)
        if (!grant.ConsumedAt.HasValue)
        {
            await grantStore.ConsumeAsync(refreshToken, ct);
        }

        // Issue new refresh token (one-time rotation)
        var newRefreshToken = await CreateRefreshTokenAsync(user, client, data.Scopes, ct);

        // Issue new access token
        var accessToken = await CreateAccessTokenAsync(user, client, data.Scopes, ct: ct);

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

    public async Task<TokenResponse> HandleClientCredentialsAsync(
        string clientId,
        IEnumerable<string> scopes,
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

        // Client credentials: access token only (no refresh token, no ID token)
        var accessToken = await CreateAccessTokenAsync(null, client, scopeList, ct: ct);

        logger.LogInformation("Client credentials token issued for client {ClientId}", clientId);

        return new TokenResponse
        {
            AccessToken = accessToken,
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
    private sealed class RefreshTokenData
    {
        public required List<string> Scopes { get; set; }
        public required string SubjectId { get; set; }
        public required string ClientId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

public sealed class AuthorizationCodeService(
    IGrantStore grantStore,
    IClientStore clientStore,
    ILogger<AuthorizationCodeService> logger)
{
    private const int CodeSizeBytes = 32;

    /// <summary>
    /// Creates an authorization code, persists it as a grant, and returns the base64url-encoded code.
    /// </summary>
    public async Task<string> CreateCodeAsync(
        string clientId,
        string subjectId,
        string redirectUri,
        IEnumerable<string> scopes,
        string? codeChallenge,
        string? codeChallengeMethod,
        string? nonce,
        IEnumerable<string>? resources = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        var client = await clientStore.GetAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client '{clientId}' not found");

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(client.AuthorizationCodeLifetimeSeconds);

        var codeBytes = RandomNumberGenerator.GetBytes(CodeSizeBytes);
        var code = Base64UrlEncode(codeBytes);

        var authCode = new AuthorizationCode
        {
            Code = code,
            ClientId = clientId,
            SubjectId = subjectId,
            RedirectUri = redirectUri,
            Scopes = scopes.ToList(),
            Resources = resources?.ToList(),
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = codeChallengeMethod,
            Nonce = nonce,
            CreatedAt = now,
            ExpiresAt = expiresAt
        };

        var grant = new PersistedGrant
        {
            Key = code,
            Type = "authorization_code",
            SubjectId = subjectId,
            ClientId = clientId,
            Data = JsonSerializer.Serialize(authCode, AuthagonalJsonContext.Default.AuthorizationCode),
            CreatedAt = now,
            ExpiresAt = expiresAt
        };

        await grantStore.StoreAsync(grant, ct);

        logger.LogInformation(
            "Authorization code created for client {ClientId}, subject {SubjectId}, expires at {ExpiresAt}",
            clientId, subjectId, expiresAt);

        return code;
    }

    /// <summary>
    /// Looks up an authorization code grant, deletes it (single-use), and returns the deserialized AuthorizationCode.
    /// Returns null if not found or expired.
    /// </summary>
    public async Task<AuthorizationCode?> GetAndRemoveCodeAsync(string code, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var grant = await grantStore.GetAsync(code, ct);

        if (grant is null || grant.Type != "authorization_code")
        {
            logger.LogWarning("Authorization code not found or wrong type: {Code}", code);
            return null;
        }

        // Delete the grant immediately (single-use)
        await grantStore.RemoveAsync(code, ct);

        if (grant.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            logger.LogWarning("Authorization code expired for client {ClientId}", grant.ClientId);
            return null;
        }

        var authCode = JsonSerializer.Deserialize(grant.Data, AuthagonalJsonContext.Default.AuthorizationCode);

        if (authCode is null)
        {
            logger.LogError("Failed to deserialize authorization code data for key {Key}", code);
            return null;
        }

        return authCode;
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

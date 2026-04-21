using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Authagonal.Protocol.Services;

public sealed class ProtocolAuthorizationCodeService(
    IGrantStore grantStore,
    IClientStore clientStore,
    ILogger<ProtocolAuthorizationCodeService> logger)
{
    private const int CodeSizeBytes = 32;

    public async Task<string> CreateCodeAsync(
        string clientId,
        OidcSubject subject,
        string redirectUri,
        IEnumerable<string> scopes,
        string? codeChallenge,
        string? codeChallengeMethod,
        string? nonce,
        IEnumerable<string>? resources = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentNullException.ThrowIfNull(subject);

        var client = await clientStore.GetAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client '{clientId}' not found");

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(client.AuthorizationCodeLifetimeSeconds);

        var code = Base64UrlEncode(RandomNumberGenerator.GetBytes(CodeSizeBytes));

        var authCode = new ProtocolAuthorizationCode
        {
            Code = code,
            ClientId = clientId,
            SubjectId = subject.SubjectId,
            RedirectUri = redirectUri,
            Scopes = scopes.ToList(),
            Resources = resources?.ToList(),
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = codeChallengeMethod,
            Nonce = nonce,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            SessionMaxExpiresAt = subject.SessionMaxExpiresAt,
            Subject = subject
        };

        var grant = new PersistedGrant
        {
            Key = code,
            Type = "authorization_code",
            SubjectId = subject.SubjectId,
            ClientId = clientId,
            Data = JsonSerializer.Serialize(authCode, ProtocolJsonContext.Default.ProtocolAuthorizationCode),
            CreatedAt = now,
            ExpiresAt = expiresAt
        };

        await grantStore.StoreAsync(grant, ct);

        logger.LogInformation(
            "Authorization code created for client {ClientId}, subject {SubjectId}, expires at {ExpiresAt}",
            clientId, subject.SubjectId, expiresAt);

        return code;
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

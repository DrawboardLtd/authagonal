using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Authagonal.Protocol.Services;

/// <summary>
/// Implements the server side of RFC 9126 (Pushed Authorization Requests).
/// Persists the full authorize-request payload under an opaque one-shot request_uri and
/// reloads it at the /authorize step so the browser never carries the parameters itself.
/// </summary>
public sealed class ProtocolPushedAuthorizationService(
    IGrantStore grantStore,
    ILogger<ProtocolPushedAuthorizationService> logger)
{
    // Per RFC 9126 §4 the lifetime is server-chosen; 90s matches the reference IdPs in the
    // wild and is tight enough to contain replay without tripping up slow redirects.
    public const int RequestUriLifetimeSeconds = 90;
    public const string RequestUriPrefix = "urn:ietf:params:oauth:request_uri:";
    public const string GrantType = "pushed_authorization_request";

    private const int KeySizeBytes = 32;

    public async Task<PushedAuthorizationResponse> StoreAsync(
        string clientId,
        Dictionary<string, string[]> parameters,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(parameters);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(RequestUriLifetimeSeconds);
        var requestUri = RequestUriPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(KeySizeBytes));

        var record = new PushedAuthorizationRequest
        {
            RequestUri = requestUri,
            ClientId = clientId,
            Parameters = parameters,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

        var grant = new PersistedGrant
        {
            Key = requestUri,
            Type = GrantType,
            SubjectId = clientId,
            ClientId = clientId,
            Data = JsonSerializer.Serialize(record, ProtocolJsonContext.Default.PushedAuthorizationRequest),
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

        await grantStore.StoreAsync(grant, ct);

        logger.LogInformation(
            "Pushed authorization request stored for client {ClientId}, expires at {ExpiresAt}",
            clientId, expiresAt);

        return new PushedAuthorizationResponse
        {
            RequestUri = requestUri,
            ExpiresIn = RequestUriLifetimeSeconds,
        };
    }

    /// <summary>
    /// Loads a pushed request without consuming it. Returns null if unknown, expired, or bound
    /// to a different client — the caller should translate that into invalid_request rather
    /// than leaking which condition failed. Callers MUST invoke <see cref="RemoveAsync"/> once
    /// the authorization code has been issued, so consumption only happens on success (this
    /// lets the user round-trip through login without burning the request_uri).
    /// </summary>
    public async Task<PushedAuthorizationRequest?> LoadAsync(
        string requestUri,
        string clientId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestUri) || !requestUri.StartsWith(RequestUriPrefix, StringComparison.Ordinal))
            return null;

        var grant = await grantStore.GetAsync(requestUri, ct);
        if (grant is null || grant.Type != GrantType)
            return null;

        if (grant.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await grantStore.RemoveAsync(requestUri, ct);
            return null;
        }

        if (!string.Equals(grant.ClientId, clientId, StringComparison.Ordinal))
            return null;

        try
        {
            return JsonSerializer.Deserialize(grant.Data, ProtocolJsonContext.Default.PushedAuthorizationRequest);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Malformed pushed authorization request payload for {RequestUri}", requestUri);
            await grantStore.RemoveAsync(requestUri, ct);
            return null;
        }
    }

    public Task RemoveAsync(string requestUri, CancellationToken ct = default) =>
        grantStore.RemoveAsync(requestUri, ct);

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

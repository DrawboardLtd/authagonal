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
    //
    // This bounds the push → FIRST /connect/authorize hop only. It cannot bound the whole flow: the record
    // is deliberately not consumed until the authorization code is issued, so that the user can round-trip
    // through login (see LoadAsync), and both hosts keep request_uri on the returnUrl they hand to the login
    // app. Everything the user has to do sits between those two points — load the SPA, enter credentials,
    // clear MFA (a TOTP code, or one emailed to them), possibly a step-up that signs the session out and
    // starts again, then read and answer the consent screen. Ninety seconds from the POST for all of that
    // meant an interactive PAR flow broke whenever a human took as long as a human takes.
    public const int RequestUriLifetimeSeconds = 90;

    /// <summary>
    /// Total life of the record, measured from the push, once it has actually been picked up.
    /// </summary>
    /// <remarks>
    /// Extended ONCE, on the first successful load, to an absolute deadline computed from
    /// <see cref="PushedAuthorizationRequest.CreatedAt"/> — not slid forward on each load. So the extension
    /// is idempotent, repeated loads cannot keep the row alive indefinitely, and the whole flow is still
    /// bounded from the moment the client pushed it. Matches the interactive window an authorization code
    /// gets, because it covers the same user journey.
    /// </remarks>
    public const int InteractiveLifetimeSeconds = 15 * 60;
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
            var record = JsonSerializer.Deserialize(grant.Data, ProtocolJsonContext.Default.PushedAuthorizationRequest);
            if (record is null)
                return null;

            // The record has been picked up, so it now has to survive the interactive leg. Extended once, to
            // an absolute deadline from the push — see InteractiveLifetimeSeconds. A read that writes, but
            // only on the first load of each record: after this the condition is false.
            var deadline = record.CreatedAt.AddSeconds(InteractiveLifetimeSeconds);
            if (grant.ExpiresAt < deadline)
            {
                record.ExpiresAt = deadline;
                grant.ExpiresAt = deadline;
                grant.Data = JsonSerializer.Serialize(record, ProtocolJsonContext.Default.PushedAuthorizationRequest);

                // Re-set explicitly: a grant read back from storage carries NO Key — the handle is hashed
                // into the partition and not recoverable — so re-storing the fetched object as-is writes into
                // the SHA-256("") partition on the real stores. IGrantStore says so, and the in-memory double
                // throws to make it impossible to miss. Which it did, on the first run of this.
                grant.Key = requestUri;
                await grantStore.StoreAsync(grant, ct);

                logger.LogDebug(
                    "Pushed authorization request {RequestUri} extended to {ExpiresAt} for its interactive leg",
                    requestUri, deadline);
            }

            return record;
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

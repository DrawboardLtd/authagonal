namespace Authagonal.Core.Services;

/// <summary>
/// Replay protection for SAML: stores in-flight AuthnRequest IDs (for round-trip validation) and
/// seen assertion IDs (to reject replays). Backend-pluggable — the Azure provider implements it over
/// Table Storage, the AWS provider over DynamoDB.
/// </summary>
/// <summary>State stored against an in-flight SAML AuthnRequest ID.</summary>
public sealed record SamlRequestState(string ConnectionId, string? ReturnUrl);

public interface ISamlReplayCache
{
    /// <summary>Store an outbound AuthnRequest ID against its connection for later validation.</summary>
    Task StoreRequestIdAsync(string requestId, string connectionId, CancellationToken ct = default);

    /// <summary>
    /// Validate a previously-stored request ID and consume it (single-use). Returns the connection id
    /// if valid and unexpired, otherwise null.
    /// </summary>
    Task<string?> ValidateAndConsumeAsync(string requestId, CancellationToken ct = default);

    /// <summary>
    /// Store an outbound AuthnRequest ID with the post-login return URL. The return URL rides the
    /// server-side request row instead of RelayState (the SAML spec caps RelayState at 80 bytes and
    /// some IdPs truncate it — a full /authorize returnUrl does not fit). Default implementation
    /// drops the return URL for back-compat; providers override to persist it.
    /// </summary>
    Task StoreRequestAsync(string requestId, string connectionId, string? returnUrl, CancellationToken ct = default)
        => StoreRequestIdAsync(requestId, connectionId, ct);

    /// <summary>
    /// Validate and consume a request ID (single-use), returning its stored state — connection id and
    /// return URL — or null if unknown/expired. Default implementation adapts the legacy method
    /// (ReturnUrl always null).
    /// </summary>
    async Task<SamlRequestState?> ValidateAndConsumeRequestAsync(string requestId, CancellationToken ct = default)
    {
        var connectionId = await ValidateAndConsumeAsync(requestId, ct).ConfigureAwait(false);
        return connectionId is null ? null : new SamlRequestState(connectionId, null);
    }

    /// <summary>
    /// Record an assertion ID the first time it's seen. Returns true if new (not a replay), false if it
    /// was already seen.
    /// </summary>
    Task<bool> CheckAndStoreAssertionIdAsync(string assertionId, CancellationToken ct = default);
}

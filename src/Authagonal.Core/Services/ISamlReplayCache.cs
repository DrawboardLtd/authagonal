namespace Authagonal.Core.Services;

/// <summary>
/// Replay protection for SAML: stores in-flight AuthnRequest IDs (for round-trip validation) and
/// seen assertion IDs (to reject replays). Backend-pluggable — the Azure provider implements it over
/// Table Storage, the AWS provider over DynamoDB.
/// </summary>
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
    /// Record an assertion ID the first time it's seen. Returns true if new (not a replay), false if it
    /// was already seen.
    /// </summary>
    Task<bool> CheckAndStoreAssertionIdAsync(string assertionId, CancellationToken ct = default);
}

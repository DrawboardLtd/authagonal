namespace Authagonal.Core.Services;

/// <summary>State carried across an upstream-OIDC federation round-trip, keyed by the state parameter.</summary>
public sealed record OidcStateData(
    string ConnectionId,
    string ReturnUrl,
    string CodeVerifier,
    string Nonce);

/// <summary>
/// Single-use store for OIDC federation state (the upstream authorize→callback round-trip).
/// Backend-pluggable — Azure provider over Table Storage, AWS provider over DynamoDB.
/// </summary>
public interface IOidcStateStore
{
    /// <summary>Persist state for an outbound authorize request.</summary>
    Task StoreAsync(string state, string connectionId, string returnUrl, string codeVerifier, string nonce, CancellationToken ct = default);

    /// <summary>Read and consume (delete) the state. Returns null if missing or expired.</summary>
    Task<OidcStateData?> ConsumeAsync(string state, CancellationToken ct = default);
}

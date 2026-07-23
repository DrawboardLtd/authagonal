namespace Authagonal.Bff;

/// <summary>The result of a token or refresh exchange.</summary>
public sealed record TokenResult(string AccessToken, string? RefreshToken, string? IdToken, int ExpiresIn);

/// <summary>Talks to the Authagonal tenant's token and revocation endpoints. A hosted-seam extension
/// point (shared, but replaceable).</summary>
public interface ITokenClient
{
    /// <summary>Exchange an authorization code (with PKCE verifier) for tokens, against the given tenant's
    /// token endpoint and confidential client.</summary>
    Task<TokenResult> ExchangeCodeAsync(BffTenantConfig tenant, string code, string redirectUri, string codeVerifier, CancellationToken ct = default);

    /// <summary>Exchange a refresh token for a fresh set (rotation-aware: the result may carry a new
    /// refresh token), against the given tenant.</summary>
    Task<TokenResult> RefreshAsync(BffTenantConfig tenant, string refreshToken, CancellationToken ct = default);

    /// <summary>Best-effort revoke a refresh token at the given tenant's revocation endpoint.</summary>
    Task RevokeAsync(BffTenantConfig tenant, string refreshToken, CancellationToken ct = default);

    /// <summary>RFC 8693 token exchange: present the session's access token as <c>subject_token</c>
    /// and receive a downscoped access token. <paramref name="extraParameters"/> are host extension
    /// params (e.g. <c>project_id</c>) validated server-side by the tenant's exchange transformer.
    /// The result never carries a refresh token and never outlives the subject token.</summary>
    Task<TokenResult> ExchangeTokenAsync(
        BffTenantConfig tenant,
        string subjectToken,
        IReadOnlyDictionary<string, string>? extraParameters = null,
        string? scope = null,
        CancellationToken ct = default);
}

/// <summary>Thrown when a token/refresh exchange fails.</summary>
public sealed class BffTokenException(string message) : Exception(message);

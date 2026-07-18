namespace Authagonal.Bff;

/// <summary>The result of a token or refresh exchange.</summary>
public sealed record TokenResult(string AccessToken, string? RefreshToken, string? IdToken, int ExpiresIn);

/// <summary>Talks to the Authagonal tenant's token and revocation endpoints. A hosted-seam extension
/// point (shared, but replaceable).</summary>
public interface ITokenClient
{
    /// <summary>Exchange an authorization code (with PKCE verifier) for tokens.</summary>
    Task<TokenResult> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct = default);

    /// <summary>Exchange a refresh token for a fresh set (rotation-aware: the result may carry a new
    /// refresh token).</summary>
    Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Best-effort revoke a refresh token at the provider's revocation endpoint.</summary>
    Task RevokeAsync(string refreshToken, CancellationToken ct = default);
}

/// <summary>Thrown when a token/refresh exchange fails.</summary>
public sealed class BffTokenException(string message) : Exception(message);

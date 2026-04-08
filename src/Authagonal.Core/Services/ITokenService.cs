using Authagonal.Core.Models;

namespace Authagonal.Core.Services;

public interface ITokenService
{
    Task<string> CreateAccessTokenAsync(AuthUser? user, OAuthClient client, IEnumerable<string> scopes, IDictionary<string, string>? additionalClaims = null, CancellationToken ct = default);
    Task<string> CreateIdTokenAsync(AuthUser user, OAuthClient client, IEnumerable<string> scopes, string? nonce = null, CancellationToken ct = default);
    Task<string> CreateRefreshTokenAsync(AuthUser user, OAuthClient client, IEnumerable<string> scopes, CancellationToken ct = default);
    Task<TokenResponse> HandleAuthorizationCodeAsync(string code, string clientId, string redirectUri, string codeVerifier, CancellationToken ct = default);
    Task<TokenResponse> HandleRefreshTokenAsync(string refreshToken, string clientId, CancellationToken ct = default);
    Task<TokenResponse> HandleClientCredentialsAsync(string clientId, IEnumerable<string> scopes, CancellationToken ct = default);
    Task<TokenResponse> HandleDeviceCodeAsync(string subjectId, string clientId, IReadOnlyList<string> scopes, CancellationToken ct = default);
    Task<bool> RevokeRefreshTokenAsync(string token, string clientId, CancellationToken ct = default);
}

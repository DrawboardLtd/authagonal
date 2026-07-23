using Authagonal.Core.Models;

namespace Authagonal.Protocol.Services;

/// <summary>
/// Token minting surface for the protocol. Unlike <c>Authagonal.Core.ITokenService</c>,
/// this takes <see cref="OidcSubject"/> directly — there is no coupling to any user-store
/// model. Hosts interact with this via the OIDC endpoints; direct use is internal.
/// </summary>
public interface IProtocolTokenService
{
    Task<string> CreateAccessTokenAsync(
        OidcSubject? subject,
        OAuthClient client,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources = null,
        CancellationToken ct = default);

    Task<string> CreateIdTokenAsync(
        OidcSubject subject,
        OAuthClient client,
        IEnumerable<string> scopes,
        string? nonce = null,
        CancellationToken ct = default);

    Task<string> CreateRefreshTokenAsync(
        OidcSubject subject,
        OAuthClient client,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources = null,
        DateTimeOffset? originalCreatedAt = null,
        CancellationToken ct = default);

    Task<TokenResponse> HandleAuthorizationCodeAsync(
        string code,
        string clientId,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct = default);

    Task<TokenResponse> HandleRefreshTokenAsync(
        string refreshToken,
        string clientId,
        IEnumerable<string>? resources = null,
        CancellationToken ct = default);

    Task<TokenResponse> HandleClientCredentialsAsync(
        string clientId,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources = null,
        CancellationToken ct = default);

    /// <summary>
    /// RFC 8693 token exchange: validates a subject access token this server issued and mints a
    /// downscoped access token from it. Requested scopes must be a subset of the subject token's
    /// scopes (∩ the exchanging client's allowed scopes); when none are requested, that
    /// intersection is granted minus <c>offline_access</c> (no refresh token is ever issued from
    /// an exchange — re-exchange from the primary token instead). The exchanged token's lifetime
    /// never exceeds the subject token's remaining lifetime, and the subject's custom claims are
    /// re-gated by the NEW scope set's UserClaims whitelists. Non-standard form parameters are
    /// forwarded to the registered <see cref="ITokenExchangeSubjectTransformer"/>, the host seam
    /// for validating and minting context-bound claims (e.g. project/workspace tokens).
    /// </summary>
    Task<TokenResponse> HandleTokenExchangeAsync(
        string clientId,
        string subjectToken,
        string subjectTokenType,
        string? requestedTokenType = null,
        IEnumerable<string>? scopes = null,
        IEnumerable<string>? resources = null,
        IEnumerable<string>? audiences = null,
        IReadOnlyDictionary<string, string>? extraParameters = null,
        CancellationToken ct = default);

    /// <summary>
    /// Mints tokens for a device-code grant. The host is responsible for driving the
    /// device flow (code issuance, polling, user approval) and for building the
    /// <see cref="OidcSubject"/> — this call is the terminal mint step.
    /// </summary>
    Task<TokenResponse> HandleDeviceCodeAsync(
        OidcSubject subject,
        OAuthClient client,
        IReadOnlyList<string> scopes,
        CancellationToken ct = default);

    Task<bool> RevokeRefreshTokenAsync(
        string token,
        string clientId,
        CancellationToken ct = default);
}

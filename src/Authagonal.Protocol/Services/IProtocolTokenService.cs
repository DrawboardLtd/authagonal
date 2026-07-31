using Authagonal.Core.Models;

namespace Authagonal.Protocol.Services;

/// <summary>
/// Token minting surface for the protocol. Unlike <c>Authagonal.Core.ITokenService</c>,
/// this takes <see cref="OidcSubject"/> directly — there is no coupling to any user-store
/// model. Hosts interact with this via the OIDC endpoints; direct use is internal.
/// </summary>
public interface IProtocolTokenService
{
    /// <param name="authorizationDetailsJson">RFC 9396 authority to stamp onto the token as
    /// the <c>authorization_details</c> claim (emitted as real JSON, not a string).</param>
    /// <param name="actorJson">RFC 8693 actor chain to stamp as the <c>act</c> claim.</param>
    /// <param name="notAfter">Extra expiry clamp on top of the client lifetime and the
    /// subject's session cap (used for agent MaxTokenLifetimeSeconds).</param>
    Task<string> CreateAccessTokenAsync(
        OidcSubject? subject,
        OAuthClient client,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources = null,
        string? authorizationDetailsJson = null,
        string? actorJson = null,
        DateTimeOffset? notAfter = null,
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

    /// <summary>
    /// Mints an access token and the refresh token that owns it, with the access token's <c>jti</c>
    /// recorded on the refresh grant.
    /// </summary>
    /// <remarks>
    /// Revoking a refresh token revokes the access tokens minted under it, which works by reading
    /// the jti list off the grant. Minting the two separately through
    /// <see cref="CreateAccessTokenAsync"/> and <see cref="CreateRefreshTokenAsync"/> writes a grant
    /// with an EMPTY list, so revoking that refresh token revokes nothing and the access token stays
    /// live for its full lifetime — which is exactly what the admin impersonation endpoint was doing
    /// with the one token pair an operator is most likely to need to pull back. Every caller that
    /// issues a pair should use this rather than pairing the two singles.
    /// </remarks>
    Task<(string AccessToken, string RefreshToken)> CreateTrackedTokenPairAsync(
        OidcSubject subject,
        OAuthClient client,
        IEnumerable<string> scopes,
        IEnumerable<string>? resources = null,
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
    /// <para>
    /// When the exchanging client has an <c>AgentProfile</c>, the exchange is a composite
    /// delegation: the mint requires the subject's standing agent consent, computes
    /// <c>effective = subject authority ∩ ceiling ∩ consent ∩ requested authorization_details</c>,
    /// stamps the RFC 8693 <c>act</c> chain and the RFC 9396 <c>authorization_details</c> claim,
    /// and parks on <see cref="ApprovalPendingException"/> when an ask-policy action is in the
    /// effective slice (<paramref name="approvalId"/> resumes it). Clients without a profile are
    /// untouched, except that <paramref name="authorizationDetailsJson"/> still narrows: a plain
    /// exchange may downscope authority, never widen it.
    /// </para>
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
        string? actorToken = null,
        string? actorTokenType = null,
        string? authorizationDetailsJson = null,
        string? approvalId = null,
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

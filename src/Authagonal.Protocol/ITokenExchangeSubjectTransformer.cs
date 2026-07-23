using Authagonal.Core.Models;

namespace Authagonal.Protocol;

/// <summary>
/// Host extension point for the RFC 8693 token-exchange grant. Invoked after the subject has been
/// rebuilt from the validated subject token and the requested scopes/audiences have been narrowed,
/// but before the downscoped access token is minted.
/// <para>
/// The exchange path deliberately never consults <see cref="IOidcSubjectResolver"/> — an exchange
/// is a projection of an existing session, not a fresh sign-in. This seam exists for hosts that
/// mint <em>context-bound</em> tokens: validate a caller-supplied binding (e.g. a
/// <c>project_id</c> form parameter) against the host's own authority, then force the resulting
/// claims onto the subject via <see cref="OidcSubject.AdditionalClaims"/> (reserved protocol claim
/// names are still blocked at mint). Return
/// <see cref="OidcSubjectResult.Reject(OidcRejection, string?)"/> to refuse the exchange — surfaced
/// to the client as <c>invalid_target</c>.
/// </para>
/// <para>
/// The transformer may SHORTEN the token lifetime by lowering
/// <see cref="OidcSubject.SessionMaxExpiresAt"/>; it can never lengthen it — the service re-clamps
/// to the subject token's expiry after the transformer runs.
/// </para>
/// </summary>
public interface ITokenExchangeSubjectTransformer
{
    /// <param name="subject">The subject rebuilt from the validated subject token (sub, roles,
    /// groups, non-protocol claims as CustomAttributes, SessionMaxExpiresAt = subject exp).</param>
    /// <param name="client">The exchanging client (already grant-checked).</param>
    /// <param name="grantedScopes">The narrowed scope set the exchanged token will carry.</param>
    /// <param name="extraParameters">Non-standard form parameters from the token request —
    /// everything except the RFC 8693 / OAuth protocol fields. Single-valued; first value wins.</param>
    Task<OidcSubjectResult> TransformAsync(
        OidcSubject subject,
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct = default);
}

/// <summary>Default no-op transformer: every exchange passes through unchanged.</summary>
public sealed class NullTokenExchangeSubjectTransformer : ITokenExchangeSubjectTransformer
{
    public Task<OidcSubjectResult> TransformAsync(
        OidcSubject subject,
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct = default)
        => Task.FromResult(OidcSubjectResult.Allow(subject));
}

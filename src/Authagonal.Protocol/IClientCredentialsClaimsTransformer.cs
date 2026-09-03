using Authagonal.Core.Models;

namespace Authagonal.Protocol;

/// <summary>
/// Host extension point for the <c>client_credentials</c> grant — the machine-caller counterpart of
/// <see cref="ITokenExchangeSubjectTransformer"/>. Invoked after the client, its scopes and any
/// RFC 8707 resources have been validated, but before the access token is minted.
/// <para>
/// A client-credentials token has no subject, so the exchange seam cannot reach it: there is no
/// <c>sub</c> to act for, and the exchange path refuses one by design. This seam exists for the
/// first-party service caller whose token has to name the CONTEXT it is acting in — an
/// organization, a tenant — without there being a user: the host validates the caller-supplied
/// binding (a non-protocol form parameter such as <c>organization_id</c>) against its own
/// authority and forces the resulting claims onto the token. Reserved protocol claim names are
/// still blocked at mint. Return <see cref="ClientCredentialsClaimsResult.Reject"/> to refuse the
/// issuance — surfaced to the client as the given OAuth error.
/// </para>
/// </summary>
public interface IClientCredentialsClaimsTransformer
{
    /// <param name="client">The authenticated client (already grant- and scope-checked).</param>
    /// <param name="grantedScopes">The scope set the token will carry.</param>
    /// <param name="extraParameters">Non-standard form parameters from the token request —
    /// everything except the OAuth protocol fields. Single-valued; first value wins. Empty when
    /// the request carried none.</param>
    Task<ClientCredentialsClaimsResult> TransformAsync(
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct = default);
}

/// <summary>The outcome of <see cref="IClientCredentialsClaimsTransformer.TransformAsync"/>.</summary>
public abstract record ClientCredentialsClaimsResult
{
    /// <summary>Issue the token, with <see cref="Claims"/> forced onto it (null or empty = unchanged).</summary>
    public sealed record Allowed(IReadOnlyDictionary<string, string>? Claims) : ClientCredentialsClaimsResult;

    /// <summary>Refuse the issuance with an OAuth error code (RFC 6749 §5.2) and description.</summary>
    public sealed record Rejected(string Error, string Description) : ClientCredentialsClaimsResult;

    public static ClientCredentialsClaimsResult Allow(IReadOnlyDictionary<string, string>? claims = null) => new Allowed(claims);

    public static ClientCredentialsClaimsResult Reject(string error, string description) => new Rejected(error, description);
}

/// <summary>Default no-op transformer: every client-credentials mint passes through unchanged.</summary>
public sealed class NullClientCredentialsClaimsTransformer : IClientCredentialsClaimsTransformer
{
    public Task<ClientCredentialsClaimsResult> TransformAsync(
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct = default)
        => Task.FromResult(ClientCredentialsClaimsResult.Allow());
}

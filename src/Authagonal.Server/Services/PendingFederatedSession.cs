using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

/// <summary>
/// The federation state of a login that has been parked on an MFA challenge, so completing the challenge
/// establishes the SAME session the callback would have established.
/// </summary>
/// <remarks>
/// When a federated user has MFA enrolled, <see cref="FederatedMfaFlow.MaybeChallengeAsync"/> returns a redirect
/// — which means the callback returns BEFORE it builds its sign-in principal. Every federation binding is added
/// only on the fall-through path below that point: <c>saml_connection</c>, <c>saml_name_id</c>,
/// <c>saml_name_id_format</c>, <c>saml_session_index</c>, <c>session_max_exp</c> (from SAML
/// <c>SessionNotOnOrAfter</c> or the OIDC session claim), the IdP-bounded cookie <c>ExpiresUtc</c>, the upstream
/// refresh token and its store seeding, and the <c>federated:*</c> claim passthrough.
/// <para>
/// The session was then established instead by <c>/api/auth/mfa/verify</c> via
/// <c>CookieSignInHelper.SignInAsync</c>, which mints only <c>sub</c>, <c>email</c>, <c>name</c>,
/// <c>security_stamp</c>, a FRESH <c>sid</c>, <c>auth_time</c>, <c>org_id</c> and <c>mfa_authenticated</c>. So
/// none of it survived, and the consequences compound:
/// </para>
/// <list type="bullet">
/// <item><b>Single logout stopped working for exactly the users who had MFA.</b> SLO matches a session by
/// <c>saml_name_id</c>; without it, an IdP-initiated logout could not find the session to end. Enabling MFA on
/// a federated tenant therefore quietly disabled SLO for its enrolled users.</item>
/// <item><b>The IdP's session bound was discarded.</b> No <c>session_max_exp</c> and no cookie
/// <c>ExpiresUtc</c>, so the local session outlived the authentication behind it — the opposite of what
/// federating to an IdP means.</item>
/// <item><b>The upstream refresh token was stranded.</b> It is seeded under the callback's <c>sid</c>, and the
/// MFA sign-in minted a new one, so nothing would ever look it up again.</item>
/// <item><b>Federated claims vanished from tokens</b> for those users only.</item>
/// </list>
/// <para>
/// Stored as a short-lived grant keyed by the challenge id rather than as new columns on
/// <see cref="MfaChallenge"/>: that model is mapped by four storage providers, and the Azure one env-prefixes
/// its <c>ChallengeId</c> — so widening it means four entity mappings and a prefix subtlety, for state that is
/// wanted for two minutes. <see cref="IGrantStore"/> already stores exactly this shape of thing.
/// </para>
/// </remarks>
public sealed class PendingFederatedSession
{
    /// <summary>Grant type. Deliberately NOT in <c>PersistedGrantTypes.SessionBound</c>: this is pre-session
    /// state, and a logout that swept it would break a challenge in flight.</summary>
    public const string GrantType = "pending_federated_session";

    [JsonPropertyName("claims")]
    public List<ClaimRecord> Claims { get; set; } = [];

    /// <summary>The IdP-bounded cookie expiry, as Unix seconds, when the IdP stated one.</summary>
    [JsonPropertyName("cookie_exp")]
    public long? CookieExpiresUnix { get; set; }

    /// <summary>
    /// The session id the callback committed to, so the cookie signed after MFA carries the SAME one.
    /// </summary>
    /// <remarks>
    /// Load-bearing for the OIDC path: the upstream refresh token is seeded into
    /// <c>IUpstreamRefreshTokenStore</c> under a per-(user, connection, sid) key at callback time. A fresh sid
    /// at the MFA sign-in left that record unreachable — the durable rotating copy every RP grant reads, orphaned
    /// for exactly the users who had MFA.
    /// </remarks>
    [JsonPropertyName("sid")]
    public string? SessionId { get; set; }

    public sealed class ClaimRecord
    {
        [JsonPropertyName("t")] public string Type { get; set; } = "";
        [JsonPropertyName("v")] public string Value { get; set; } = "";
    }

    private static string KeyFor(string challengeId) => $"pfs:{challengeId}";

    /// <summary>
    /// Parks the federation claims for <paramref name="challengeId"/>.
    /// </summary>
    /// <remarks>
    /// Best-effort by design: a failure here must not block a login that has already authenticated upstream.
    /// The caller logs it, and the user gets the session the old code would have given them.
    /// </remarks>
    public static async Task StoreAsync(
        IGrantStore grantStore,
        string challengeId,
        string subjectId,
        string? clientId,
        IEnumerable<Claim> federationClaims,
        DateTimeOffset? cookieExpires,
        DateTimeOffset expiresAt,
        CancellationToken ct = default,
        string? sessionId = null)
    {
        var payload = new PendingFederatedSession
        {
            Claims = [.. federationClaims.Select(c => new ClaimRecord { Type = c.Type, Value = c.Value })],
            CookieExpiresUnix = cookieExpires?.ToUnixTimeSeconds(),
            SessionId = sessionId,
        };

        await grantStore.StoreAsync(new PersistedGrant
        {
            Key = KeyFor(challengeId),
            Type = GrantType,
            SubjectId = subjectId,
            ClientId = clientId ?? "",
            Data = JsonSerializer.Serialize(payload, AuthagonalJsonContext.Default.PendingFederatedSession),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
        }, ct);
    }

    /// <summary>
    /// Reads and removes the parked state for <paramref name="challengeId"/>, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Null is the normal case for a password login, which parks nothing — so the caller signs in exactly as
    /// before. Removed on read because the challenge it belongs to is single-use.
    /// </remarks>
    public static async Task<PendingFederatedSession?> ConsumeAsync(
        IGrantStore grantStore, string challengeId, string subjectId, CancellationToken ct = default)
    {
        var key = KeyFor(challengeId);
        var grant = await grantStore.GetAsync(key, ct);
        if (grant is null || grant.Type != GrantType) return null;

        // The parked state belongs to the subject the challenge was issued for. A mismatch means the challenge
        // id and the verifying session disagree, which is not a state to sign a federated cookie from.
        if (!string.Equals(grant.SubjectId, subjectId, StringComparison.Ordinal)) return null;
        if (grant.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow) return null;

        try
        {
            return JsonSerializer.Deserialize(grant.Data, AuthagonalJsonContext.Default.PendingFederatedSession);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            await grantStore.RemoveAsync(key, ct);
        }
    }

    /// <summary>The parked claims, as claims.</summary>
    public IEnumerable<Claim> ToClaims() => Claims.Select(c => new Claim(c.Type, c.Value));

    /// <summary>The parked cookie bound, when the IdP stated one.</summary>
    public DateTimeOffset? CookieExpires =>
        CookieExpiresUnix is { } unix ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
}

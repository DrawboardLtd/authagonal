using System.Text.Json;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Authagonal.Protocol.Services;

/// <summary>
/// Revocation that spans both halves of a grant's authority: the rows in <see cref="IGrantStore"/>,
/// and the access tokens minted under them, which live in <see cref="IRevokedTokenStore"/>.
/// </summary>
/// <remarks>
/// Access tokens here are self-contained ES256 JWTs — there is no reference-token mode — so removing
/// a refresh grant does nothing to the tokens it already issued. Every caller that revokes a grant
/// has to do both halves, and each one that only did the first left the operator believing access
/// had been withdrawn when it had not. Centralised so a new revocation path cannot repeat that.
/// </remarks>
public static class GrantRevocation
{
    /// <summary>
    /// Ends a client's live token authority for one subject: removes the grant rows of the given
    /// <paramref name="types"/> and revokes the access tokens those grants minted. Returns the number
    /// of grant rows removed.
    /// </summary>
    /// <param name="types">Which grant types to remove. Callers ending a session pass
    /// <see cref="PersistedGrantTypes.SessionBound"/>; callers revoking an authorized app pass that
    /// plus <see cref="PersistedGrantTypes.Consent"/>. Passing the consent type alone removes the
    /// user's recorded preference without touching live tokens, which is a different act.</param>
    public static async Task<int> RevokeClientGrantsAsync(
        IGrantStore grantStore,
        IRevokedTokenStore? revokedTokenStore,
        string subjectId,
        string clientId,
        IReadOnlyCollection<string> types,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var grants = await grantStore.GetBySubjectAsync(subjectId, ct);

        var doomed = grants
            .Where(g => string.Equals(g.ClientId, clientId, StringComparison.Ordinal)
                        && types.Contains(g.Type))
            .ToList();

        // Access tokens are read off the refresh grants before the rows go away — afterwards there is
        // no record of which jtis this family issued, and a self-contained JWT no one can name cannot
        // be revoked by any party.
        foreach (var grant in doomed.Where(g => g.Type == PersistedGrantTypes.RefreshToken))
            await RevokeTrackedAccessTokensAsync(revokedTokenStore, grant, logger, ct);

        await grantStore.RemoveBySubjectAsync(subjectId, types, clientId, ct);

        return doomed.Count;
    }

    /// <summary>
    /// Writes a refresh grant's tracked access tokens to the revoked-token store. Returns how many
    /// were still live and therefore worth revoking.
    /// </summary>
    /// <remarks>
    /// Each entry is written with the token's own expiry, so the revocation row lives exactly as long
    /// as the token it kills and the stores' existing expiry reapers keep the list bounded. A host with
    /// no <see cref="IRevokedTokenStore"/> registered tracks nothing — the same degradation the
    /// token-exchange revocation check already accepts, since the alternative is failing revocation
    /// closed over a store the host chose not to configure.
    /// </remarks>
    public static async Task<int> RevokeTrackedAccessTokensAsync(
        IRevokedTokenStore? revokedTokenStore,
        PersistedGrant grant,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (revokedTokenStore is null || string.IsNullOrEmpty(grant.Data)) return 0;

        RefreshTokenData? data;
        try
        {
            data = JsonSerializer.Deserialize(grant.Data, ProtocolJsonContext.Default.RefreshTokenData);
        }
        catch (JsonException)
        {
            // The caller is removing this grant either way; failing the whole revocation over an
            // unreadable payload would leave the refresh token alive, which is the worse outcome.
            logger?.LogWarning(
                "Could not read refresh grant data while revoking access tokens for client {ClientId}",
                grant.ClientId);
            return 0;
        }

        if (data?.AccessTokens is not { Count: > 0 } tracked) return 0;

        var now = DateTimeOffset.UtcNow;
        var revoked = 0;
        foreach (var token in tracked)
        {
            // An expired token needs no revocation row — every enforcement point already rejects it
            // on exp, and writing one would only add a row the reaper has to clear.
            if (token.ExpiresAt <= now) continue;
            await revokedTokenStore.AddAsync(token.Jti, token.ExpiresAt, grant.ClientId, ct);
            revoked++;
        }

        if (revoked > 0)
        {
            logger?.LogInformation(
                "Revoked {Count} access token(s) minted under a revoked refresh grant. Client: {ClientId}, Subject: {SubjectId}",
                revoked, grant.ClientId, grant.SubjectId);
        }

        return revoked;
    }
}

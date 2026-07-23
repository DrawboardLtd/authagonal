using System.Collections.Generic;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Server.Services.Oidc;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// Server's <see cref="IOidcSubjectResolver"/>: maps an authenticated
/// <see cref="ClaimsPrincipal"/> to an <see cref="OidcSubject"/> by looking up the
/// corresponding <see cref="AuthUser"/> in the user store and inflating groups from
/// the SCIM group store. On refresh, re-reads the user to pick up deactivation,
/// role changes, and fresh group membership — the token endpoint then mints against
/// this fresh subject, so nothing survives deactivation across a refresh.
/// </summary>
public sealed class UserStoreOidcSubjectResolver(
    IUserStore userStore,
    IScimGroupStore scimGroupStore,
    IScimGroupRoleMappingStore groupRoleMappingStore,
    IClientStore clientStore,
    IOidcProviderStore oidcProviderStore,
    OidcDiscoveryClient discoveryClient,
    ISecretProvider secretProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<UserStoreOidcSubjectResolver> logger,
    IUpstreamRefreshTokenStore? upstreamTokenStore = null) : IOidcSubjectResolver
{
    public async Task<OidcSubjectResult> ResolveAsync(
        ClaimsPrincipal authenticatedPrincipal,
        OidcSubjectResolutionContext context,
        CancellationToken ct = default)
    {
        var subjectId = authenticatedPrincipal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? authenticatedPrincipal.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(subjectId))
            return OidcSubjectResult.Reject(OidcRejection.LoginRequired, "No subject claim on principal");

        var user = await userStore.GetAsync(subjectId, ct);
        if (user is null || !user.IsActive)
            return OidcSubjectResult.Reject(OidcRejection.AccessDenied, "User not found or inactive");

        var client = await clientStore.GetAsync(context.ClientId, ct);

        // Propagate the upstream-federation cap captured by the cookie (session_max_exp).
        // This is set at sign-in time when an IdP asserts a session lifetime.
        DateTimeOffset? sessionMaxExpiresAt = null;
        var sessionMaxExpClaim = authenticatedPrincipal.FindFirstValue("session_max_exp");
        if (!string.IsNullOrEmpty(sessionMaxExpClaim) &&
            long.TryParse(sessionMaxExpClaim, out var sessionMaxExpSeconds))
        {
            sessionMaxExpiresAt = DateTimeOffset.FromUnixTimeSeconds(sessionMaxExpSeconds);
        }

        var sessionId = authenticatedPrincipal.FindFirstValue("sid");

        // Federation claims captured at the OIDC callback ride on the cookie as
        // `federated:<name>` claims. Pass them through OidcSubject.FederationClaims
        // so ProtocolTokenService's scope-gated emission re-releases them on the
        // Authagonal-issued token, and so they survive refresh rotations distinct
        // from per-user CustomAttributes (which we re-read fresh on refresh).
        var federationClaims = ExtractFederationClaims(authenticatedPrincipal);

        // Upstream-federated refresh: the token rode the cookie from the federation callback and is also
        // seeded into IUpstreamRefreshTokenStore keyed by (user, connection, sid). Prefer the STORE — it
        // holds the latest rotated token shared by every RP grant for this session, so a new authorize
        // doesn't seed a grant from a cookie copy the upstream already rotated to death. The cookie is the
        // fallback (first authorize before any refresh, or no store registered). Non-emitted.
        var upstreamRefreshToken = authenticatedPrincipal.FindFirstValue("upstream_refresh_token");
        var upstreamConnectionId = authenticatedPrincipal.FindFirstValue("upstream_connection_id");
        if (upstreamTokenStore is not null && !string.IsNullOrEmpty(upstreamConnectionId) && !string.IsNullOrEmpty(sessionId))
        {
            var stored = await upstreamTokenStore.GetAsync(subjectId, upstreamConnectionId, sessionId, ct);
            if (!string.IsNullOrEmpty(stored))
                upstreamRefreshToken = stored;
        }

        var subject = await BuildSubjectAsync(
            user, client, sessionMaxExpiresAt, sessionId, federationClaims,
            upstreamRefreshToken, upstreamConnectionId, ct);
        return OidcSubjectResult.Allow(subject);
    }

    private const string FederationClaimPrefix = "federated:";

    private static Dictionary<string, string> ExtractFederationClaims(ClaimsPrincipal principal)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var claim in principal.Claims)
        {
            if (!claim.Type.StartsWith(FederationClaimPrefix, StringComparison.Ordinal))
                continue;
            var name = claim.Type[FederationClaimPrefix.Length..];
            if (string.IsNullOrEmpty(name)) continue;
            result[name] = claim.Value;
        }
        return result;
    }

    public async Task<OidcSubjectResult> ResolveRefreshAsync(
        OidcSubject priorSubject,
        OidcSubjectResolutionContext context,
        CancellationToken ct = default)
    {
        var user = await userStore.GetAsync(priorSubject.SubjectId, ct);
        if (user is null || !user.IsActive)
            return OidcSubjectResult.Reject(OidcRejection.AccessDenied, "User not found or inactive");

        var client = await clientStore.GetAsync(context.ClientId, ct);

        // Upstream-federated refresh (Option A): the point of this path. Redeem the stored upstream
        // refresh token against its IdP. If the upstream refuses (invalid_grant — the federated
        // credential, e.g. a share link, was revoked or expired), reject the local refresh too so the
        // session dies; on success, carry the rotated token forward into the successor grant. A transient
        // failure leaves the session alive (the SessionMaxExpiresAt cap still bounds it).
        // Read the latest upstream token from the shared store (rotated by whichever RP refreshed last),
        // falling back to the copy pinned on this grant. Redeem it; on rotation, write the new token back
        // to the store so sibling RP grants see it; on revocation, drop it.
        var upstreamRefreshToken = priorSubject.UpstreamRefreshToken;
        if (upstreamTokenStore is not null && !string.IsNullOrEmpty(priorSubject.UpstreamConnectionId) && !string.IsNullOrEmpty(priorSubject.SessionId))
        {
            var stored = await upstreamTokenStore.GetAsync(priorSubject.SubjectId, priorSubject.UpstreamConnectionId, priorSubject.SessionId, ct);
            if (!string.IsNullOrEmpty(stored))
                upstreamRefreshToken = stored;
        }
        if (!string.IsNullOrEmpty(upstreamRefreshToken) && !string.IsNullOrEmpty(priorSubject.UpstreamConnectionId))
        {
            var (outcome, rotated) = await RedeemUpstreamRefreshAsync(
                priorSubject.UpstreamConnectionId, upstreamRefreshToken, ct);
            if (outcome == UpstreamRefreshOutcome.Revoked)
            {
                if (upstreamTokenStore is not null && !string.IsNullOrEmpty(priorSubject.SessionId))
                    await upstreamTokenStore.RemoveAsync(priorSubject.SubjectId, priorSubject.UpstreamConnectionId, priorSubject.SessionId, ct);
                return OidcSubjectResult.Reject(
                    OidcRejection.AccessDenied,
                    "Upstream session ended (the federated credential was revoked or has expired).");
            }
            if (outcome == UpstreamRefreshOutcome.Valid)
            {
                upstreamRefreshToken = rotated;
                if (upstreamTokenStore is not null && !string.IsNullOrEmpty(priorSubject.SessionId) && !string.IsNullOrEmpty(rotated))
                    await upstreamTokenStore.SetAsync(
                        priorSubject.SubjectId, priorSubject.UpstreamConnectionId!, priorSubject.SessionId!, rotated!,
                        priorSubject.SessionMaxExpiresAt ?? DateTimeOffset.UtcNow.AddDays(7), ct);
            }
        }

        // Preserve the federation cap, session id, and federation claims across rotations
        // — the resolver can't re-read any of them from the cookie at refresh time, and
        // they must survive rotations so the cap can't be lifted, back-channel logouts can
        // correlate, and federation-derived claims keep flowing onto refreshed tokens.
        var subject = await BuildSubjectAsync(
            user, client,
            priorSubject.SessionMaxExpiresAt,
            priorSubject.SessionId,
            priorSubject.FederationClaims,
            upstreamRefreshToken,
            priorSubject.UpstreamConnectionId,
            ct);
        return OidcSubjectResult.Allow(subject);
    }

    private enum UpstreamRefreshOutcome { Valid, Revoked, Transient }

    /// <summary>
    /// Redeems the upstream refresh token at its connection's token endpoint. <see cref="UpstreamRefreshOutcome.Valid"/>
    /// (with the rotated token, or the same token if the upstream didn't rotate) on success;
    /// <see cref="UpstreamRefreshOutcome.Revoked"/> on a 4xx (invalid_grant — credential gone); and
    /// <see cref="UpstreamRefreshOutcome.Transient"/> on any transport/5xx/config error, which keeps the
    /// session alive (bounded by SessionMaxExpiresAt) rather than killing it over a blip.
    /// </summary>
    private async Task<(UpstreamRefreshOutcome Outcome, string? RotatedToken)> RedeemUpstreamRefreshAsync(
        string connectionId, string refreshToken, CancellationToken ct)
    {
        OidcProviderConfig? config;
        try
        {
            config = await oidcProviderStore.GetAsync(connectionId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upstream-refresh: could not load connection {ConnectionId}; treating as transient", connectionId);
            return (UpstreamRefreshOutcome.Transient, null);
        }
        if (config is null)
        {
            logger.LogWarning("Upstream-refresh: connection {ConnectionId} no longer exists; treating as transient", connectionId);
            return (UpstreamRefreshOutcome.Transient, null);
        }

        string tokenEndpoint;
        string clientSecret;
        try
        {
            var discovery = await discoveryClient.GetDiscoveryAsync(config.MetadataLocation, ct);
            tokenEndpoint = discovery.TokenEndpoint;
            clientSecret = await secretProvider.ResolveAsync(config.ClientSecret, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upstream-refresh: discovery/secret failed for {ConnectionId}; treating as transient", connectionId);
            return (UpstreamRefreshOutcome.Transient, null);
        }

        HttpResponseMessage response;
        string body;
        try
        {
            var client = httpClientFactory.CreateClient("OidcDiscovery");
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = config.ClientId,
                ["client_secret"] = clientSecret,
            });
            response = await client.PostAsync(tokenEndpoint, content, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upstream-refresh: token request failed for {ConnectionId}; treating as transient", connectionId);
            return (UpstreamRefreshOutcome.Transient, null);
        }

        if (response.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var rotated = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
                // No rotation → keep redeeming the same token next time.
                return (UpstreamRefreshOutcome.Valid, string.IsNullOrEmpty(rotated) ? refreshToken : rotated);
            }
            catch
            {
                return (UpstreamRefreshOutcome.Valid, refreshToken);
            }
        }

        // Only error=invalid_grant means the refresh token itself is gone/revoked — fail closed so
        // revocation propagates. Any OTHER 4xx is an operator/config fault, NOT proof the user's
        // session ended: invalid_client (a rotated or misconfigured client secret), invalid_request,
        // unauthorized_client, a 429, etc. Treating those as revocation would mass-terminate EVERY
        // federated session on this connection at once. Keep the session (bounded by the absolute
        // session cap) and surface the fault in logs. 5xx is transient too. An unparseable/absent
        // error body is treated as transient (fail open) rather than revoking on ambiguity.
        if ((int)response.StatusCode is >= 400 and < 500)
        {
            var error = TryReadOAuthError(body);
            if (string.Equals(error, "invalid_grant", StringComparison.Ordinal))
            {
                logger.LogInformation("Upstream-refresh: connection {ConnectionId} returned invalid_grant; ending the local session", connectionId);
                return (UpstreamRefreshOutcome.Revoked, null);
            }

            logger.LogWarning("Upstream-refresh: connection {ConnectionId} returned {Status} (error={Error}); treating as transient, session kept", connectionId, (int)response.StatusCode, error ?? "none");
            return (UpstreamRefreshOutcome.Transient, null);
        }

        logger.LogWarning("Upstream-refresh: connection {ConnectionId} returned {Status}; treating as transient", connectionId, (int)response.StatusCode);
        return (UpstreamRefreshOutcome.Transient, null);
    }

    // Extract the RFC 6749 `error` code from an OAuth token-endpoint error response. Returns null if
    // the body is empty or not the expected JSON object, in which case the caller treats it as transient.
    private static string? TryReadOAuthError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e)
                    ? e.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds an <see cref="OidcSubject"/> from an <see cref="AuthUser"/>. Exposed for
    /// device-code and admin token paths that already know the subject and don't go
    /// through the authorize endpoint.
    /// </summary>
    public async Task<OidcSubject> BuildSubjectAsync(
        AuthUser user,
        OAuthClient? client,
        DateTimeOffset? sessionMaxExpiresAt = null,
        string? sessionId = null,
        IReadOnlyDictionary<string, string>? federationClaims = null,
        string? upstreamRefreshToken = null,
        string? upstreamConnectionId = null,
        CancellationToken ct = default)
    {
        // SCIM group → role mappings (empty store = no-op). Fetch the user's groups once,
        // used for both the optional groups claim and effective-role resolution.
        var mappings = await groupRoleMappingStore.GetAllAsync(ct);
        IReadOnlyList<ScimGroup>? scimGroups = null;
        if (mappings.Count > 0 || client is { IncludeGroupsInTokens: true })
            scimGroups = await scimGroupStore.GetGroupsByUserIdAsync(user.Id, ct);

        IReadOnlyList<string>? groups = null;
        if (client is { IncludeGroupsInTokens: true } && scimGroups is { Count: > 0 })
            groups = scimGroups.Select(g => g.DisplayName).ToList();

        // Effective roles = directly-assigned ∪ roles granted by the user's group memberships.
        var roles = new HashSet<string>(user.Roles, StringComparer.Ordinal);
        if (mappings.Count > 0 && scimGroups is { Count: > 0 })
        {
            var memberGroupIds = scimGroups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var m in mappings)
                if (memberGroupIds.Contains(m.GroupId))
                    roles.Add(m.Role);
        }

        return new OidcSubject
        {
            SubjectId = user.Id,
            Email = user.Email,
            EmailVerified = user.EmailConfirmed,
            GivenName = user.FirstName,
            FamilyName = user.LastName,
            Phone = user.Phone,
            Locale = user.Locale,
            OrganizationId = user.OrganizationId,
            Roles = roles.Count > 0 ? roles.ToList() : null,
            Groups = groups,
            CustomAttributes = user.CustomAttributes.Count > 0
                ? user.CustomAttributes.ToDictionary(kv => kv.Key, kv => kv.Value)
                : null,
            FederationClaims = federationClaims is { Count: > 0 } ? federationClaims : null,
            SessionMaxExpiresAt = sessionMaxExpiresAt,
            SessionId = sessionId,
            UpstreamRefreshToken = upstreamRefreshToken,
            UpstreamConnectionId = upstreamConnectionId,
        };
    }
}

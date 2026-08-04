using Authagonal.Core.Constants;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Services;

/// <summary>
/// What "logged out" means, in one place: notify the relying parties and drop the session-bound grants.
/// </summary>
/// <remarks>
/// There are two sign-out paths in this product and they disagreed. <c>/connect/endsession</c> collected
/// front-channel URIs, built and POSTed signed Logout Tokens, and revoked
/// <c>PersistedGrantTypes.SessionBound</c>. <c>POST /api/auth/logout</c> — the one the OP's OWN login app calls,
/// and therefore the one most users actually traverse — did none of it: its entire body was an upstream-session
/// cleanup and a cookie <c>SignOutAsync</c>.
/// <para>
/// So the everyday sign-out left every relying party believing the user was still present, and left the
/// session-bound grants in the store: the refresh tokens and authorization codes minted for that session stayed
/// usable after the user had, as far as they were concerned, logged out. Meanwhile the discovery document
/// advertises <c>backchannel_logout_supported</c>, <c>frontchannel_logout_supported</c> and both
/// <c>*_session_supported</c> flags unconditionally, and <c>docs/configuration.md</c> states "When a user logs
/// out, Authagonal sends a signed logout token (JWT) to each client's registered URI" — true of one path and
/// not the other.
/// </para>
/// <para>
/// Extracted rather than duplicated for the reason this whole review keeps rediscovering: a rule implemented
/// twice is a rule that is about to be fixed once. Every hardening already in this block — the outbound-URL
/// guard on both URI kinds with loopback allowed for the browser-side fetch and refused for the server-side
/// POST, the two-minute token lifetime, the explicit <c>TokenType</c> so a logout token cannot be presented as
/// a subject token, session-bound-only revocation so signing out does not silently discard the user's stored
/// consents — now applies to both paths by construction.
/// </para>
/// </remarks>
public static class SessionTermination
{
    /// <summary>Front-channel URIs to load, for a caller that can render them.</summary>
    /// <remarks>
    /// Returned rather than acted on, because only <c>/connect/endsession</c> renders a page. The JSON logout
    /// endpoint hands them to the login app, which is the only party in that flow with a browser to load them
    /// in — a server cannot perform a front-channel logout on the user's behalf by definition.
    /// </remarks>
    public sealed record Result(IReadOnlyList<string> FrontChannelUris);

    /// <summary>
    /// Collects front-channel URIs, POSTs a signed Logout Token to every client registered for one, and
    /// revokes the subject's session-bound grants.
    /// </summary>
    /// <remarks>
    /// Call this BEFORE the cookie is dropped: the grant lookup needs the subject, and the session id has to
    /// come off the live principal.
    /// <para>
    /// Everything that needs the request's tenant scope — grants, clients, the signed tokens — is resolved
    /// here, synchronously. Only the HTTP POSTs are backgrounded, because a background scope has no tenant and
    /// its store resolution throws, which is how an earlier fire-and-forget-with-a-fresh-scope silently emitted
    /// nothing at all.
    /// </para>
    /// </remarks>
    public static async Task<Result> NotifyAndRevokeAsync(
        HttpContext httpContext,
        string? subjectId,
        string? sessionId,
        IClientStore clientStore,
        IGrantStore grantStore,
        IKeyManager keyManager,
        ITenantContext tenantContext,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct,
        IRevokedTokenStore? revokedTokenStore = null)
    {
        var frontChannelUris = new List<string>();
        if (string.IsNullOrEmpty(subjectId)) return new Result(frontChannelUris);

        var logger = httpContext.RequestServices
            .GetRequiredService<ILoggerFactory>().CreateLogger("SessionTermination");

        // Optional, and resolved here rather than taken as a required handler parameter, for the same reason
        // ConsentEndpoint does it this way: a host that registered no provider has no revoked-token store, and
        // failing sign-out over its absence would be worse than tracking nothing. The parameter above exists so
        // a test can inject one directly.
        revokedTokenStore ??= httpContext.RequestServices.GetService<IRevokedTokenStore>();

        List<(string Uri, string Token)> notifications = [];
        try
        {
            var grants = await grantStore.GetBySubjectAsync(subjectId);
            foreach (var clientIdGrant in grants.Select(g => g.ClientId).Distinct())
            {
                var c = await clientStore.GetAsync(clientIdGrant, ct);
                if (c is null) continue;

                if (c.FrontChannelLogoutUri is not null
                    // Re-checked at USE time, not only at write time. A client row can predate the registration
                    // guard, arrive from a migration, or be written by an embedding host's own IClientStore —
                    // and this URI goes in an iframe src, so an RFC1918 or link-local one turns any logout into
                    // a browser-side probe of whatever private network the user sits on.
                    //
                    // Loopback is allowed here and ONLY here: the fetch is made by the USER's browser, so
                    // http://localhost:PORT is that user's own machine, which is how a local-dev relying party
                    // legitimately receives front-channel logout.
                    && OutboundUrl.IsSafe(c.FrontChannelLogoutUri, allowLoopback: true))
                {
                    var uri = c.FrontChannelLogoutUri;
                    if (c.FrontChannelLogoutSessionRequired)
                    {
                        var sep = uri.Contains('?') ? '&' : '?';
                        uri = $"{uri}{sep}iss={Uri.EscapeDataString(tenantContext.Issuer)}";
                        if (!string.IsNullOrEmpty(sessionId))
                            uri += $"&sid={Uri.EscapeDataString(sessionId)}";
                    }
                    frontChannelUris.Add(uri);
                }

                if (c.BackChannelLogoutUri is null) continue;

                // No allowLoopback on this one, unlike the front channel: this request is made BY the server, so
                // loopback is the server's own network namespace rather than the user's. A DIAGNOSTIC rather
                // than the guard — the guard travels with the send below, which matters because the send happens
                // in a fire-and-forget task, in another scope, at another time. Its value here is attribution:
                // the client id is still in scope, so a refusal names the client whose registration is wrong.
                if (!OutboundUrl.IsSafe(c.BackChannelLogoutUri))
                {
                    logger.LogWarning(
                        "Back-channel logout for client {ClientId} refused: the registered URI is not a "
                        + "permitted outbound target", clientIdGrant);
                    continue;
                }

                var tokenSid = c.BackChannelLogoutSessionRequired ? sessionId : null;
                notifications.Add((c.BackChannelLogoutUri,
                    CreateBackChannelLogoutToken(tenantContext.Issuer, clientIdGrant, subjectId, tokenSid, keyManager)));
            }

            // Session-bound grants ONLY. RemoveAllBySubjectAsync would delete the user's recorded `consent` and
            // `agent_consent` records and every pending approval along with the tokens. Ending a session is not
            // revoking consent — there is a separate Authorized Apps page for that — so the broader call made
            // logging out silently discard preferences the user never asked to discard.
            //
            // Through GrantRevocation, not IGrantStore directly, because removing a refresh grant does nothing
            // to the access tokens it already minted: those are self-contained ES256 JWTs valid to their own
            // exp. This path deleted the rows and stopped there, so for up to AccessTokenLifetimeSeconds after
            // signing out — 30 minutes on the defaults — the token still passed the JwtBearer scheme, still
            // returned the user's claims from /connect/userinfo, and still reported active:true from
            // /connect/introspect. Revoking the same grant from the Authorized Apps page killed it immediately,
            // because THAT path came through here. Same product, same token, opposite answers.
            await GrantRevocation.RevokeSubjectGrantsAsync(
                grantStore, revokedTokenStore, subjectId, PersistedGrantTypes.SessionBound, logger, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session termination preparation failed for subject {SubjectId}", subjectId);
        }

        if (notifications.Count > 0)
            _ = Task.Run(async () =>
            {
                foreach (var (uri, token) in notifications)
                {
                    try
                    {
                        var client = httpClientFactory.CreateClient("BackChannelLogout");
                        client.Timeout = TimeSpan.FromSeconds(10);
                        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, uri)
                        {
                            Content = new FormUrlEncodedContent(
                                new Dictionary<string, string> { ["logout_token"] = token }),
                        };
                        using var _ = await SafeOutboundHttp.SendAsync(
                            client, logoutRequest, logger, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Back-channel logout POST failed for {Uri}", uri);
                    }
                }
            });

        return new Result(frontChannelUris);
    }

    internal static string CreateBackChannelLogoutToken(
        string issuer, string clientId, string subjectId, string? sessionId, IKeyManager keyManager)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subjectId,
            // Must be JSON-serializable — an anonymous type throws IDX11025 at CreateToken (silent RP-notify failure).
            ["events"] = new Dictionary<string, object>
            {
                ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>(),
            },
            ["jti"] = Guid.NewGuid().ToString("N"),
        };

        if (!string.IsNullOrEmpty(sessionId))
            claims["sid"] = sessionId;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = clientId,
            IssuedAt = DateTime.UtcNow,
            // A logout token is delivered immediately and consumed once. With Expires unset IdentityModel
            // stamps exp = iat + 60 minutes, so a captured token stayed usable for an hour. Two minutes is
            // ample for the POST and bounds the replay window.
            Expires = DateTime.UtcNow.AddMinutes(2),
            // Make the kind explicit, so this token cannot be presented anywhere an access token is expected —
            // the token-exchange endpoint accepted exactly this token as a subject_token.
            TokenType = TokenTypes.LogoutJwt,
            Claims = claims,
            SigningCredentials = keyManager.GetSigningCredentials(),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Endpoints;

public static class UserinfoEndpoint
{
    public static IEndpointRouteBuilder MapUserinfoEndpoint(this IEndpointRouteBuilder app)
    {
        // OIDC Core §5.3.1: userinfo MUST accept both GET and POST. Only GET was mapped, so a
        // client following the spec's POST form got a 405 from an endpoint that advertises support.
        app.MapMethods("/connect/userinfo", ["GET", "POST"], async (
            HttpContext httpContext,
            Authagonal.Core.Services.IKeyManager keyManager,
            IUserStore userStore,
            IScimGroupStore scimGroupStore,
            IRevokedTokenStore revokedTokenStore,
            Authagonal.Core.Services.ITenantContext tenantContext,
            CancellationToken ct) =>
        {
            // RFC 6750 §2 — Authorization header or form-encoded body, shared with the Protocol host's
            // twin. Reading the header alone meant a client that POSTed `access_token` (the shape §2.2
            // exists for) was answered as if it had presented no credential.
            var (token, extractionError) = await Authagonal.Protocol.Endpoints.BearerToken.ReadAsync(httpContext, ct);
            if (extractionError is not null)
                return extractionError;
            if (string.IsNullOrWhiteSpace(token))
                return UnauthorizedWithChallenge();

            // Validate the JWT
            var issuer = tenantContext.Issuer;
            var keys = keyManager.GetSecurityKeys().Select(Authagonal.Protocol.Services.ProtocolSigningKeyOps.JwkToSecurityKey).ToList();

            var validationParams = new TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidateIssuer = true,
                ValidateAudience = true,
                // userinfo is the OP's own endpoint: any valid access token this OP issued (correct
                // issuer + signature + lifetime, with the openid scope) may call it, regardless of the
                // client it was minted for. So "any audience present" is correct here — unlike a
                // resource server, which pins its own audience.
                AudienceValidator = (audiences, _, _) => audiences?.Any() == true,
                ValidateLifetime = true,
                ValidAlgorithms = ["ES256"],
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(60)
            };

            var handler = new JsonWebTokenHandler();
            var result = await handler.ValidateTokenAsync(token, validationParams);

            if (!result.IsValid)
                return UnauthorizedWithChallenge();

            // Every JWT this server signs shares one issuer and one key, so a valid signature does not
            // establish that the bearer holds an ACCESS token. Without this, an id_token or a back-channel
            // logout token was accepted here (cross-JWT confusion) — an id_token is issued to the client,
            // not as a credential for calling the OP.
            if (!TokenTypes.IsAccessToken(
                    (result.SecurityToken as Microsoft.IdentityModel.JsonWebTokens.JsonWebToken)?.Typ,
                    result.Claims.ContainsKey))
                return UnauthorizedWithChallenge();

            // Scope-gate claims (OIDC §5.3.2): `sub` is always returned; profile/email/phone claims
            // are released only when the corresponding scope was granted to the access token.
            var scopeClaim = result.Claims.TryGetValue("scope", out var scObj) ? scObj?.ToString() ?? "" : "";
            var scopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // OIDC Core §5.3.1: userinfo is reached with an access token issued for an OpenID Connect
            // request. The comment above the validation parameters has always asserted "with the openid
            // scope" while nothing checked it, so a pure-OAuth token — a client_credentials token, or
            // one minted for an API-only scope — read this endpoint and got `sub` back.
            //
            // Checked BEFORE the subject and the user lookup, which is where its twin in Authagonal.Protocol
            // checks it. That ordering is the whole answer, not a detail: a client_credentials token has no
            // `sub` at all, so resolving the subject first meant this host answered 401 invalid_token where
            // the Protocol host answered 403 insufficient_scope — the same request, two different answers,
            // and the misleading one of the two. `invalid_token` tells a client its token is bad, so a
            // conforming client refreshes, gets a token with the same scopes, and loops. It also spends a
            // user-store read on a token that was never entitled to this endpoint.
            if (!scopes.Contains(StandardScopes.OpenId, StringComparer.Ordinal))
                return Authagonal.Protocol.Endpoints.UserinfoEndpoint.InsufficientScope(
                    "The access token does not carry the openid scope.");

            var subjectId = result.Claims.TryGetValue("sub", out var sub) ? sub?.ToString() : null;
            if (string.IsNullOrWhiteSpace(subjectId))
                return UnauthorizedWithChallenge();

            // Reject revoked access tokens (the JWT may still be unexpired).
            var jti = result.Claims.TryGetValue("jti", out var jtiObj) ? jtiObj?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(jti) && await revokedTokenStore.IsRevokedAsync(jti, ct))
                return UnauthorizedWithChallenge();

            var user = await userStore.GetAsync(subjectId, ct);
            if (user is null)
                return UnauthorizedWithChallenge();

            // A deactivated subject is not a subject this endpoint answers for. The store read above already
            // happened, so this costs nothing — and without it the OP's own userinfo kept returning a disabled
            // user's email, name, phone, roles and groups for the remaining lifetime of an access token minted
            // before the account was disabled. Deactivation now revokes those tokens (see GrantRevocation), but
            // this is the check that does not depend on the revocation list being complete or on the host
            // having registered a revoked-token store at all.
            if (!user.IsActive)
                return UnauthorizedWithChallenge();

            var claims = new Dictionary<string, object?> { ["sub"] = user.Id };

            if (scopes.Contains(StandardScopes.Email, StringComparer.Ordinal))
            {
                claims["email"] = user.Email;
                claims["email_verified"] = user.EmailConfirmed;
            }

            var hasProfile = scopes.Contains(StandardScopes.Profile, StringComparer.Ordinal);

            if (hasProfile)
            {
                if (!string.IsNullOrWhiteSpace(user.FirstName))
                    claims["given_name"] = user.FirstName;
                if (!string.IsNullOrWhiteSpace(user.LastName))
                    claims["family_name"] = user.LastName;
                var fullName = $"{user.FirstName} {user.LastName}".Trim();
                if (!string.IsNullOrWhiteSpace(fullName))
                    claims["name"] = fullName;
                if (!string.IsNullOrWhiteSpace(user.Locale))
                    claims["locale"] = user.Locale;
            }

            // OIDC §5.4 gives the phone claims their own scope. They were released under `profile`,
            // so the consent screen never told the user their phone number was being disclosed.
            if (scopes.Contains(StandardScopes.Phone, StringComparer.Ordinal)
                && !string.IsNullOrWhiteSpace(user.Phone))
            {
                claims["phone_number"] = user.Phone;
            }

            // org_id, roles and the full SCIM group membership were appended to EVERY response with
            // no scope gate whatsoever — three lines below a comment citing §5.3.2 for the claims
            // that were gated. Group membership in particular is an organisational graph fetched live
            // from the group store and handed to any client holding any access token.
            if (hasProfile && !string.IsNullOrWhiteSpace(user.OrganizationId))
                claims["org_id"] = user.OrganizationId;

            if (scopes.Contains(StandardScopes.Roles, StringComparer.Ordinal) && user.Roles.Count > 0)
                claims["roles"] = user.Roles;

            if (scopes.Contains(StandardScopes.Groups, StringComparer.Ordinal))
            {
                var groups = await scimGroupStore.GetGroupsByUserIdAsync(user.Id, ct);
                if (groups.Count > 0)
                {
                    claims["groups"] = groups.Select(g => new { id = g.Id, name = g.DisplayName }).ToArray();
                }
            }

            // The body is the subject's PII. It gets the same no-store the token response gets —
            // without it an intermediary may apply heuristic freshness to a 200 with no explicit
            // expiry and hand one bearer's claims to the next caller of the same URL.
            return Authagonal.Protocol.Endpoints.JsonResults.NoStore(Results.Ok(claims));
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .WithTags("OAuth");

        return app;
    }

    /// <summary>
    /// A 401 carrying the challenge RFC 6750 §3 requires.
    /// </summary>
    /// <remarks>
    /// Bare 401s told a resource client that its token was unacceptable but not that Bearer was even
    /// the scheme in use — the header is how a client learns which credential to present and whether
    /// to refresh rather than re-authenticate. The reason is deliberately generic: the endpoint
    /// distinguishes several causes internally (wrong token type, revoked, unknown subject), and some
    /// of those would report on state the caller has not proved it may know.
    /// </remarks>
    private static IResult UnauthorizedWithChallenge() => new BearerChallenge();

    private sealed class BearerChallenge : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            httpContext.Response.Headers.WWWAuthenticate =
                "Bearer realm=\"userinfo\", error=\"invalid_token\"";
            return Task.CompletedTask;
        }
    }
}

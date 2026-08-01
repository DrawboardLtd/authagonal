using Authagonal.Core.Constants;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Protocol.Endpoints;

internal static class UserinfoEndpoint
{
    public static IEndpointRouteBuilder MapProtocolUserinfoEndpoint(this IEndpointRouteBuilder app)
    {
        // OIDC Core §5.3.1: userinfo MUST accept both GET and POST. This host mapped GET alone, so a
        // client following the spec's POST form got a 405 from an endpoint that advertises support —
        // the Server host's twin was fixed and this one, the package that ships on nuget.org, was not.
        app.MapMethods("/connect/userinfo", ["GET", "POST"], async (
            HttpContext httpContext,
            IKeyManager keyManager,
            ITenantContext tenantContext,
            CancellationToken ct) =>
        {
            var (token, extractionError) = await BearerToken.ReadAsync(httpContext, ct);
            if (extractionError is not null)
                return extractionError;
            if (string.IsNullOrWhiteSpace(token))
                return UnauthorizedWithChallenge();

            var issuer = tenantContext.Issuer;
            // JsonWebKey already implements SecurityKey — no conversion needed,
            // and this works for both EC and RSA without algorithm-specific handling.
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
                // Pin the algorithm, as the Server host's userinfo does. This validator was the one
                // inbound-token path with no ValidAlgorithms — a policy gap rather than a live hole
                // (the keys are EC, so no HMAC provider can be constructed over them), but the
                // guarantee belongs in this code rather than in a library default.
                ValidAlgorithms = ["ES256", "ES384", "ES512"],
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(60)
            };

            var handler = new JsonWebTokenHandler();
            var result = await handler.ValidateTokenAsync(token, validationParams);

            if (!result.IsValid)
                return UnauthorizedWithChallenge();

            // Only an access token may call userinfo — see the Server host's equivalent check. An
            // id_token or a logout token carries the same issuer and signature but is not a credential.
            if (!TokenTypes.IsAccessToken(
                    (result.SecurityToken as Microsoft.IdentityModel.JsonWebTokens.JsonWebToken)?.Typ,
                    result.Claims.ContainsKey))
                return UnauthorizedWithChallenge();

            // An access token is a self-contained JWT: revoking it writes the jti to
            // IRevokedTokenStore, and an endpoint that never reads that store keeps honouring the
            // token until its natural exp. /connect/revocation and replay-triggered family revocation
            // both land there, so without this check a token the client (or an operator responding to
            // a compromise) explicitly killed still returned the subject's full claim set here — while
            // the Server host's twin refused it. Resolved through the service provider rather than as
            // a handler parameter because the store is an optional host registration: a host that
            // registers none has nothing to revoke into, which is the same degradation the token
            // service already documents, not a check silently skipped on a host that has one.
            var jti = result.Claims.TryGetValue("jti", out var jtiObj) ? jtiObj?.ToString() : null;
            var revokedTokenStore = httpContext.RequestServices.GetService<IRevokedTokenStore>();
            if (revokedTokenStore is not null && !string.IsNullOrWhiteSpace(jti)
                && await revokedTokenStore.IsRevokedAsync(jti, ct))
                return UnauthorizedWithChallenge();

            // Userinfo returns whatever claims the access token carried that look user-identifying.
            // We do not re-engage the subject resolver — the token was minted from a resolved subject
            // and relying parties should call userinfo for a snapshot, not fresh re-resolution.
            // Hosts that want dynamic userinfo can wrap this endpoint.
            //
            // Scope gating (OIDC Core §5.4), mirroring the Server host's equivalent. Every listed
            // claim used to be copied straight off the token with no reference to its `scope`, three
            // lines below a comment asserting the caller must hold `openid` — a check that was never
            // performed. A client granted only `openid` therefore received email, phone number and
            // group membership it had never been consented for, and the consent screen had told the
            // user otherwise.
            var scopeClaim = result.Claims.TryGetValue("scope", out var scObj) ? scObj?.ToString() ?? "" : "";
            var scopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (!scopes.Contains("openid", StringComparer.Ordinal))
                return UnauthorizedWithChallenge();

            var claims = new Dictionary<string, object?>();

            // sub is unconditional — it is the response's whole point (§5.3.2).
            if (result.Claims.TryGetValue("sub", out var subValue) && subValue is not null)
                claims["sub"] = subValue;

            CopyIfScoped("email", "email", "email_verified");
            CopyIfScoped("profile", "given_name", "family_name", "name", "org_id");
            CopyIfScoped("phone", "phone_number");
            CopyIfScoped("roles", "roles");
            CopyIfScoped("groups", "groups");

            void CopyIfScoped(string scope, params string[] claimNames)
            {
                if (!scopes.Contains(scope, StringComparer.Ordinal))
                    return;
                foreach (var name in claimNames)
                {
                    if (result.Claims.TryGetValue(name, out var value) && value is not null)
                        claims[name] = value;
                }
            }

            if (claims.Count == 0 || !claims.ContainsKey("sub"))
                return UnauthorizedWithChallenge();

            // The body is the subject's claim set. It gets the same no-store the token response gets.
            return JsonResults.NoStore(Results.Ok(claims));
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .RequireTls()
        .WithTags("OIDC");

        return app;
    }

    /// <summary>
    /// A 401 carrying the challenge RFC 6750 §3 requires, matching the Server host's userinfo.
    /// </summary>
    /// <remarks>
    /// Bare 401s told a resource client that its token was unacceptable but not that Bearer was even
    /// the scheme in use — the header is how a client learns which credential to present and whether
    /// to refresh rather than re-authenticate. The reason is deliberately generic: the endpoint
    /// distinguishes several causes internally (wrong token type, revoked, missing scope), and some
    /// of those would report on state the caller has not proved it may know.
    /// </remarks>
    internal static IResult UnauthorizedWithChallenge() => new BearerChallenge();

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

/// <summary>
/// RFC 6750 §2 access-token extraction for the userinfo endpoints of both hosts.
/// </summary>
/// <remarks>
/// OIDC Core §5.3.1 requires the token to be presented "per Section 2 of RFC 6750", which is the
/// Authorization header (§2.1) or the form-encoded body parameter (§2.2). Only the header was read,
/// so a client that POSTed <c>access_token</c> — the shape §2.2 exists for, and the one a strict
/// OIDC conformance suite exercises — was answered as if it had presented no credential at all.
/// §2 also requires a request that uses more than one method to be refused outright rather than
/// resolved by precedence: otherwise a proxy or a form-injecting page can smuggle a second token past
/// whatever inspected the first.
/// </remarks>
internal static class BearerToken
{
    public static async Task<(string? Token, IResult? Error)> ReadAsync(HttpContext httpContext, CancellationToken ct)
    {
        string? headerToken = null;
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            headerToken = authHeader["Bearer ".Length..].Trim();

        string? formToken = null;
        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync(ct);
            formToken = form["access_token"].FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(headerToken) && !string.IsNullOrWhiteSpace(formToken))
        {
            return (null, JsonResults.OAuthError(
                "invalid_request",
                "The access token must be presented once, by one method (RFC 6750 §2)."));
        }

        return (string.IsNullOrWhiteSpace(headerToken) ? formToken : headerToken, null);
    }
}

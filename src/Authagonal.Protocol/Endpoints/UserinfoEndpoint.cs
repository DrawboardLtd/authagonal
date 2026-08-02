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

            // 403 insufficient_scope, not 401 invalid_token: the token is valid and refreshing it will
            // return one with exactly these scopes again. Matches the Server host.
            if (!scopes.Contains("openid", StringComparer.Ordinal))
                return InsufficientScope("The access token does not carry the openid scope.");

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
    internal static IResult UnauthorizedWithChallenge() =>
        new BearerChallenge(StatusCodes.Status401Unauthorized, "invalid_token");

    /// <summary>
    /// A 403 saying the token is genuine but does not carry the scope this endpoint needs.
    /// </summary>
    /// <remarks>
    /// RFC 6750 §3.1 gives this its own code precisely so a client can tell it apart from
    /// <c>invalid_token</c>, and the distinction is the difference between two very different reactions:
    /// <c>invalid_token</c> means refresh and retry, <c>insufficient_scope</c> means asking for the same
    /// thing again will fail forever and a new authorization with a wider scope is required.
    /// <para>
    /// The two hosts disagreed here, which is worse than either answer alone: this one returned 401
    /// <c>invalid_token</c> for a missing <c>openid</c> scope, so a conforming client refreshed, got a token
    /// with exactly the same scopes, and looped; the Server host returned a bare 403 with no challenge at
    /// all, so a client could not tell a scope refusal from any other 403. Same condition, same product,
    /// two wrong answers.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Wraps the JSON error body rather than replacing it. The Server host already answered this case with
    /// <c>{"error":"insufficient_scope"}</c> and a caller may be reading it, so the header is added
    /// alongside — dropping the body to "improve conformance" would be a silent contract change.
    /// </remarks>
    internal static IResult InsufficientScope(string description) =>
        new ChallengeHeader(
            $"Bearer realm=\"userinfo\", error=\"insufficient_scope\", error_description=\"{QuotedString(description)}\"",
            JsonResults.OAuthError("insufficient_scope", description, statusCode: StatusCodes.Status403Forbidden));

    /// <summary>
    /// A 400 for a malformed request, still carrying the challenge.
    /// </summary>
    /// <remarks>
    /// RFC 6750 §3 attaches the header to the protected resource's refusal, not specifically to a 401 —
    /// and this one is reachable before any token has been evaluated (a request presenting credentials in
    /// two places at once). Added because closing the §2.2 dual-credential hole introduced a fresh
    /// challenge-less rejection on the very endpoint these findings exist to fix.
    /// </remarks>
    /// <remarks>
    /// Wraps the existing error BODY rather than replacing it: a caller that was reading
    /// <c>{"error":"invalid_request"}</c> keeps getting it, and the header is added alongside. Swapping
    /// the body for a bare header would have been a silent contract change made in the name of
    /// conformance.
    /// </remarks>
    internal static IResult InvalidRequestWithChallenge(string description) =>
        new ChallengeHeader(
            $"Bearer realm=\"userinfo\", error=\"invalid_request\", error_description=\"{QuotedString(description)}\"",
            JsonResults.OAuthError("invalid_request", description));

    /// <summary>
    /// A description rendered safe for the <c>quoted-string</c> of a <c>WWW-Authenticate</c> value.
    /// </summary>
    /// <remarks>
    /// The three call sites did <c>description.Replace("\"", "'")</c> and a comment claimed escaping "keeps
    /// that true of the next one somebody adds". It did not, in two ways that a fixed literal happens not to
    /// exercise:
    /// <list type="bullet">
    /// <item>
    /// A trailing backslash. RFC 9110 §5.6.4 makes <c>\</c> a quoted-pair, so a value ending in one escapes
    /// the closing quote and merges the header terminator into the value.
    /// </item>
    /// <item>
    /// CR or LF. Kestrel validates response headers and throws, so an endpoint whose entire purpose at that
    /// moment is to return a well-formed refusal answers 500 from an unhandled exception instead.
    /// </item>
    /// </list>
    /// <para>
    /// So: backslash and quote are escaped as quoted-pairs, and anything that cannot appear in a header at
    /// all — CR, LF, and the other control characters — becomes a space rather than being passed through.
    /// The guarantee the comment asserted now holds, which matters because the next caller will rely on it.
    /// </para>
    /// </remarks>
    internal static string QuotedString(string value)
    {
        var escaped = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or '"') escaped.Append('\\').Append(c);
            else if (char.IsControl(c)) escaped.Append(' ');
            else escaped.Append(c);
        }

        return escaped.ToString();
    }

    private sealed class ChallengeHeader(string challenge, IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.WWWAuthenticate = challenge;
            return inner.ExecuteAsync(httpContext);
        }
    }

    private sealed class BearerChallenge(int status, string error, string? description = null) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            // error_description is quoted-string per RFC 6750 §3, so a stray quote would split the
            // header. These are all fixed literals from this file; QuotedString is what makes the claim
            // that escaping "keeps that true of the next one somebody adds" actually true — see its remarks
            // for the two cases the old single-character replace did not cover.
            var challenge = $"Bearer realm=\"userinfo\", error=\"{error}\"";
            if (description is not null)
                challenge += $", error_description=\"{QuotedString(description)}\"";

            httpContext.Response.Headers.WWWAuthenticate = challenge;
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
            // A challenge-bearing 400, not JsonResults.OAuthError. That helper writes the OAuth
            // TOKEN-endpoint error body, which is the wrong shape here — userinfo is a protected
            // resource, and RFC 6750 §3 wants its refusals to carry WWW-Authenticate so the caller
            // learns the scheme. Both hosts route through this reader, so the omission was on both.
            return (null, UserinfoEndpoint.InvalidRequestWithChallenge(
                "The access token must be presented once, by one method (RFC 6750 §2)."));
        }

        return (string.IsNullOrWhiteSpace(headerToken) ? formToken : headerToken, null);
    }
}

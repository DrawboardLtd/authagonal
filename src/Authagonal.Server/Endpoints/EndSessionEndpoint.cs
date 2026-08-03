using System.Security.Cryptography;
using Authagonal.Core.Constants;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Localization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Server.Endpoints;

public static class EndSessionEndpoint
{
    public static IEndpointRouteBuilder MapEndSessionEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/connect/endsession", HandleAsync).AllowAnonymous().WithTags("OAuth");
        app.MapPost("/connect/endsession", HandleAsync).AllowAnonymous().WithTags("OAuth");
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        IClientStore clientStore,
        IGrantStore grantStore,
        Authagonal.Core.Services.IKeyManager keyManager,
        Authagonal.Core.Services.ITenantContext tenantContext,
        IHttpClientFactory httpClientFactory,
        IStringLocalizer<SharedMessages> localizer,
        CancellationToken ct)
    {
        var request = httpContext.Request;
        var hasForm = request.HasFormContentType;
        var idTokenHint = request.Query["id_token_hint"].FirstOrDefault()
            ?? (hasForm ? request.Form["id_token_hint"].FirstOrDefault() : null);
        var postLogoutRedirectUri = request.Query["post_logout_redirect_uri"].FirstOrDefault()
            ?? (hasForm ? request.Form["post_logout_redirect_uri"].FirstOrDefault() : null);
        var state = request.Query["state"].FirstOrDefault()
            ?? (hasForm ? request.Form["state"].FirstOrDefault() : null);

        // OIDC RP-Initiated Logout §2 defines client_id, and it was read nowhere — an RP that sent it
        // (the spec's recommended way to identify itself when id_token_hint is absent or expired) was
        // silently ignored. Where BOTH are present they must agree: accepting a client_id that names a
        // different client than the ID Token would let one RP borrow another's hint to have its own
        // post_logout_redirect_uri validated against the wrong registration.
        var clientIdParam = request.Query["client_id"].FirstOrDefault()
            ?? (hasForm ? request.Form["client_id"].FirstOrDefault() : null);

        // Get subject ID before signing out (for back-channel + front-channel logout)
        var subjectId = httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var sessionId = httpContext.User.FindFirst("sid")?.Value;

        // Validate id_token_hint ONCE, up front, for both of the things it decides: which client is asking
        // (so post_logout_redirect_uri can be checked against it) and whether the request provably came
        // from an RP that holds a token for THIS session.
        var hint = await ValidateIdTokenHintAsync(idTokenHint, keyManager, tenantContext.Issuer);

        // When both identifiers are present they must name the same client. Otherwise an RP could
        // pair its own client_id with another RP's ID Token so that its post_logout_redirect_uri was
        // validated against the wrong registration.
        if (!string.IsNullOrEmpty(clientIdParam) && hint?.ClientId is { } hintClient
            && !string.Equals(clientIdParam, hintClient, StringComparison.Ordinal))
        {
            return Results.BadRequest(new
            {
                error = "invalid_request",
                error_description = "client_id does not match the client named by id_token_hint.",
            });
        }

        // The client this logout is on behalf of: the ID Token's when we have one, otherwise the
        // explicit parameter. Previously only the ID Token could name it, so an RP whose token had
        // expired — the case the parameter exists for — could not have its redirect validated at all.
        var requestingClientId = hint?.ClientId ?? clientIdParam;

        // OIDC RP-Initiated Logout 1.0 §2: the OP MUST ask the End-User to confirm when no id_token_hint
        // was supplied, or when the supplied ID Token does not belong to the current OP session. That is
        // also the CSRF boundary, and it has to be, because the session cookie is SameSite=Lax — which
        // DOES accompany a cross-site top-level GET navigation. Without this check, any page could
        // navigate a signed-in user to /connect/endsession and silently end their session; §6 names this
        // as a denial-of-service vector for exactly that reason. A signed id_token whose `sub` matches the
        // session is unforgeable by a third party, so it stands in for the confirmation.
        var hintMatchesSession = hint is not null
            && !string.IsNullOrEmpty(subjectId)
            && string.Equals(hint.Subject, subjectId, StringComparison.Ordinal);

        if (!hintMatchesSession && !string.IsNullOrEmpty(subjectId) && !HasLogoutConfirmation(request, hasForm))
        {
            // No side effects on this branch — that is the whole point. Render a page whose only action is
            // a same-origin POST back here carrying the confirmation marker.
            return RenderConfirmation(httpContext, idTokenHint, postLogoutRedirectUri, state, localizer);
        }

        // Notify the relying parties and drop the session-bound grants — BEFORE the cookie goes, because the
        // grant lookup needs the subject and the sid comes off the live principal. Shared with
        // POST /api/auth/logout so the two sign-out paths cannot disagree about what "logged out" means. See
        // SessionTermination.
        var termination = await SessionTermination.NotifyAndRevokeAsync(
            httpContext, subjectId, sessionId, clientStore, grantStore, keyManager,
            tenantContext, httpClientFactory, ct);
        var frontChannelUris = termination.FrontChannelUris;

        // The upstream IdP's refresh token for this federated session is a live credential for
        // another provider, and nothing removed it — read the key off the principal before the cookie
        // that carries it is dropped.
        await Services.UpstreamSessionCleanup.RemoveForPrincipalAsync(httpContext, ct);

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // Resolve the final redirect target (if any) by validating post_logout_redirect_uri against the
        // client named by the (already validated) id_token_hint.
        string? finalRedirect = null;
        if (!string.IsNullOrWhiteSpace(postLogoutRedirectUri) && requestingClientId is { } resolvedClientId)
        {
            var client = await clientStore.GetAsync(resolvedClientId, ct);
            // Component-wise, matching the authorization endpoint. RP-Initiated Logout 1.0 §3 requires
            // an EXACT match against a registered value; a whole-string OrdinalIgnoreCase compare also
            // admitted case variants of the path and query, so a registration of
            // https://rp.example/Logout?tenant=Acme let through /logout?tenant=acme. The two matchers
            // had drifted — this one is the copy that was wrong.
            if (client is not null &&
                Authagonal.Protocol.Endpoints.AuthorizeRequestSupport.IsRedirectUriRegistered(
                    postLogoutRedirectUri, client.PostLogoutRedirectUris))
            {
                finalRedirect = string.IsNullOrWhiteSpace(state)
                    ? postLogoutRedirectUri
                    : $"{postLogoutRedirectUri}{(postLogoutRedirectUri.Contains('?') ? '&' : '?')}state={Uri.EscapeDataString(state)}";
            }
        }

        // If any clients registered front-channel logout URIs, render an HTML page with hidden iframes
        // so each client's logout endpoint is hit in the user's browser before redirecting/confirming.
        if (frontChannelUris.Count > 0)
        {
            var iframes = string.Join("\n", frontChannelUris.Select(u =>
                $"<iframe src=\"{System.Net.WebUtility.HtmlEncode(u)}\" style=\"display:none\"></iframe>"));

            string tail;
            if (!string.IsNullOrWhiteSpace(finalRedirect))
            {
                var escaped = System.Net.WebUtility.HtmlEncode(finalRedirect);
                // A meta refresh, not a script. The host's CSP has no script-src in its default branch, so
                // `default-src 'self'` governed scripts and this inline <script> was blocked outright —
                // the page hung on "Signed out" and never reached post_logout_redirect_uri. The <noscript>
                // fallback did not rescue it either: <noscript> renders when scripting is DISABLED, not
                // when CSP refuses a script. Meta refresh is not gated by any CSP directive.
                tail = $"<meta http-equiv=\"refresh\" content=\"2;url={escaped}\">";
            }
            else
            {
                var msg = System.Net.WebUtility.HtmlEncode(localizer["EndSession_SignedOut"].Value);
                tail = $"<p>{msg}</p>";
            }

            var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Signed out</title>" +
                       $"{(tail.StartsWith("<meta", StringComparison.Ordinal) ? tail : "")}</head>" +
                       $"<body>{iframes}{(tail.StartsWith("<meta", StringComparison.Ordinal) ? "" : tail)}</body></html>";

            // Per-response CSP admitting exactly the origins this page frames. The pipeline-wide policy
            // sets no frame-src, so cross-origin logout iframes fell back to `default-src 'self'` and every
            // one of them was blocked — while discovery advertised frontchannel_logout_supported: true.
            // Overwriting the header here works because the security-headers middleware stamps it BEFORE
            // calling next(), so the endpoint still owns the response.
            ApplyFrontChannelCsp(httpContext, frontChannelUris);

            return Results.Content(html, "text/html; charset=utf-8");
        }

        if (!string.IsNullOrWhiteSpace(finalRedirect))
            return Results.Redirect(finalRedirect);

        // No front-channel URIs and no validated redirect — just confirm logout.
        return TypedResults.Json(new MessageResponse { Message = localizer["EndSession_SignedOut"].Value }, AuthagonalJsonContext.Default.MessageResponse);
    }

    /// <summary>What a validated <c>id_token_hint</c> tells us: which client is asking, and about whom.</summary>
    private sealed record IdTokenHint(string? ClientId, string? Subject);

    /// <summary>
    /// Validates <c>id_token_hint</c> and returns both the client and the subject it names, or null if it
    /// is absent or does not verify.
    /// </summary>
    /// <remarks>
    /// This replaces an <c>ExtractClientId</c> that pulled only the client out. The <c>sub</c> was never
    /// read, so nothing ever compared the hint against the session it was being used to end — which is
    /// what OIDC RP-Initiated Logout §2 requires before skipping End-User confirmation. Lifetime is still
    /// not validated (the user is logging out, so an expired ID token is the normal case), which is exactly
    /// why the signature and issuer checks have to carry the weight.
    /// </remarks>
    private static async Task<IdTokenHint?> ValidateIdTokenHintAsync(
        string? idToken, Authagonal.Core.Services.IKeyManager keyManager, string issuer)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;

        try
        {
            var handler = new JsonWebTokenHandler();
            var keys = keyManager.GetSecurityKeys().Select(Authagonal.Protocol.Services.ProtocolSigningKeyOps.JwkToSecurityKey).ToList();

            // Awaited rather than blocked on. This endpoint is anonymous and unthrottled, so
            // .GetAwaiter().GetResult() pinned a thread-pool thread for the duration of every call —
            // and validation performs key resolution. Enough concurrent requests starve the pool,
            // which takes down every endpoint on the host, not just this one.
            var result = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidateIssuer = true,
                ValidateAudience = false,
                ValidateLifetime = false, // token may be expired
                ValidAlgorithms = ["ES256"],
                // An id_token, and nothing else this server signs.
                //
                // Every JWT the server issues shares one issuer and one ES256 key, so issuer + signature
                // alone accepted an ACCESS token or a back-channel LOGOUT token here — cross-JWT confusion.
                // That mattered because the hint is what decides `hintMatchesSession`, the only thing that
                // lets a request skip the confirmation interstitial, and the session cookie is SameSite=Lax,
                // which does ride a cross-site top-level GET. So a page holding any token this server minted
                // for the victim could log them out silently — and an access token is the one JWT an RP
                // routinely hands to third parties.
                //
                // Access tokens carry typ at+jwt (RFC 9068) and logout tokens typ logout+jwt; the id_token
                // descriptor sets no TokenType, so its typ is JWT.
                ValidTypes = ["JWT"],
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true
            });

            if (!result.IsValid)
                return null;

            // Shape checks behind the typ pin, because the typ header is one line away from being changed
            // by whoever mints the next token kind. `client_id` is an access-token claim (RFC 9068 §2.2)
            // and `events` a logout-token one (OIDC Back-Channel Logout §2.4); an id_token carries neither.
            if (result.Claims.ContainsKey("client_id") || result.Claims.ContainsKey("events"))
                return null;

            // The audience IS the client for an id_token. It used to prefer `client_id` — the claim only a
            // non-id_token has — and a hint naming no audience at all was accepted with a null client.
            var clientId = SingleAudience(result.Claims);
            if (string.IsNullOrWhiteSpace(clientId))
                return null;

            var subject = result.Claims.TryGetValue("sub", out var sub) ? sub?.ToString() : null;

            return new IdTokenHint(clientId, subject);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The single client named by <c>aud</c>, or null when the token names none or several.
    /// </summary>
    /// <remarks>
    /// A multi-audience token is not a hint about which RP is asking, and this value selects the RP whose
    /// <c>post_logout_redirect_uri</c> the request is allowed to be sent back to. <c>aud</c> arrives as a
    /// string for one audience and a collection for many, so a plain <c>ToString()</c> on the claim yielded
    /// <c>System.String[]</c> — a client id that matches nothing, silently.
    /// </remarks>
    private static string? SingleAudience(IDictionary<string, object> claims)
    {
        if (!claims.TryGetValue("aud", out var aud) || aud is null) return null;

        if (aud is string single) return single;

        if (aud is System.Collections.IEnumerable many and not string)
        {
            string? only = null;
            foreach (var item in many)
            {
                if (item?.ToString() is not { Length: > 0 } value) continue;
                if (only is not null) return null; // more than one audience
                only = value;
            }
            return only;
        }

        return aud.ToString();
    }

    /// <summary>Purpose string for the confirmation token's protector. Changing it invalidates outstanding tokens.</summary>
    private const string ConfirmationProtectorPurpose = "Authagonal.EndSession.Confirm.v1";

    /// <summary>How long a rendered confirmation page stays actionable.</summary>
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(15);

    private static ITimeLimitedDataProtector ConfirmationProtector(HttpContext httpContext) =>
        httpContext.RequestServices
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(ConfirmationProtectorPurpose)
            .ToTimeLimitedDataProtector();

    /// <summary>
    /// True when the request carries a confirmation token this server issued for THIS session.
    /// </summary>
    /// <remarks>
    /// Session-bound rather than a bare marker field, so it cannot be replayed against another user and
    /// cannot be fabricated by a page that merely knows the parameter name. The token is minted only by
    /// <see cref="RenderConfirmation"/>, which is reached only by a request that already carried the
    /// session cookie.
    /// </remarks>
    private static bool HasLogoutConfirmation(HttpRequest request, bool hasForm)
    {
        if (!HttpMethods.IsPost(request.Method)) return false;

        var supplied = (hasForm ? request.Form["logout_confirm"].FirstOrDefault() : null)
            ?? request.Query["logout_confirm"].FirstOrDefault();
        if (string.IsNullOrEmpty(supplied)) return false;

        var subjectId = request.HttpContext.User.FindFirst("sub")?.Value
            ?? request.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(subjectId)) return false;

        try
        {
            var payload = ConfirmationProtector(request.HttpContext).Unprotect(supplied);
            return string.Equals(payload, ConfirmationPayload(request.HttpContext, subjectId), StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            // Tampered, expired, or minted under a different key ring.
            return false;
        }
    }

    private static string ConfirmationPayload(HttpContext httpContext, string subjectId) =>
        $"{subjectId}|{httpContext.User.FindFirst("sid")?.Value}";

    /// <summary>
    /// The confirmation interstitial. Renders no-side-effect HTML whose only action is a same-origin POST
    /// back to this endpoint, carrying the original request's parameters and a session-bound token.
    /// </summary>
    private static IResult RenderConfirmation(
        HttpContext httpContext,
        string? idTokenHint,
        string? postLogoutRedirectUri,
        string? state,
        IStringLocalizer<SharedMessages> localizer)
    {
        var subjectId = httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;

        var token = ConfirmationProtector(httpContext)
            .Protect(ConfirmationPayload(httpContext, subjectId), ConfirmationLifetime);

        static string Enc(string? v) => System.Net.WebUtility.HtmlEncode(v ?? string.Empty);

        var hidden = new System.Text.StringBuilder();
        hidden.Append($"<input type=\"hidden\" name=\"logout_confirm\" value=\"{Enc(token)}\">");
        if (!string.IsNullOrWhiteSpace(idTokenHint))
            hidden.Append($"<input type=\"hidden\" name=\"id_token_hint\" value=\"{Enc(idTokenHint)}\">");
        if (!string.IsNullOrWhiteSpace(postLogoutRedirectUri))
            hidden.Append($"<input type=\"hidden\" name=\"post_logout_redirect_uri\" value=\"{Enc(postLogoutRedirectUri)}\">");
        if (!string.IsNullOrWhiteSpace(state))
            hidden.Append($"<input type=\"hidden\" name=\"state\" value=\"{Enc(state)}\">");

        var prompt = Enc(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            localizer["EndSession_ConfirmPrompt"].Value,
            httpContext.RequestServices.GetRequiredService<Authagonal.Core.Services.ITenantContext>().Issuer));

        var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Sign out</title></head><body>" +
                   $"<p>{prompt}</p>" +
                   "<form method=\"post\" action=\"/connect/endsession\">" +
                   hidden +
                   $"<button type=\"submit\">{Enc(localizer["EndSession_ConfirmButton"].Value)}</button>" +
                   "</form></body></html>";

        return Results.Content(html, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Replaces the pipeline-wide CSP for this response with one that admits the front-channel logout
    /// origins the body frames — and nothing else.
    /// </summary>
    private static void ApplyFrontChannelCsp(HttpContext httpContext, IEnumerable<string> frontChannelUris)
    {
        var origins = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var uri in frontChannelUris)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                origins.Add(parsed.GetLeftPart(UriPartial.Authority));
        }

        var frameSrc = origins.Count > 0 ? string.Join(' ', origins) : "'none'";
        httpContext.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
            $"script-src 'none'; object-src 'none'; frame-ancestors 'none'; frame-src {frameSrc}";
    }

    // One implementation, in SessionTermination — this was a second copy of it.
    private static string CreateBackChannelLogoutToken(
        string issuer, string clientId, string subjectId, string? sessionId,
        Authagonal.Core.Services.IKeyManager keyManager)
        => SessionTermination.CreateBackChannelLogoutToken(
            issuer, clientId, subjectId, sessionId, keyManager);
}

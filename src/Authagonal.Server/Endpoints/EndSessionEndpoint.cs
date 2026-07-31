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

        // Collect front-channel logout URIs before signing out (grant lookup needs subject)
        var frontChannelUris = new List<string>();
        if (!string.IsNullOrEmpty(subjectId))
        {
            try
            {
                var grants = await grantStore.GetBySubjectAsync(subjectId);
                foreach (var clientIdGrant in grants.Select(g => g.ClientId).Distinct())
                {
                    var c = await clientStore.GetAsync(clientIdGrant, ct);
                    if (c?.FrontChannelLogoutUri is null) continue;
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
            }
            catch { /* fall through */ }
        }

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // Back-channel logout. Resolve everything that needs the request's tenant scope NOW — grants,
        // clients, and the SIGNED logout tokens — because these stores are per-tenant and bind to the
        // request's tenant context; a background scope has no tenant, so its store resolution throws
        // (which is why the previous fire-and-forget-with-a-fresh-scope silently emitted nothing). Only
        // the HTTP POSTs run in the background — they need just the singleton IHttpClientFactory and the
        // already-built tokens, no tenant scope.
        if (!string.IsNullOrEmpty(subjectId))
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BackChannelLogout");
            List<(string Uri, string Token)> notifications = [];
            try
            {
                var grants = await grantStore.GetBySubjectAsync(subjectId);
                foreach (var clientIdGrant in grants.Select(g => g.ClientId).Distinct())
                {
                    var c = await clientStore.GetAsync(clientIdGrant, ct);
                    if (c?.BackChannelLogoutUri is null) continue;
                    var tokenSid = c.BackChannelLogoutSessionRequired ? sessionId : null;
                    notifications.Add((c.BackChannelLogoutUri,
                        CreateBackChannelLogoutToken(tenantContext.Issuer, clientIdGrant, subjectId, tokenSid, keyManager)));
                }

                // Session-bound grants only. This was RemoveAllBySubjectAsync, whose contract is EVERY grant
                // for the subject: it deleted the user's recorded `consent` and `agent_consent` records and
                // every pending approval along with the tokens. Ending a session is not revoking consent —
                // the user has a separate Authorized Apps page for that — so logging out silently discarded
                // preferences the user never asked to discard, and re-prompted them at every client.
                await grantStore.RemoveBySubjectAsync(subjectId, PersistedGrantTypes.SessionBound, ct: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Back-channel logout preparation failed for subject {SubjectId}", subjectId);
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
                            await client.PostAsync(uri, new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }));
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Back-channel logout POST failed for {Uri}", uri);
                        }
                    }
                });
        }

        // Resolve the final redirect target (if any) by validating post_logout_redirect_uri against the
        // client named by the (already validated) id_token_hint.
        string? finalRedirect = null;
        if (!string.IsNullOrWhiteSpace(postLogoutRedirectUri) && requestingClientId is { } resolvedClientId)
        {
            var client = await clientStore.GetAsync(resolvedClientId, ct);
            if (client is not null &&
                client.PostLogoutRedirectUris.Contains(postLogoutRedirectUri, StringComparer.OrdinalIgnoreCase))
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
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true
            });

            if (!result.IsValid)
                return null;

            var clientId = result.Claims.TryGetValue("client_id", out var cid) ? cid?.ToString()
                : result.Claims.TryGetValue("aud", out var aud) ? aud?.ToString()
                : null;
            var subject = result.Claims.TryGetValue("sub", out var sub) ? sub?.ToString() : null;

            return new IdTokenHint(clientId, subject);
        }
        catch
        {
            return null;
        }
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

    private static string CreateBackChannelLogoutToken(
        string issuer, string clientId, string subjectId, string? sessionId,
        Authagonal.Core.Services.IKeyManager keyManager)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subjectId,
            // Must be JSON-serializable — an anonymous type throws IDX11025 at CreateToken (silent RP-notify failure).
            ["events"] = new Dictionary<string, object>
            {
                ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>()
            },
            ["jti"] = Guid.NewGuid().ToString("N")
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
            // ample for the back-channel POST and bounds the replay window.
            Expires = DateTime.UtcNow.AddMinutes(2),
            // Make the kind explicit, so this token cannot be presented anywhere an access token is
            // expected — the token-exchange endpoint accepted exactly this token as a subject_token.
            TokenType = TokenTypes.LogoutJwt,
            Claims = claims,
            SigningCredentials = keyManager.GetSigningCredentials()
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

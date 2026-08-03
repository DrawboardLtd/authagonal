using Authagonal.Core.Services;
using Authagonal.Server.Endpoints;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Server.Services;

/// <summary>
/// Refuses a state-changing interactive POST that did not come from the IdP's own pages.
/// </summary>
/// <remarks>
/// Granting standing agent consent and resolving a just-in-time approval both change what an agent
/// may do on the user's behalf — the first, when <c>authority</c> is omitted, grants the agent's FULL
/// ceiling — and the only credential either required was the ambient session cookie. No confirmation
/// step, no re-authentication, no antiforgery token, and no binding to a pending request.
/// <para>
/// SameSite=Lax stops the cookie riding a genuinely cross-SITE request, so this was never reachable
/// from an arbitrary third-party domain. It does not stop a cross-ORIGIN request from a sibling host,
/// and idp.acme.com beside app.acme.com is the normal deployment shape — so any XSS or hostile script
/// on any same-site origin could grant an agent full authority over the visiting user, silently.
/// </para>
/// <para>
/// Checked on the request rather than solved with a token because these endpoints take a JSON body
/// from a first-party SPA: an Origin that is absent or equal to this server's is the same thing an
/// antiforgery token would prove, without a round-trip the login app would have to be taught.
/// </para>
/// </remarks>
internal static class InteractiveOriginGuard
{
    /// <summary>
    /// Applies <see cref="Check"/> to every state-changing route in a group, so a route cannot be added
    /// without it.
    /// </summary>
    /// <remarks>
    /// Written as a filter because the per-call-site version did not hold. It was applied at four sites and
    /// the CHANGELOG asserted those were "the only ones missing it" — while <c>POST /consent</c>, the PRIMARY
    /// OAuth consent-granting POST, had none, nor did the whole <c>/api/auth/mfa/*</c> setup group, logout,
    /// the profile PATCH, or the session-revocation routes. A guard you have to remember to call is a guard
    /// that documents its own coverage wrongly.
    /// <para>
    /// Safe reads are skipped so a GET-heavy group can carry the filter wholesale: GET, HEAD and OPTIONS
    /// change nothing, and OPTIONS in particular must survive for CORS preflight to work at all.
    /// </para>
    /// <para>
    /// This is not redundant with CORS. A body-less <c>POST</c> with <c>credentials:'include'</c> sends no
    /// <c>Content-Type</c>, which makes it a CORS-SIMPLE request: the browser issues no preflight and the
    /// request EXECUTES regardless of policy — only the response is withheld. So
    /// <c>POST /api/auth/mfa/recovery/generate</c> (no bound body, antiforgery disabled) was reachable
    /// cross-origin from anywhere, whatever the CORS configuration said.
    /// </para>
    /// </remarks>
    public static TBuilder RequireOwnOrigin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var method = context.HttpContext.Request.Method;
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
                return await next(context);

            return Check(context.HttpContext) is { } refusal ? refusal : await next(context);
        });

        return builder;
    }

    /// <summary>
    /// Null when the request may proceed, or the result to return instead.
    /// </summary>
    public static IResult? Check(HttpContext httpContext)
    {
        // Sec-Fetch-Site is the browser's own answer and cannot be set by script. When it says the
        // request came from somewhere else, that settles it before any string comparison.
        var fetchSite = httpContext.Request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0 && !string.Equals(fetchSite, "same-origin", StringComparison.Ordinal))
            return Refuse();

        var origin = httpContext.Request.Headers.Origin.ToString();

        // No Origin at all is a same-origin non-browser caller (curl, a native app, a server-side
        // integration). Browsers attach it to every cross-origin request and to every POST, so its
        // absence cannot be forged by a page.
        if (origin.Length == 0)
            return null;

        return IsOwnOrigin(httpContext, origin) ? null : Refuse();
    }

    private static bool IsOwnOrigin(HttpContext httpContext, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed))
            return false;

        var candidate = parsed.GetLeftPart(UriPartial.Authority);

        // The request's own host, so a multi-tenant deployment does not need every tenant's issuer
        // enumerated here.
        var request = httpContext.Request;
        if (string.Equals(candidate, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))
            return true;

        // …and the configured issuer, which is what a deployment behind a proxy that rewrites Host
        // will actually be reached at.
        var issuer = httpContext.RequestServices.GetService<ITenantContext>()?.Issuer;
        return !string.IsNullOrEmpty(issuer)
            && Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
            && string.Equals(candidate, issuerUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    private static IResult Refuse() =>
        TypedResults.Json(
            new ErrorInfoResponse
            {
                Error = "invalid_origin",
                ErrorDescription = "This action must be taken from the identity provider's own pages.",
            },
            AuthagonalJsonContext.Default.ErrorInfoResponse,
            statusCode: 403);
}

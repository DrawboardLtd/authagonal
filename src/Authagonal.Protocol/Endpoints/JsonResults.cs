using Microsoft.AspNetCore.Http;

namespace Authagonal.Protocol.Endpoints;

internal static class JsonResults
{
    /// <summary>
    /// An OAuth error body (RFC 6749 §5.2), always with the token-endpoint caching headers.
    /// </summary>
    /// <remarks>
    /// Set on the error shape too, not only on the success shape: these bodies are emitted by the
    /// token, PAR, revocation and device endpoints, they name a client and its failure mode, and a
    /// shared forward proxy applying heuristic freshness to a 400 will happily replay one response
    /// to a different caller. The success path's obligation is RFC 6749 §5.1's MUST; this is the
    /// same header on the same endpoints for the same reason.
    /// </remarks>
    public static IResult OAuthError(string error, string description, int statusCode = 400)
        => NoStore(TypedResults.Json(
            new OAuthErrorResponse { Error = error, ErrorDescription = description },
            ProtocolJsonContext.Default.OAuthErrorResponse,
            statusCode: statusCode));

    /// <summary>
    /// A client-authentication failure with the challenge RFC 6749 §5.2 requires.
    /// </summary>
    /// <remarks>
    /// "If the client attempted to authenticate via the Authorization request header field, the
    /// authorization server MUST respond with an HTTP 401 … and include the WWW-Authenticate response
    /// header field matching the authentication scheme used by the client." Without it a client
    /// cannot tell an authentication failure from any other 401 — which is the one distinction the
    /// header exists to make, and the reason an HTTP stack knows to re-present credentials rather
    /// than surface the error to the user. Every endpoint that authenticates a client owes the same
    /// header; it used to be emitted only by the token endpoint.
    /// </remarks>
    public static IResult UnauthorizedClient(string error, string description, string realm)
        => new ClientChallenge(error, description, realm);

    /// <summary>
    /// Wraps a result so it carries <c>Cache-Control: no-store</c> and <c>Pragma: no-cache</c>.
    /// </summary>
    /// <remarks>
    /// RFC 6749 §5.1 states it as a MUST for the token response, and the same reasoning covers every
    /// body that carries a credential or the subject's claims — userinfo (PII) and introspection
    /// (token state) included. Without it an intermediary is entitled to apply heuristic freshness to
    /// a 200 with no explicit expiry and serve one caller's tokens or claims to the next.
    /// </remarks>
    public static IResult NoStore(IResult inner) => new NoStoreResult(inner);

    private sealed class NoStoreResult(IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers.Pragma = "no-cache";
            return inner.ExecuteAsync(httpContext);
        }
    }

    private sealed class ClientChallenge(string error, string description, string realm) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.WWWAuthenticate =
                $"Basic realm=\"{realm}\", error=\"{error}\"";
            return OAuthError(error, description, statusCode: StatusCodes.Status401Unauthorized)
                .ExecuteAsync(httpContext);
        }
    }
}

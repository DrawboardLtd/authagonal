using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Authagonal.Protocol.Endpoints;

/// <summary>
/// The RFC 6749 §3.1/§3.2 TLS requirement, carried by the endpoints themselves.
/// </summary>
/// <remarks>
/// Authagonal.Server enforces this as middleware, because it owns its pipeline. This package does not:
/// an embedding host calls <c>MapAuthagonalProtocolEndpoints</c> (or maps an individual endpoint) into a
/// pipeline it composed itself, so there is no middleware slot to occupy and anything the host has to
/// remember to call is a control that will sometimes be missing. Authagonal.Protocol ships on nuget.org,
/// so "sometimes" is not hypothetical.
/// <para>
/// An endpoint filter travels with the route instead. However the host composes its pipeline, and whether
/// it maps the whole surface or one endpoint at a time, the token endpoint refuses to complete a code
/// exchange in cleartext.
/// </para>
/// <para>
/// It reads <see cref="HttpRequest.IsHttps"/>, which is the scheme after the host's forwarded-header
/// middleware has run, if it registered any — the filter executes after routing, so that ordering is
/// automatic rather than something the host has to get right. A host that terminates TLS at a proxy and
/// registers no forwarded-header handling will see plaintext and be refused, which is the correct answer:
/// its cookies are not being marked Secure and its generated absolute URLs are wrong for the same reason.
/// </para>
/// <para>
/// That refusal is also the one an embedder is most likely to meet on upgrade, and the least likely to
/// diagnose from a bare <c>invalid_request</c>, because the deployment genuinely is on TLS — a proxy in
/// front of it terminated the connection and said so in a header nothing was listening for. So when the
/// request carries <c>X-Forwarded-Proto: https</c> and the scheme is still http, the refusal names that
/// specifically. The remedy belongs to the host: register <c>UseForwardedHeaders</c> with the terminating
/// proxy in <c>KnownProxies</c>/<c>KnownNetworks</c>. An empty trust set is not the remedy — the framework
/// reads it as "every caller is a trusted proxy", which hands the scheme to whoever asks for it.
/// </para>
/// </remarks>
internal static class TransportSecurity
{
    public static RouteHandlerBuilder RequireTls(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.Request.IsHttps)
            {
                var options = context.HttpContext.RequestServices
                    .GetRequiredService<IOptions<AuthagonalProtocolOptions>>().Value;

                if (!options.AllowInsecureHttp)
                {
                    var claimedHttps = context.HttpContext.Request.Headers["X-Forwarded-Proto"]
                        .Any(v => string.Equals(v, "https", StringComparison.OrdinalIgnoreCase));

                    return JsonResults.OAuthError(
                        "invalid_request",
                        claimedHttps
                            ? "TLS is required at the OAuth endpoints (RFC 6749 sections 3.1 and 3.2). This " +
                              "request carried X-Forwarded-Proto: https, but the host pipeline did not apply " +
                              "it — register UseForwardedHeaders with the terminating proxy in KnownProxies " +
                              "or KnownNetworks (an empty trust set honours the header from any caller and is " +
                              "not a safe substitute), or set AllowInsecureHttp on AuthagonalProtocolOptions " +
                              "for a development host."
                            : "TLS is required at the OAuth endpoints (RFC 6749 sections 3.1 and 3.2). Use " +
                              "https, or set AllowInsecureHttp on AuthagonalProtocolOptions for a development " +
                              "host.");
                }
            }

            return await next(context);
        });
}

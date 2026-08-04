using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Server.Services;

/// <summary>
/// Whether cookies this server sets should carry <c>Secure</c>.
/// </summary>
/// <remarks>
/// One decision, shared by every cookie, because the session cookie made it correctly and the three
/// hand-built ones each made it differently. Its configuration states the rule outright:
/// <c>SameAsRequest</c> "looks equivalent behind a TLS-terminating proxy, but it depends on
/// <c>X-Forwarded-Proto</c> arriving and being trusted: a misconfigured ingress, a health probe on plain HTTP,
/// or a proxy that drops the header yields a NON-Secure cookie… The failure is silent." So the session cookie
/// uses <c>CookieSecurePolicy.Always</c> unless <c>Authentication:AllowInsecureCookie</c> is set, and
/// <c>CookiePolicyConfigurationTests</c> pins that against the production wiring.
/// <para>
/// The other three took <c>Secure</c> from <c>Request.IsHttps</c> — precisely the posture that configuration
/// rejects — and one of them carries the MFA-enrolment token, which is a full sign-in credential: with TLS
/// terminated at an ingress not declared in <c>ForwardedHeaders:KnownProxies</c>, <c>IsHttps</c> is false for
/// every request and that cookie went out non-Secure while the session cookie beside it did not.
/// </para>
/// <para>
/// Resolved from the request rather than captured at startup so a static cookie-options helper needs no new
/// constructor parameter, which is what kept these three from sharing the decision in the first place.
/// </para>
/// </remarks>
internal static class CookieSecurity
{
    internal static bool Secure(HttpContext httpContext) =>
        !httpContext.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("Authentication:AllowInsecureCookie", false);
}

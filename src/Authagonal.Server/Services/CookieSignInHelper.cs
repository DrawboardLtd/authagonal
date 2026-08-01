using System.Security.Claims;
using Authagonal.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Authagonal.Server.Services;

public static class CookieSignInHelper
{
    /// <summary>Cookie claim asserting the session completed multi-factor authentication (or was
    /// established via an external IdP that owns authentication). Required at /connect/authorize for
    /// MFA-enrolled users.</summary>
    public const string MfaAuthenticatedClaim = "mfa_authenticated";

    /// <summary>Cookie claim (Unix seconds) recording when this session was established by an actual
    /// authentication. Set on every real sign-in and never bumped by sliding-cookie renewal, so
    /// /connect/authorize can honor prompt=login by requiring a session newer than the request.</summary>
    public const string AuthTimeClaim = "auth_time";

    /// <summary>
    /// Authentication-property key recording when this session began, in Unix seconds.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>Properties.IssuedUtc</c>, which sliding renewal rewrites on every refresh —
    /// making it useless as an absolute-lifetime reference. This value is written once at sign-in and
    /// never touched again, so the 7-day cap can actually fire.
    /// </remarks>
    public const string SessionStartedProperty = "session_started";

    /// <summary>Reads <see cref="SessionStartedProperty"/>, or null when the session predates it.</summary>
    public static DateTimeOffset? SessionStartedAt(AuthenticationProperties? properties)
    {
        var raw = properties?.GetString(SessionStartedProperty);
        return long.TryParse(raw, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    /// <summary>
    /// Stamps <see cref="SessionStartedProperty"/> with now, unless it is already set.
    /// </summary>
    /// <remarks>
    /// Idempotent because the absolute cap is only a cap while this value is written ONCE. Re-stamping on
    /// a renewal, or on a second sign-in that reuses an existing property bag, would slide the 7-day
    /// deadline forward on every request — which is exactly the defect that made the cap dead code when
    /// it read <c>Properties.IssuedUtc</c>.
    /// </remarks>
    public static void MarkSessionStart(AuthenticationProperties properties)
    {
        if (SessionStartedAt(properties) is null)
            properties.SetString(SessionStartedProperty, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    }

    public static async Task SignInAsync(HttpContext httpContext, AuthUser user, bool mfaAuthenticated = false)
    {
        var name = $"{user.FirstName} {user.LastName}".Trim();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new("sub", user.Id),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? user.Email : name),
            new("security_stamp", user.SecurityStamp ?? ""),
            new("sid", Guid.NewGuid().ToString("N")),
            new(AuthTimeClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };

        if (mfaAuthenticated)
            claims.Add(new Claim(MfaAuthenticatedClaim, "true"));

        if (!string.IsNullOrWhiteSpace(user.OrganizationId))
            claims.Add(new Claim("org_id", user.OrganizationId));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var properties = new AuthenticationProperties();
        MarkSessionStart(properties);

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
    }

    public static string GetDisplayName(AuthUser user)
    {
        return $"{user.FirstName} {user.LastName}".Trim();
    }
}

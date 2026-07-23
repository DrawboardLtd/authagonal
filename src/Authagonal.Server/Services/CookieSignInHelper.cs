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

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    }

    public static string GetDisplayName(AuthUser user)
    {
        return $"{user.FirstName} {user.LastName}".Trim();
    }
}

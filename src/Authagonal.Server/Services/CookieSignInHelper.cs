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
            new("sid", Guid.NewGuid().ToString("N"))
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

using System.Security.Claims;
using Authagonal.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Authagonal.Server.Services;

public static class CookieSignInHelper
{
    public static async Task SignInAsync(HttpContext httpContext, AuthUser user)
    {
        var name = $"{user.FirstName} {user.LastName}".Trim();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new("sub", user.Id),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? user.Email : name),
            new("security_stamp", user.SecurityStamp ?? "")
        };

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

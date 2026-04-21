using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

namespace Authagonal.OidcProvider;

internal static class OidcEndpointHandlers
{
    public static async Task HandleAuthorizeAsync(HttpContext http)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("Not an OpenIddict authorize request.");

        var opts = http.RequestServices.GetRequiredService<IOptions<AuthagonalOidcProviderOptions>>().Value;

        var auth = await http.AuthenticateAsync(opts.AuthenticationScheme);
        if (!auth.Succeeded || auth.Principal?.Identity?.IsAuthenticated != true)
        {
            await http.ChallengeAsync(opts.AuthenticationScheme, new AuthenticationProperties
            {
                RedirectUri = http.Request.Path + http.Request.QueryString,
            });
            return;
        }

        var resolver = http.RequestServices.GetRequiredService<IOidcSubjectResolver>();
        var scopes = request.GetScopes();
        var resources = request.GetResources();

        var subject = await resolver.ResolveAsync(
            auth.Principal,
            new OidcSubjectResolutionContext(
                ClientId: request.ClientId ?? "",
                RequestedScopes: scopes,
                RequestedResources: resources),
            http.RequestAborted);

        if (subject is null)
        {
            await http.ForbidAsync(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] =
                        OpenIddictConstants.Errors.LoginRequired,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                        "The subject resolver rejected the request.",
                }));
            return;
        }

        var identity = BuildIdentity(subject);
        identity.SetScopes(scopes);
        identity.SetResources(resources);
        foreach (var claim in identity.Claims)
        {
            claim.SetDestinations(DestinationsFor(claim, scopes));
        }

        var principal = new ClaimsPrincipal(identity);
        await http.SignInAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, principal);
    }

    public static async Task HandleTokenAsync(HttpContext http)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("Not an OpenIddict token request.");

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            var auth = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            if (!auth.Succeeded || auth.Principal is null)
            {
                await http.ForbidAsync(
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                    new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] =
                            OpenIddictConstants.Errors.InvalidGrant,
                    }));
                return;
            }

            await http.SignInAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, auth.Principal);
            return;
        }

        await http.ForbidAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] =
                    OpenIddictConstants.Errors.UnsupportedGrantType,
            }));
    }

    public static async Task HandleUserinfoAsync(HttpContext http)
    {
        var auth = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (!auth.Succeeded || auth.Principal is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var sub = auth.Principal.GetClaim(OpenIddictConstants.Claims.Subject);
        if (string.IsNullOrEmpty(sub))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var claims = new Dictionary<string, object>
        {
            [OpenIddictConstants.Claims.Subject] = sub,
        };

        var email = auth.Principal.GetClaim(OpenIddictConstants.Claims.Email);
        if (!string.IsNullOrEmpty(email))
        {
            claims[OpenIddictConstants.Claims.Email] = email;
        }

        var name = auth.Principal.GetClaim(OpenIddictConstants.Claims.Name);
        if (!string.IsNullOrEmpty(name))
        {
            claims[OpenIddictConstants.Claims.Name] = name;
        }

        await http.Response.WriteAsJsonAsync(claims);
    }

    private static ClaimsIdentity BuildIdentity(OidcSubject subject)
    {
        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity.SetClaim(OpenIddictConstants.Claims.Subject, subject.SubjectId);

        if (!string.IsNullOrEmpty(subject.Email))
        {
            identity.SetClaim(OpenIddictConstants.Claims.Email, subject.Email);
            identity.SetClaim(OpenIddictConstants.Claims.EmailVerified, subject.EmailVerified);
        }

        if (!string.IsNullOrEmpty(subject.Name))
        {
            identity.SetClaim(OpenIddictConstants.Claims.Name, subject.Name);
        }

        if (!string.IsNullOrEmpty(subject.GivenName))
        {
            identity.SetClaim(OpenIddictConstants.Claims.GivenName, subject.GivenName);
        }

        if (!string.IsNullOrEmpty(subject.FamilyName))
        {
            identity.SetClaim(OpenIddictConstants.Claims.FamilyName, subject.FamilyName);
        }

        if (subject.Roles is { Count: > 0 })
        {
            identity.SetClaims(OpenIddictConstants.Claims.Role, [.. subject.Roles]);
        }

        if (subject.AdditionalClaims is not null)
        {
            foreach (var (type, value) in subject.AdditionalClaims)
            {
                identity.SetClaim(type, value);
            }
        }

        return identity;
    }

    private static IEnumerable<string> DestinationsFor(Claim claim, IReadOnlyCollection<string> scopes)
    {
        yield return OpenIddictConstants.Destinations.AccessToken;

        switch (claim.Type)
        {
            case OpenIddictConstants.Claims.Name:
            case OpenIddictConstants.Claims.GivenName:
            case OpenIddictConstants.Claims.FamilyName:
                if (scopes.Contains(OpenIddictConstants.Scopes.Profile))
                {
                    yield return OpenIddictConstants.Destinations.IdentityToken;
                }
                break;

            case OpenIddictConstants.Claims.Email:
            case OpenIddictConstants.Claims.EmailVerified:
                if (scopes.Contains(OpenIddictConstants.Scopes.Email))
                {
                    yield return OpenIddictConstants.Destinations.IdentityToken;
                }
                break;

            case OpenIddictConstants.Claims.Subject:
                yield return OpenIddictConstants.Destinations.IdentityToken;
                break;
        }
    }
}

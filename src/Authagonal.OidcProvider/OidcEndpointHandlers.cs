using System.Globalization;
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
    /// <summary>
    /// Private claim carrying the session cap as Unix seconds. Persisted through
    /// authorization code → refresh token rotations so the cap cannot be extended by
    /// refreshing. Destinations are empty — this claim never lands on an access or
    /// id token.
    /// </summary>
    internal const string SessionMaxExpClaim = "session_max_exp";

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

        var result = await resolver.ResolveAsync(
            auth.Principal,
            new OidcSubjectResolutionContext(
                ClientId: request.ClientId ?? "",
                RequestedScopes: scopes,
                RequestedResources: resources),
            http.RequestAborted);

        if (result is OidcSubjectResult.Rejected rejected)
        {
            await RejectAsync(http, rejected);
            return;
        }

        var subject = ((OidcSubjectResult.Allowed)result).Subject;

        var identity = BuildIdentity(subject);
        identity.SetScopes(scopes);
        identity.SetResources(resources);
        foreach (var claim in identity.Claims)
        {
            claim.SetDestinations(DestinationsFor(claim, scopes));
        }

        var principal = new ClaimsPrincipal(identity);
        ApplySessionCap(principal, subject.SessionMaxExpiresAt);

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

            var principal = auth.Principal;
            var cap = ReadSessionCap(principal);

            // If the cap has already passed, refuse to rotate/issue — the federated
            // session this token chain was anchored to has ended.
            if (cap.HasValue && cap.Value <= DateTimeOffset.UtcNow)
            {
                await http.ForbidAsync(
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                    new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] =
                            OpenIddictConstants.Errors.InvalidGrant,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                            "The upstream session has ended.",
                    }));
                return;
            }

            ApplySessionCap(principal, cap);
            await http.SignInAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, principal);
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

    public static async Task HandleEndSessionAsync(HttpContext http)
    {
        var opts = http.RequestServices.GetRequiredService<IOptions<AuthagonalOidcProviderOptions>>().Value;

        await http.SignOutAsync(opts.AuthenticationScheme);

        // OpenIddict's sign-out result takes care of validating post_logout_redirect_uri
        // against the registered client and redirecting there if it's allowed.
        await http.SignOutAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            new AuthenticationProperties
            {
                RedirectUri = "/",
            });
    }

    private static async Task RejectAsync(HttpContext http, OidcSubjectResult.Rejected rejected)
    {
        var error = rejected.Reason switch
        {
            OidcRejection.LoginRequired => OpenIddictConstants.Errors.LoginRequired,
            OidcRejection.ConsentRequired => OpenIddictConstants.Errors.ConsentRequired,
            OidcRejection.AccountSelectionRequired => OpenIddictConstants.Errors.AccountSelectionRequired,
            OidcRejection.AccessDenied => OpenIddictConstants.Errors.AccessDenied,
            _ => OpenIddictConstants.Errors.LoginRequired,
        };

        await http.ForbidAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                    rejected.Description ?? "The subject resolver rejected the request.",
            }));
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

        if (subject.SessionMaxExpiresAt is { } cap)
        {
            // Stored as a claim with no token destinations, so it survives code →
            // refresh rotations via OpenIddict's principal round-trip but never leaks
            // into an access/id token.
            var unix = cap.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            identity.AddClaim(new Claim(SessionMaxExpClaim, unix));
        }

        return identity;
    }

    /// <summary>
    /// Clamps issued token lifetimes so no rotation — however many — can push a
    /// chain of tokens past <paramref name="cap"/>. Equivalent in spirit to the
    /// refresh-expiry clamp in <c>Authagonal.Server.TokenService</c>.
    /// </summary>
    private static void ApplySessionCap(ClaimsPrincipal principal, DateTimeOffset? cap)
    {
        if (cap is not { } deadline)
        {
            return;
        }

        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            // Zero or negative would be rejected outright; caller should have already
            // short-circuited. Defensive clamp to a tiny positive value in case we
            // somehow hit this path.
            remaining = TimeSpan.FromSeconds(1);
        }

        principal.SetAccessTokenLifetime(Min(principal.GetAccessTokenLifetime(), remaining));
        principal.SetIdentityTokenLifetime(Min(principal.GetIdentityTokenLifetime(), remaining));
        principal.SetRefreshTokenLifetime(Min(principal.GetRefreshTokenLifetime(), remaining));
        principal.SetAuthorizationCodeLifetime(Min(principal.GetAuthorizationCodeLifetime(), remaining));
    }

    private static DateTimeOffset? ReadSessionCap(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(SessionMaxExpClaim)?.Value;
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            return null;
        }
        return DateTimeOffset.FromUnixTimeSeconds(unix);
    }

    private static TimeSpan Min(TimeSpan? configured, TimeSpan remaining)
    {
        if (configured is null)
        {
            return remaining;
        }
        return configured.Value < remaining ? configured.Value : remaining;
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

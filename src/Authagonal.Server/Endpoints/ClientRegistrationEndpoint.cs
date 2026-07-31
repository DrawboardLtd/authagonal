using System.Security.Cryptography;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Endpoints;

/// <summary>
/// Dynamic Client Registration (RFC 7591). Allows client applications to register themselves
/// at runtime. Disabled by default — enable via <c>Auth:DynamicClientRegistrationEnabled</c>.
/// </summary>
public static class ClientRegistrationEndpoint
{
    public static IEndpointRouteBuilder MapClientRegistrationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/register", HandleAsync)
            .AllowAnonymous()
            .WithTags("OAuth");
        return app;
    }

    // Open self-service registration must not be able to mint machine-to-machine or interactive
    // grant types that bypass a user — only the code + refresh flow.
    private static readonly HashSet<string> RegistrableGrantTypes =
        new(["authorization_code", "refresh_token"], StringComparer.Ordinal);

    private static async Task<IResult> HandleAsync(
        ClientRegistrationRequest request,
        HttpContext httpContext,
        IClientStore clientStore,
        IScopeStore scopeStore,
        PasswordHasher passwordHasher,
        IRateLimiter rateLimiter,
        IConfiguration configuration,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        if (!authOptions.Value.DynamicClientRegistrationEnabled)
            return TypedResults.Json(
                new ErrorInfoResponse { Error = "not_supported", ErrorDescription = "Dynamic client registration is not enabled" },
                AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 403);

        // Rate-limit anonymous registration to prevent client-record flooding.
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (await rateLimiter.IsRateLimitedAsync($"dcr|{ip}", 10, TimeSpan.FromMinutes(60), ct))
            return TypedResults.Json(
                new ErrorInfoResponse { Error = "rate_limited", ErrorDescription = "Too many registration attempts. Try again later." },
                AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 429);

        var redirectUris = request.RedirectUris ?? [];
        var grantTypes = request.GrantTypes ?? ["authorization_code"];

        // Restrict to user-mediated grant types; never allow open DCR to register a
        // client_credentials/implicit/device client.
        foreach (var gt in grantTypes)
        {
            if (!RegistrableGrantTypes.Contains(gt))
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_client_metadata", ErrorDescription = $"grant_type '{gt}' may not be registered via dynamic client registration" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        }

        foreach (var uri in redirectUris)
        {
            // Require an absolute URI and reject script/data/file pseudo-schemes; https and native
            // custom schemes (mobile deep links) remain valid.
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
                parsed.Scheme is "javascript" or "data" or "vbscript" or "file")
            {
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_redirect_uri", ErrorDescription = $"Invalid redirect_uri: {uri}" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
            }

            // RFC 6749 §3.1.2 requires the redirection endpoint to carry no fragment, and the
            // fragment is where an implicit-style response would put a token — so a registered URI
            // with one is either a mistake or an attempt to shape where credentials land.
            if (!string.IsNullOrEmpty(parsed.Fragment))
            {
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_redirect_uri", ErrorDescription = $"redirect_uri must not contain a fragment: {uri}" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
            }

            // Cleartext http is refused except on loopback, which RFC 8252 §7.3 requires for native
            // apps. Anonymous registration accepting http://anywhere means an authorization code —
            // and with it the whole authorization — travels to an arbitrary host over a link any
            // on-path party can read and modify.
            if (parsed.Scheme == Uri.UriSchemeHttp && !parsed.IsLoopback)
            {
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_redirect_uri", ErrorDescription = $"redirect_uri must use https (http is permitted only for loopback): {uri}" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
            }
        }

        var needsRedirects = grantTypes.Any(g =>
            g == "authorization_code" || g == "implicit");
        if (needsRedirects && redirectUris.Count == 0)
        {
            return TypedResults.Json(
                new ErrorInfoResponse { Error = "invalid_client_metadata", ErrorDescription = "redirect_uris is required for the requested grant_types" },
                AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        }

        var authMethod = request.TokenEndpointAuthMethod ?? "client_secret_basic";
        var isPublicClient = authMethod == "none";

        var requestedScopes = string.IsNullOrWhiteSpace(request.Scope)
            ? new List<string>()
            : request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var builtInScopes = new HashSet<string>(["openid", "profile", "email", "offline_access"], StringComparer.Ordinal);
        var storeScopes = await scopeStore.ListAsync(ct);
        var knownScopes = new HashSet<string>(storeScopes.Select(s => s.Name), StringComparer.Ordinal);
        knownScopes.UnionWith(builtInScopes);

        // The administrative scope is never grantable through open registration.
        var adminScope = configuration["AdminApi:Scope"] ?? AdminScopeReservation.DefaultAdminScope;
        if (AdminScopeReservation.Grants(requestedScopes, adminScope))
            return TypedResults.Json(
                new ErrorInfoResponse { Error = "invalid_scope", ErrorDescription = "The administrative scope cannot be registered" },
                AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 403);

        foreach (var s in requestedScopes)
        {
            if (!knownScopes.Contains(s))
            {
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_scope", ErrorDescription = $"Unknown scope: {s}" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
            }

            // A role-gated scope is not open-registrable.
            //
            // Existence in the scope store was the only test, so an anonymous registrant could
            // self-assign ANY scope the deployment had defined — including the ones an operator
            // restricted to particular roles. Every authenticated client-mutation path runs those
            // through IClientScopeGuard; this one, the only unauthenticated one, ran nothing. The
            // per-user gate still applies at authorize, so the scope would not be granted to an
            // unentitled user — but the client should not be able to declare it in the first place,
            // and it appears on the consent screen as though it could.
            var registered = storeScopes.FirstOrDefault(x => string.Equals(x.Name, s, StringComparison.Ordinal));
            if (registered is { AllowedRoles.Count: > 0 })
            {
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_scope", ErrorDescription = $"Scope '{s}' is restricted and cannot be registered dynamically" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 403);
            }
        }

        var clientId = GenerateClientId();
        string? clientSecret = null;
        List<string> secretHashes = [];
        if (!isPublicClient)
        {
            clientSecret = GenerateClientSecret();
            secretHashes.Add(passwordHasher.HashPassword(clientSecret));
        }

        var offlineAccess = requestedScopes.Contains("offline_access") || grantTypes.Contains("refresh_token");
        if (offlineAccess && !grantTypes.Contains("refresh_token"))
            grantTypes = [.. grantTypes, "refresh_token"];

        // Both logout URIs are fetched/navigated by the SERVER (back-channel is an outbound POST from the
        // logout path, front-channel is rendered into an iframe), and anonymous DCR sets them. Unvalidated,
        // that is unauthenticated SSRF with an attacker-chosen target — so they go through the same guard as
        // every other outbound URL, at registration time rather than only at use time.
        foreach (var logoutUri in new[] { request.BackchannelLogoutUri, request.FrontchannelLogoutUri })
        {
            if (string.IsNullOrWhiteSpace(logoutUri)) continue;
            if (!Authagonal.Core.Services.OutboundUrl.IsSafe(logoutUri))
                return TypedResults.Json(
                    new ErrorInfoResponse
                    {
                        Error = "invalid_client_metadata",
                        ErrorDescription = "Logout URIs must be external https endpoints.",
                    },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
        }

        var client = new OAuthClient
        {
            ClientId = clientId,
            ClientName = string.IsNullOrWhiteSpace(request.ClientName) ? clientId : request.ClientName!,
            ClientSecretHashes = secretHashes,
            AllowedGrantTypes = grantTypes,
            RedirectUris = redirectUris,
            PostLogoutRedirectUris = request.PostLogoutRedirectUris ?? [],
            BackChannelLogoutUri = request.BackchannelLogoutUri,
            FrontChannelLogoutUri = request.FrontchannelLogoutUri,
            FrontChannelLogoutSessionRequired = request.FrontchannelLogoutSessionRequired ?? true,
            Audiences = request.Audiences ?? [],
            AllowedScopes = requestedScopes,
            // Restricted to the origins of the registrant's OWN already-validated https redirect URIs.
            // An arbitrary list here landed in a server-wide credentialed CORS allowlist that (before it was
            // path-scoped) applied to every endpoint including the cookie-authenticated account API, so an
            // anonymous registrant could name any origin and read authenticated responses from it. Deriving
            // the origins instead means a registrant can only ever reach origins it already had to prove a
            // redirect URI for.
            AllowedCorsOrigins = CorsOriginsFromRedirectUris(redirectUris),
            RequirePkce = true,
            AllowOfflineAccess = offlineAccess,
            RequireClientSecret = !isPublicClient,
            // Consent is NOT optional for a self-registered client. A statically seeded client was
            // configured by an operator who already decided what it may do; this one registered itself
            // over an anonymous endpoint and chose its own scope list. Skipping consent would mean a
            // user signs in and silently grants whatever the client asked for — so the only human
            // check on a client nobody vetted is the one screen this flag shows.
            RequireConsent = true,
        };

        await clientStore.UpsertAsync(client, ct);

        var response = new ClientRegistrationResponse
        {
            ClientId = client.ClientId,
            ClientSecret = clientSecret,
            ClientIdIssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ClientSecretExpiresAt = 0,
            ClientName = client.ClientName,
            RedirectUris = client.RedirectUris,
            PostLogoutRedirectUris = client.PostLogoutRedirectUris,
            GrantTypes = client.AllowedGrantTypes,
            ResponseTypes = client.AllowedGrantTypes.Contains("authorization_code") ? ["code"] : [],
            Scope = string.Join(' ', client.AllowedScopes),
            TokenEndpointAuthMethod = authMethod,
        };

        return TypedResults.Json(response, AuthagonalJsonContext.Default.ClientRegistrationResponse, statusCode: 201);
    }

    /// <summary>
    /// The https origins implied by a client's validated redirect URIs. Loopback and custom-scheme URIs
    /// (native apps) contribute nothing: a browser never sends those as an <c>Origin</c>.
    /// </summary>
    private static List<string> CorsOriginsFromRedirectUris(IEnumerable<string> redirectUris)
    {
        var origins = new List<string>();
        foreach (var uri in redirectUris)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) continue;
            if (parsed.Scheme != Uri.UriSchemeHttps) continue;
            var origin = parsed.IsDefaultPort
                ? $"{parsed.Scheme}://{parsed.Host}"
                : $"{parsed.Scheme}://{parsed.Host}:{parsed.Port}";
            if (!origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                origins.Add(origin);
        }
        return origins;
    }

    private static string GenerateClientId()
    {
        Span<byte> buf = stackalloc byte[16];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    private static string GenerateClientSecret()
    {
        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(buf.ToArray());
    }
}

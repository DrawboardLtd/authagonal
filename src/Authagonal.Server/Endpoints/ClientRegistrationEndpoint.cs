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

    /// <summary>
    /// The OIDC built-ins, always registrable. Anything beyond these has to be named in
    /// <c>Auth:DynamicClientRegistrationScopes</c>.
    /// </summary>
    private static readonly HashSet<string> BuiltInScopes =
        new(["openid", "profile", "email", "offline_access"], StringComparer.Ordinal);

    /// <summary>
    /// Ceiling on how many redirect URIs one anonymous registration may declare, and how long each may
    /// be. Nothing bounded either, so a single registration could carry a megabyte of URIs and the only
    /// limit on total client-record bloat was the 10/hour per-IP budget — which is 10 unbounded records
    /// an hour, per IP, forever. The numbers are generous for a real client and useless as an
    /// amplifier.
    /// </summary>
    private const int MaxRedirectUris = 20;
    private const int MaxRedirectUriLength = 2048;

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

        // Rate-limit anonymous registration to prevent client-record flooding. Keyed on the address the
        // caller cannot choose — Connection.RemoteIpAddress is the forwarded value whenever the immediate
        // peer sits in the default-trusted private ranges, which made this bucket per-request.
        var ip = Services.Cluster.InternalEndpointGuard.TrustedClientAddress(httpContext);
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

        // Bound the list before inspecting it, and bound post_logout_redirect_uris with it — both land
        // in the same client record and neither had a cap of any kind.
        foreach (var (name, list) in new[]
                 {
                     ("redirect_uris", redirectUris),
                     ("post_logout_redirect_uris", request.PostLogoutRedirectUris ?? []),
                 })
        {
            if (list.Count > MaxRedirectUris)
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_client_metadata", ErrorDescription = $"{name} may contain at most {MaxRedirectUris} entries" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

            foreach (var entry in list)
            {
                if (entry?.Length > MaxRedirectUriLength)
                    return TypedResults.Json(
                        new ErrorInfoResponse { Error = "invalid_redirect_uri", ErrorDescription = $"Each {name} entry may be at most {MaxRedirectUriLength} characters" },
                        AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
            }
        }

        // audiences was stored verbatim: unbounded list, unbounded entries, any string at all — on a field
        // that becomes the `aud` of a signed token, from an anonymous endpoint. Same unbounded-list problem
        // the caps above close.
        if (Authagonal.Core.Services.ResourceAudiencePolicy.RejectAudiences(request.Audiences ?? []) is { } audienceError)
            return TypedResults.Json(
                new ErrorInfoResponse { Error = "invalid_client_metadata", ErrorDescription = audienceError },
                AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

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

        // RFC 7591 §2 / §3.2.1: the registered token_endpoint_auth_method must be one the AS supports,
        // and the response must state the method the AS ACTUALLY assigned.
        //
        // Nothing validated it. Anything that was not the literal string "none" produced a confidential
        // client with a generated secret, and the response echoed whatever the caller had asked for — so
        // registering "private_key_jwt" returned a client that says it authenticates with a private key
        // and in fact authenticates with a shared secret nobody expected to receive, while a typo'd
        // method registered silently as client_secret_basic. A client that believes it holds no bearer
        // secret does not protect the one it was handed.
        var authMethod = request.TokenEndpointAuthMethod ?? "client_secret_basic";
        if (!Authagonal.Protocol.Endpoints.ClientAuthentication.SupportedAuthMethods.Contains(authMethod, StringComparer.Ordinal))
            return TypedResults.Json(
                new ErrorInfoResponse
                {
                    Error = "invalid_client_metadata",
                    ErrorDescription = $"Unsupported token_endpoint_auth_method '{authMethod}'. Supported: {string.Join(", ", Authagonal.Protocol.Endpoints.ClientAuthentication.SupportedAuthMethods)}",
                },
                AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);

        var isPublicClient = authMethod == "none";

        // private_key_jwt authenticates against the client's registered JWKS and nothing else, so a
        // registration that names it without supplying one asks for a method that cannot ever succeed —
        // and, before this, was quietly downgraded to a client_secret. Bind the key material here, at
        // the only point the registrant can prove it owns it.
        var usesPrivateKeyJwt = authMethod == "private_key_jwt";
        string? registeredJwksJson = null;
        if (usesPrivateKeyJwt)
        {
            if (request.Jwks is { } jwksElement && jwksElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                registeredJwksJson = jwksElement.GetRawText();
            }
            else if (!string.IsNullOrWhiteSpace(request.JwksUri))
            {
                // Server-fetched client metadata (RFC 9700 §4.14): the JWKS URI is retrieved by THIS
                // process during client authentication, from an anonymously-registrable field, so it
                // goes through the same outbound guard as every other URL a registrant supplies.
                if (!Authagonal.Core.Services.OutboundUrl.IsSafe(request.JwksUri))
                    return TypedResults.Json(
                        new ErrorInfoResponse { Error = "invalid_client_metadata", ErrorDescription = "jwks_uri must be an external https endpoint." },
                        AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
            }
            else
            {
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_client_metadata", ErrorDescription = "token_endpoint_auth_method 'private_key_jwt' requires jwks or jwks_uri" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
            }
        }

        var requestedScopes = string.IsNullOrWhiteSpace(request.Scope)
            ? new List<string>()
            : request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var storeScopes = await scopeStore.ListAsync(ct);
        var knownScopes = new HashSet<string>(storeScopes.Select(s => s.Name), StringComparer.Ordinal);
        knownScopes.UnionWith(BuiltInScopes);

        // What open registration may reach: the built-ins, plus exactly what an operator named.
        //
        // The previous set was "everything in the scope store minus the admin name", which inverts the
        // default in the wrong direction — a scope exists because some client needs it, not because
        // every anonymous registrant should be able to claim it.
        var registrableScopes = new HashSet<string>(BuiltInScopes, StringComparer.Ordinal);
        registrableScopes.UnionWith(
            authOptions.Value.DynamicClientRegistrationScopes.Where(s => !string.IsNullOrWhiteSpace(s)));

        // The administrative scope is never grantable through open registration.
        var adminScope = configuration["AdminApi:Scope"] ?? AdminScopeReservation.DefaultAdminScope;
        if (AdminScopeReservation.Grants(requestedScopes, adminScope))
            return TypedResults.Json(
                new ErrorInfoResponse { Error = "invalid_scope", ErrorDescription = "The administrative scope cannot be registered" },
                AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 403);

        // Give the host's own escalation policy a say, exactly as every authenticated client-mutation
        // path does. The principal here is anonymous, which is the point: a host that distinguishes
        // callers can refuse outright what it would let a named admin grant. The shipped guard allows
        // everything, so on a default deployment the allowlist above is what binds.
        var scopeGuard = httpContext.RequestServices.GetService<IClientScopeGuard>();
        if (scopeGuard?.FindUngrantableScope(httpContext.User, requestedScopes) is { } ungrantable)
            return TypedResults.Json(
                new ErrorInfoResponse { Error = "invalid_scope", ErrorDescription = $"Scope '{ungrantable}' cannot be registered dynamically" },
                AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 403);

        foreach (var s in requestedScopes)
        {
            if (!knownScopes.Contains(s))
            {
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_scope", ErrorDescription = $"Unknown scope: {s}" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
            }

            // Not on the allowlist, or role-gated: not open-registrable.
            //
            // Existence in the scope store was the only test, so an anonymous registrant could
            // self-assign ANY scope the deployment had defined. Role-gated scopes were then refused,
            // but Scope.AllowedRoles defaults to empty — "every scope until an operator says
            // otherwise" — so the ungated majority stayed reachable. The per-user gate still applies at
            // authorize, so the scope would not be granted to an unentitled user; but the client should
            // not be able to declare it, and it appears on the consent screen as though it could.
            var registered = storeScopes.FirstOrDefault(x => string.Equals(x.Name, s, StringComparison.Ordinal));
            if (!registrableScopes.Contains(s) || registered is { AllowedRoles.Count: > 0 })
            {
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_scope", ErrorDescription = $"Scope '{s}' is restricted and cannot be registered dynamically" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 403);
            }
        }

        // allowed_cors_origins was stored verbatim. The CORS provider now ignores malformed entries,
        // but refusing them here is what stops an operator seeing an origin in the client record and
        // reasonably believing it is in effect.
        foreach (var origin in request.AllowedCorsOrigins ?? [])
        {
            if (!DynamicCorsPolicyProvider.IsValidOrigin(origin))
            {
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_client_metadata", ErrorDescription = $"Invalid CORS origin: {origin}" },
                    AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 400);
            }
        }

        var clientId = GenerateClientId();
        string? clientSecret = null;
        List<string> secretHashes = [];
        // No secret for private_key_jwt either: the client authenticates with a signed assertion, and
        // issuing one anyway would leave a second, weaker credential in circulation for a client whose
        // whole point is not to hold one.
        if (!isPublicClient && !usesPrivateKeyJwt)
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
                        // Says what the guard actually enforces. It claimed "https endpoints" while
                        // OutboundUrl.IsSafe permits http and does no DNS resolution, so the text
                        // described a check the server does not make — the kind of overclaim that
                        // gets a reviewer to stop looking.
                        ErrorDescription =
                            "Logout URIs must be external addresses (not loopback, link-local, private, " +
                            "or a .localhost/.local/.internal name).",
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
            // The registrant had the field, so its answer counts — including "none". Without this flag an
            // empty list was indistinguishable from a client that was never offered the choice, and every
            // DCR client therefore kept the permissive reading that let it name any absolute URI as a
            // resource and receive a tenant-signed token aimed at it.
            AudiencesDeclared = true,
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
            // The key material the assertion will be verified against. Without it, registering
            // private_key_jwt bound nothing and ClientAuthentication had no JWKS to resolve.
            JwksJson = registeredJwksJson,
            JwksUri = usesPrivateKeyJwt && registeredJwksJson is null ? request.JwksUri : null,
            // Consent is NOT optional for a self-registered client. A statically seeded client was
            // configured by an operator who already decided what it may do; this one registered itself
            // over an anonymous endpoint and chose its own scope list. Skipping consent would mean a
            // user signs in and silently grants whatever the client asked for — so the only human
            // check on a client nobody vetted is the one screen this flag shows.
            RequireConsent = true,
        };

        await clientStore.UpsertAsync(client, ct);

        // A registration adds credentialed origins to the pooled CORS list, which every node caches for
        // an hour with no invalidation — so without this the registrant's own origins do not work until
        // the cache turns over, and (worse, on the revocation side) removing them later would not take
        // effect either. Best-effort: the client is already written, and the entry expires anyway.
        try
        {
            if (httpContext.RequestServices.GetService<Authagonal.Core.Clustering.IClusterEventBus>() is { } bus)
                await DynamicCorsPolicyProvider.InvalidateAsync(
                    bus, httpContext.RequestServices.GetService<ITenantContext>(), ct);
        }
        catch (Exception) { /* registration succeeded; a bus hiccup must not turn it into a 500 */ }

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

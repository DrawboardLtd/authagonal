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
            // Require an absolute URI and reject script/data/file pseudo-schemes; http(s) and native
            // custom schemes (mobile deep links) remain valid.
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
                parsed.Scheme is "javascript" or "data" or "vbscript" or "file")
            {
                return TypedResults.Json(
                    new ErrorInfoResponse { Error = "invalid_redirect_uri", ErrorDescription = $"Invalid redirect_uri: {uri}" },
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
        var adminScope = configuration["AdminApi:Scope"] ?? "authagonal-admin";
        if (requestedScopes.Contains(adminScope, StringComparer.OrdinalIgnoreCase))
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
            AllowedCorsOrigins = request.AllowedCorsOrigins ?? [],
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

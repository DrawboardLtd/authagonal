using System.Security.Cryptography;
using Authagonal.Core.Models;
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

    private static async Task<IResult> HandleAsync(
        ClientRegistrationRequest request,
        IClientStore clientStore,
        IScopeStore scopeStore,
        PasswordHasher passwordHasher,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        if (!authOptions.Value.DynamicClientRegistrationEnabled)
            return TypedResults.Json(
                new ErrorInfoResponse { Error = "not_supported", ErrorDescription = "Dynamic client registration is not enabled" },
                AuthagonalJsonContext.Default.ErrorInfoResponse, statusCode: 403);

        var redirectUris = request.RedirectUris ?? [];
        var grantTypes = request.GrantTypes ?? ["authorization_code"];

        foreach (var uri in redirectUris)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != "http" && parsed.Scheme != "https" && !parsed.IsAbsoluteUri))
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

using System.Text;
using Authagonal.Core.Constants;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Endpoints;

public static class TokenEndpoint
{
    public static IEndpointRouteBuilder MapTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/token", async (
            HttpContext httpContext,
            ITokenService tokenService,
            IClientStore clientStore,
            Services.PasswordHasher passwordHasher,
            CancellationToken ct) =>
        {
            var form = await httpContext.Request.ReadFormAsync(ct);

            // Authenticate client
            var (clientId, clientSecret) = ExtractClientCredentials(httpContext, form);

            if (string.IsNullOrWhiteSpace(clientId))
                return TokenError("invalid_client", "client_id is required");

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return TokenError("invalid_client", "Unknown client");

            // Verify client secret if required
            if (client.RequireClientSecret)
            {
                if (string.IsNullOrWhiteSpace(clientSecret))
                    return TokenError("invalid_client", "client_secret is required");

                var secretValid = client.ClientSecretHashes.Any(hash =>
                {
                    var result = passwordHasher.VerifyPassword(clientSecret, hash);
                    return result is Services.PasswordVerifyResult.Success or Services.PasswordVerifyResult.SuccessRehashNeeded;
                });

                if (!secretValid)
                    return TokenError("invalid_client", "Invalid client credentials");
            }

            var grantType = form["grant_type"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(grantType))
                return TokenError("invalid_request", "grant_type is required");

            if (!client.AllowedGrantTypes.Contains(grantType, StringComparer.OrdinalIgnoreCase))
                return TokenError("unauthorized_client", "Grant type not allowed for this client");

            try
            {
                return grantType switch
                {
                    GrantTypes.AuthorizationCode => await HandleAuthorizationCode(form, tokenService, clientId, ct),
                    GrantTypes.RefreshToken => await HandleRefreshToken(form, tokenService, clientId, ct),
                    GrantTypes.ClientCredentials => await HandleClientCredentials(form, tokenService, clientId, ct),
                    _ => TokenError("unsupported_grant_type", $"Grant type '{grantType}' is not supported")
                };
            }
            catch (InvalidOperationException ex)
            {
                return TokenError("invalid_grant", ex.Message);
            }
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .RequireRateLimiting("token")
        .WithTags("OAuth");

        return app;
    }

    private static async Task<IResult> HandleAuthorizationCode(
        IFormCollection form, ITokenService tokenService, string clientId, CancellationToken ct)
    {
        var code = form["code"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(code))
            return TokenError("invalid_request", "code is required");

        var redirectUri = form["redirect_uri"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(redirectUri))
            return TokenError("invalid_request", "redirect_uri is required");

        var codeVerifier = form["code_verifier"].FirstOrDefault() ?? string.Empty;

        var response = await tokenService.HandleAuthorizationCodeAsync(code, clientId, redirectUri, codeVerifier, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> HandleRefreshToken(
        IFormCollection form, ITokenService tokenService, string clientId, CancellationToken ct)
    {
        var refreshToken = form["refresh_token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(refreshToken))
            return TokenError("invalid_request", "refresh_token is required");

        var response = await tokenService.HandleRefreshTokenAsync(refreshToken, clientId, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> HandleClientCredentials(
        IFormCollection form, ITokenService tokenService, string clientId, CancellationToken ct)
    {
        var scope = form["scope"].FirstOrDefault() ?? string.Empty;
        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var response = await tokenService.HandleClientCredentialsAsync(clientId, scopes, ct);
        return Results.Ok(response);
    }

    private static (string? ClientId, string? ClientSecret) ExtractClientCredentials(
        HttpContext httpContext, IFormCollection form)
    {
        // Try Authorization header first (client_secret_basic)
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var encoded = authHeader["Basic ".Length..];
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var colonIndex = decoded.IndexOf(':');
                if (colonIndex > 0)
                {
                    var id = Uri.UnescapeDataString(decoded[..colonIndex]);
                    var secret = Uri.UnescapeDataString(decoded[(colonIndex + 1)..]);
                    return (id, secret);
                }
            }
            catch (FormatException)
            {
                // Malformed Basic header; fall through to form body
            }
        }

        // Fall back to client_secret_post (form body)
        var clientId = form["client_id"].FirstOrDefault();
        var clientSecret = form["client_secret"].FirstOrDefault();
        return (clientId, clientSecret);
    }

    private static IResult TokenError(string error, string description)
    {
        return Results.Json(
            new { error, error_description = description },
            statusCode: error == "invalid_client" ? 401 : 400);
    }
}

using System.Diagnostics;
using System.Text;
using Authagonal.Core.Constants;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Authagonal.Protocol.Endpoints;

internal static class TokenEndpoint
{
    public static IEndpointRouteBuilder MapProtocolTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/token", async (
            HttpContext httpContext,
            IProtocolTokenService tokenService,
            IClientStore clientStore,
            IClientSecretVerifier secretVerifier,
            CancellationToken ct) =>
        {
            var form = await httpContext.Request.ReadFormAsync(ct);

            var (clientId, clientSecret) = ExtractClientCredentials(httpContext, form);

            if (string.IsNullOrWhiteSpace(clientId))
                return TokenError("invalid_client", "client_id is required");

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return TokenError("invalid_client", "Unknown client");

            if (!client.Enabled)
                return TokenError("unauthorized_client", "Client is disabled");

            if (client.RequireClientSecret)
            {
                if (string.IsNullOrWhiteSpace(clientSecret))
                    return TokenError("invalid_client", "client_secret is required");

                if (!await secretVerifier.VerifyAsync(client, clientSecret, ct))
                    return TokenError("invalid_client", "Invalid client credentials");
            }

            var grantType = form["grant_type"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(grantType))
                return TokenError("invalid_request", "grant_type is required");

            if (!client.AllowedGrantTypes.Contains(grantType, StringComparer.OrdinalIgnoreCase))
                return TokenError("unauthorized_client", "Grant type not allowed for this client");

            if (grantType is not (GrantTypes.AuthorizationCode or GrantTypes.RefreshToken or GrantTypes.ClientCredentials))
                return TokenError("unsupported_grant_type", $"Grant type '{grantType}' is not supported");

            try
            {
                return grantType switch
                {
                    GrantTypes.AuthorizationCode => await HandleAuthorizationCode(form, tokenService, clientId, ct),
                    GrantTypes.RefreshToken => await HandleRefreshToken(form, tokenService, clientId, ct),
                    GrantTypes.ClientCredentials => await HandleClientCredentials(form, tokenService, clientId, ct),
                    _ => throw new UnreachableException()
                };
            }
            catch (InvalidOperationException ex)
            {
                return TokenError("invalid_grant", ex.Message);
            }
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .WithTags("OIDC");

        return app;
    }

    private static async Task<IResult> HandleAuthorizationCode(
        IFormCollection form, IProtocolTokenService tokenService, string clientId, CancellationToken ct)
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
        IFormCollection form, IProtocolTokenService tokenService, string clientId, CancellationToken ct)
    {
        var refreshToken = form["refresh_token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(refreshToken))
            return TokenError("invalid_request", "refresh_token is required");

        var resources = form["resource"].Where(r => !string.IsNullOrWhiteSpace(r)).Cast<string>().ToArray();

        try
        {
            var response = await tokenService.HandleRefreshTokenAsync(
                refreshToken, clientId, resources.Length > 0 ? resources : null, ct);
            return Results.Ok(response);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Resource '", StringComparison.Ordinal))
        {
            return TokenError("invalid_target", ex.Message);
        }
    }

    private static async Task<IResult> HandleClientCredentials(
        IFormCollection form, IProtocolTokenService tokenService, string clientId, CancellationToken ct)
    {
        var scope = form["scope"].FirstOrDefault() ?? string.Empty;
        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var resources = form["resource"].Where(r => !string.IsNullOrWhiteSpace(r)).Cast<string>().ToArray();

        try
        {
            var response = await tokenService.HandleClientCredentialsAsync(
                clientId, scopes, resources.Length > 0 ? resources : null, ct);
            return Results.Ok(response);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Resource '", StringComparison.Ordinal))
        {
            return TokenError("invalid_target", ex.Message);
        }
    }

    private static (string? ClientId, string? ClientSecret) ExtractClientCredentials(
        HttpContext httpContext, IFormCollection form)
    {
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
            catch (FormatException) { /* fall through */ }
        }

        var clientId = form["client_id"].FirstOrDefault();
        var clientSecret = form["client_secret"].FirstOrDefault();
        return (clientId, clientSecret);
    }

    private static IResult TokenError(string error, string description)
    {
        return JsonResults.OAuthError(error, description,
            statusCode: error == "invalid_client" ? 401 : 400);
    }
}

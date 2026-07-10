using Authagonal.Protocol.Services;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Protocol.Endpoints;

/// <summary>
/// Grant handlers shared by the Protocol and Server token endpoints. Server layers
/// device-code and IAuthHook handling on top; the three core grants are identical.
/// </summary>
internal static class TokenGrantHandlers
{
    public static async Task<IResult> HandleAuthorizationCode(
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

    public static async Task<IResult> HandleRefreshToken(
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

    public static async Task<IResult> HandleClientCredentials(
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

    public static IResult TokenError(string error, string description)
    {
        return JsonResults.OAuthError(error, description,
            statusCode: error == "invalid_client" ? 401 : 400);
    }
}

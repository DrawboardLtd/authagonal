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

    // RFC 8693 / OAuth protocol fields on a token-exchange request; anything else is a host
    // extension parameter forwarded to ITokenExchangeSubjectTransformer.
    private static readonly HashSet<string> TokenExchangeProtocolParameters = new(StringComparer.Ordinal)
    {
        "grant_type", "client_id", "client_secret", "client_assertion", "client_assertion_type",
        "subject_token", "subject_token_type", "actor_token", "actor_token_type",
        "requested_token_type", "scope", "resource", "audience",
    };

    public static async Task<IResult> HandleTokenExchange(
        IFormCollection form, IProtocolTokenService tokenService, string clientId, CancellationToken ct)
    {
        var subjectToken = form["subject_token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(subjectToken))
            return TokenError("invalid_request", "subject_token is required");

        var subjectTokenType = form["subject_token_type"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(subjectTokenType))
            return TokenError("invalid_request", "subject_token_type is required");

        // RFC 8693 §2.1 makes actor tokens optional for servers; this one does not do delegation
        // chains (no act claim), so be explicit rather than silently ignoring the parameter.
        if (!string.IsNullOrWhiteSpace(form["actor_token"].FirstOrDefault()))
            return TokenError("invalid_request", "actor_token is not supported");

        var requestedTokenType = form["requested_token_type"].FirstOrDefault();
        var scope = form["scope"].FirstOrDefault() ?? string.Empty;
        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var resources = form["resource"].Where(r => !string.IsNullOrWhiteSpace(r)).Cast<string>().ToArray();
        var audiences = form["audience"].Where(a => !string.IsNullOrWhiteSpace(a)).Cast<string>().ToArray();

        // RFC 8693 permits additional request parameters; everything non-protocol is forwarded to
        // the host's ITokenExchangeSubjectTransformer (context bindings like project_id).
        var extraParameters = form
            .Where(kv => !TokenExchangeProtocolParameters.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value.FirstOrDefault() ?? string.Empty, StringComparer.Ordinal);

        try
        {
            var response = await tokenService.HandleTokenExchangeAsync(
                clientId, subjectToken, subjectTokenType, requestedTokenType,
                scopes.Length > 0 ? scopes : null,
                resources.Length > 0 ? resources : null,
                audiences.Length > 0 ? audiences : null,
                extraParameters.Count > 0 ? extraParameters : null,
                ct);
            return Results.Ok(response);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Scope '", StringComparison.Ordinal))
        {
            return TokenError("invalid_scope", ex.Message);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith("Resource '", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Exchange rejected", StringComparison.Ordinal))
        {
            return TokenError("invalid_target", ex.Message);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("subject_token_type", StringComparison.Ordinal) ||
            ex.Message.Contains("requested_token_type", StringComparison.Ordinal))
        {
            return TokenError("invalid_request", ex.Message);
        }
    }

    public static IResult TokenError(string error, string description)
    {
        return JsonResults.OAuthError(error, description,
            statusCode: error == "invalid_client" ? 401 : 400);
    }
}

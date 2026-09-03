using Authagonal.Core.Models;
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

        try
        {
            var response = await tokenService.HandleAuthorizationCodeAsync(code, clientId, redirectUri, codeVerifier, ct);
            return TokenSuccess(response);
        }
        // ProtocolTokenException is the type that carries an OAuth error code, and this handler did not map
        // it — harmless while nothing on this path threw one, and a 500 the moment something did. The agent
        // ceiling now applies at the shared mint, so an agent whose ceiling grants nothing unattended is
        // refused here and the caller is entitled to `unauthorized_client` rather than an opaque failure.
        catch (ProtocolTokenException ex)
        {
            return TokenError(ex.Error, ex.Description);
        }
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
            return TokenSuccess(response);
        }
        catch (ProtocolTokenException ex)
        {
            return TokenError(ex.Error, ex.Description);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Resource '", StringComparison.Ordinal))
        {
            return TokenError("invalid_target", ex.Message);
        }
    }

    // OAuth protocol fields on a client_credentials request; anything else is a host extension
    // parameter forwarded to IClientCredentialsClaimsTransformer (context bindings like organization_id).
    private static readonly HashSet<string> ClientCredentialsProtocolParameters = new(StringComparer.Ordinal)
    {
        "grant_type", "client_id", "client_secret", "client_assertion", "client_assertion_type",
        "scope", "resource", "audience",
    };

    public static async Task<IResult> HandleClientCredentials(
        IFormCollection form, IProtocolTokenService tokenService, string clientId, CancellationToken ct)
    {
        var scope = form["scope"].FirstOrDefault() ?? string.Empty;
        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var resources = form["resource"].Where(r => !string.IsNullOrWhiteSpace(r)).Cast<string>().ToArray();

        var extraParameters = form
            .Where(kv => !ClientCredentialsProtocolParameters.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value.FirstOrDefault() ?? string.Empty, StringComparer.Ordinal);

        try
        {
            var response = await tokenService.HandleClientCredentialsAsync(
                clientId, scopes, resources.Length > 0 ? resources : null,
                extraParameters.Count > 0 ? extraParameters : null, ct);
            return TokenSuccess(response);
        }
        catch (ProtocolTokenException ex)
        {
            return TokenError(ex.Error, ex.Description);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Resource '", StringComparison.Ordinal))
        {
            return TokenError("invalid_target", ex.Message);
        }
    }

    // RFC 8693 / RFC 9396 / OAuth protocol fields on a token-exchange request; anything else
    // is a host extension parameter forwarded to ITokenExchangeSubjectTransformer.
    private static readonly HashSet<string> TokenExchangeProtocolParameters = new(StringComparer.Ordinal)
    {
        "grant_type", "client_id", "client_secret", "client_assertion", "client_assertion_type",
        "subject_token", "subject_token_type", "actor_token", "actor_token_type",
        "requested_token_type", "scope", "resource", "audience",
        "authorization_details", "approval_id",
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

        // RFC 8693 §2.1 — an actor token is accepted only on the composite-delegation path
        // (clients with an agent profile); the service rejects it for everyone else.
        var actorToken = form["actor_token"].FirstOrDefault();
        var actorTokenType = form["actor_token_type"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(actorToken) && string.IsNullOrWhiteSpace(actorTokenType))
            return TokenError("invalid_request", "actor_token_type is required when actor_token is present");
        // RFC 8693 §2.1 states the constraint in both directions: actor_token_type "MUST NOT be
        // included otherwise". Ignoring the orphan meant the whole actor block — including the check
        // binding the actor to the authenticated client — was skipped while the caller believed it had
        // asked for delegation.
        if (string.IsNullOrWhiteSpace(actorToken) && !string.IsNullOrWhiteSpace(actorTokenType))
            return TokenError("invalid_request", "actor_token_type must not be present without actor_token");

        // RFC 9396 narrowing + the approval-poll handle (both agentic; harmless when absent).
        var authorizationDetails = form["authorization_details"].FirstOrDefault();
        var approvalId = form["approval_id"].FirstOrDefault();

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
                actorToken: string.IsNullOrWhiteSpace(actorToken) ? null : actorToken,
                actorTokenType: string.IsNullOrWhiteSpace(actorTokenType) ? null : actorTokenType,
                authorizationDetailsJson: string.IsNullOrWhiteSpace(authorizationDetails) ? null : authorizationDetails,
                approvalId: string.IsNullOrWhiteSpace(approvalId) ? null : approvalId,
                ct: ct);
            return TokenSuccess(response);
        }
        catch (ApprovalPendingException pending)
        {
            // RFC 8628-style park: the body carries the approval handle and poll interval.
            return JsonResults.NoStore(TypedResults.Json(new ApprovalPendingResponse
            {
                ErrorDescription = pending.Description,
                ApprovalId = pending.ApprovalId,
                Interval = pending.IntervalSeconds,
            }, ProtocolJsonContext.Default.ApprovalPendingResponse, statusCode: 400));
        }
        catch (ProtocolTokenException ex)
        {
            return TokenError(ex.Error, ex.Description);
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

    /// <summary>
    /// A token response with the caching headers RFC 6749 §5.1 requires.
    /// </summary>
    /// <remarks>
    /// The spec is explicit — "The authorization server MUST include the HTTP Cache-Control response
    /// header field with a value of no-store … as well as the Pragma response header field with a
    /// value of no-cache" — and none were set. The body carries the access token, the refresh token
    /// and the ID token, so any intermediary or browser applying heuristic freshness could retain
    /// them.
    /// </remarks>
    public static IResult TokenSuccess(TokenResponse response) => new NoStoreJson(response);

    private sealed class NoStoreJson(TokenResponse response) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers.Pragma = "no-cache";
            return Results.Json(response, ProtocolJsonContext.Default.TokenResponse).ExecuteAsync(httpContext);
        }
    }

    public static IResult TokenError(string error, string description)
    {
        // RFC 6749 §5.2: a 401 invalid_client MUST carry a WWW-Authenticate challenge naming the
        // scheme the server accepts. Shared with PAR, introspection, revocation and the device
        // endpoint — they authenticate clients through the same path and owe the same header.
        if (error == "invalid_client")
            return JsonResults.UnauthorizedClient(error, description, realm: "token");

        return JsonResults.OAuthError(error, description, statusCode: 400);
    }
}

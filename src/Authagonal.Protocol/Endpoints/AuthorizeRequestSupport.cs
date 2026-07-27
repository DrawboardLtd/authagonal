using System.Web;
using Authagonal.Core.Models;
using Authagonal.Protocol.Services;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Protocol.Endpoints;

/// <summary>
/// Uniform read access over the two places authorize-request parameters can live:
/// the query string, or a pushed (PAR) payload.
/// </summary>
internal interface IReadableRequestParameters
{
    string? Get(string key);
    IEnumerable<string> GetAll(string key);
}

internal sealed class QueryRequestParameters(IQueryCollection query) : IReadableRequestParameters
{
    public string? Get(string key) => query[key].FirstOrDefault();
    public IEnumerable<string> GetAll(string key) => query[key].Where(v => v is not null).Cast<string>();
}

internal sealed class ParRequestParameters(Dictionary<string, string[]> parameters) : IReadableRequestParameters
{
    public string? Get(string key) => parameters.TryGetValue(key, out var values) ? values.FirstOrDefault() : null;
    public IEnumerable<string> GetAll(string key) => parameters.TryGetValue(key, out var values) ? values : [];
}

/// <summary>The authorize-request parameters both hosts validate and act on.</summary>
internal sealed class AuthorizeRequest
{
    public string? RedirectUri { get; init; }
    public string? ResponseType { get; init; }
    public string? Scope { get; init; }
    public string? State { get; init; }
    public string? CodeChallenge { get; init; }
    public string? CodeChallengeMethod { get; init; }
    public string? Nonce { get; init; }
    public string[] Resources { get; init; } = [];

    /// <summary>Set by <see cref="AuthorizeRequestSupport.Validate"/>.</summary>
    public string[] RequestedScopes { get; set; } = [];

    public static AuthorizeRequest Read(IReadableRequestParameters source) => new()
    {
        RedirectUri = source.Get("redirect_uri"),
        ResponseType = source.Get("response_type"),
        Scope = source.Get("scope"),
        State = source.Get("state"),
        CodeChallenge = source.Get("code_challenge"),
        CodeChallengeMethod = source.Get("code_challenge_method"),
        Nonce = source.Get("nonce"),
        Resources = source.GetAll("resource").Where(r => !string.IsNullOrWhiteSpace(r)).ToArray(),
    };
}

/// <summary>
/// Validation and code-issuance logic shared by the Protocol and Server authorize endpoints.
/// The hosts differ in how they authenticate the user (login UI vs. Challenge), consent,
/// and provisioning — everything protocol-shaped lives here.
/// </summary>
internal static class AuthorizeRequestSupport
{
    /// <summary>
    /// Runs the redirect_uri → response_type → scope → resource → PKCE validation sequence.
    /// Returns an error result to short-circuit with, or null when the request is valid
    /// (in which case <see cref="AuthorizeRequest.RequestedScopes"/> is populated).
    /// </summary>
    public static IResult? Validate(OAuthClient client, AuthorizeRequest request)
    {
        var (redirectUri, state) = (request.RedirectUri, request.State);

        if (string.IsNullOrWhiteSpace(redirectUri))
            return BuildErrorRedirect(null, "invalid_request", "redirect_uri is required", state);

        if (!IsRedirectUriRegistered(redirectUri, client.RedirectUris))
            return BuildErrorRedirect(null, "invalid_request", "redirect_uri is not registered for this client", state);

        if (string.IsNullOrWhiteSpace(request.ResponseType) || request.ResponseType != "code")
            return BuildErrorRedirect(redirectUri, "unsupported_response_type", "Only response_type=code is supported", state);

        if (string.IsNullOrWhiteSpace(request.Scope))
            return BuildErrorRedirect(redirectUri, "invalid_scope", "scope is required", state);

        var requestedScopes = request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var invalidScopes = requestedScopes.Except(client.AllowedScopes, StringComparer.OrdinalIgnoreCase).ToArray();
        if (invalidScopes.Length > 0)
            return BuildErrorRedirect(redirectUri, "invalid_scope", $"Scopes not allowed: {string.Join(", ", invalidScopes)}", state);

        // RFC 8707: each resource must be an absolute URI without a fragment, and — when the client
        // declares an audience allowlist — must appear in it.
        //
        // An EMPTY Audiences list means "unset", not "deny everything". A dynamically registered
        // client cannot declare audiences (RFC 7591 has no field for them), so treating empty as
        // deny-all made `resource` unusable for every DCR client — which is every MCP client, since
        // the MCP authorization spec requires them to name the MCP server as the resource. The
        // restriction still applies wherever an operator has deliberately configured one.
        //
        // Naming a resource is not access to it: the value only narrows `aud`, and the resource
        // server still validates that `aud` addresses itself before honouring the token.
        foreach (var resource in request.Resources)
        {
            if (!Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) || !string.IsNullOrEmpty(resourceUri.Fragment))
                return BuildErrorRedirect(redirectUri, "invalid_target", $"resource '{resource}' is not a valid absolute URI", state);

            if (client.Audiences.Count > 0 && !client.Audiences.Contains(resource, StringComparer.Ordinal))
                return BuildErrorRedirect(redirectUri, "invalid_target", $"resource '{resource}' is not registered for this client", state);
        }

        if (client.RequirePkce && string.IsNullOrWhiteSpace(request.CodeChallenge))
            return BuildErrorRedirect(redirectUri, "invalid_request", "code_challenge is required", state);

        // The method is checked whenever a challenge is present, not only for RequirePkce clients. `plain`
        // makes PKCE decorative — the challenge IS the verifier, so anyone positioned to read the
        // authorization request (the attack PKCE exists to stop) can redeem an intercepted code. RFC 7636
        // §4.3 also defaults a missing method to `plain`, so saying nothing degraded the same way. A
        // client that opts into PKCE without being marked RequirePkce is doing so defensively, and used to
        // get the weakest form of it. Discovery has always advertised S256 only; this agrees with it.
        if (!string.IsNullOrWhiteSpace(request.CodeChallenge)
            && !string.Equals(request.CodeChallengeMethod, "S256", StringComparison.Ordinal))
        {
            return BuildErrorRedirect(redirectUri, "invalid_request", "code_challenge_method must be S256", state);
        }

        request.RequestedScopes = requestedScopes;
        return null;
    }

    public static string MapRejectionError(OidcRejection reason) => reason switch
    {
        OidcRejection.LoginRequired => "login_required",
        OidcRejection.ConsentRequired => "consent_required",
        OidcRejection.AccountSelectionRequired => "account_selection_required",
        _ => "access_denied",
    };

    /// <summary>
    /// Mints the authorization code, consumes any PAR record, and builds the success redirect.
    /// Call only after <see cref="Validate"/> has passed.
    /// </summary>
    public static async Task<IResult> IssueCodeAndRedirectAsync(
        ProtocolAuthorizationCodeService authCodeService,
        ProtocolPushedAuthorizationService parService,
        string clientId,
        OidcSubject subject,
        AuthorizeRequest request,
        string? requestUri,
        string issuer,
        CancellationToken ct)
    {
        var code = await authCodeService.CreateCodeAsync(
            clientId,
            subject,
            request.RedirectUri!,
            request.RequestedScopes.ToList(),
            request.CodeChallenge,
            request.CodeChallengeMethod,
            request.Nonce,
            request.Resources.Length > 0 ? request.Resources : null,
            ct);

        if (!string.IsNullOrWhiteSpace(requestUri))
            await parService.RemoveAsync(requestUri, ct);

        var uriBuilder = new UriBuilder(request.RedirectUri!);
        var queryParams = HttpUtility.ParseQueryString(uriBuilder.Query);
        queryParams["code"] = code;
        if (!string.IsNullOrWhiteSpace(request.State))
            queryParams["state"] = request.State;
        // RFC 9207: name the issuer in the authorization response. A client talking to several
        // authorization servers cannot otherwise tell which one a code came back from, which is the
        // whole mix-up attack — the attacker gets a victim's code redeemed at the wrong server.
        // Clients that ignore the parameter are unaffected.
        queryParams["iss"] = issuer;
        uriBuilder.Query = queryParams.ToString();

        return Results.Redirect(uriBuilder.ToString());
    }

    /// <summary>
    /// Compares redirect URIs using normalized form (scheme, host, port, path, query) to prevent
    /// bypass via implicit ports, trailing slashes, or encoding differences.
    /// </summary>
    public static bool IsRedirectUriRegistered(string redirectUri, IReadOnlyList<string> registeredUris)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var requestedUri))
            return false;

        // Reject URIs with fragments (per OAuth spec)
        if (!string.IsNullOrEmpty(requestedUri.Fragment))
            return false;

        foreach (var registered in registeredUris)
        {
            if (!Uri.TryCreate(registered, UriKind.Absolute, out var registeredUri))
                continue;

            if (string.Equals(requestedUri.Scheme, registeredUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(requestedUri.Host, registeredUri.Host, StringComparison.OrdinalIgnoreCase) &&
                requestedUri.Port == registeredUri.Port &&
                string.Equals(requestedUri.AbsolutePath, registeredUri.AbsolutePath, StringComparison.Ordinal) &&
                string.Equals(requestedUri.Query, registeredUri.Query, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Redirects the error back to the client when a redirect_uri is available, otherwise
    /// returns a direct OAuth error response.
    /// </summary>
    public static IResult BuildErrorRedirect(string? redirectUri, string error, string errorDescription, string? state)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return JsonResults.OAuthError(error, errorDescription);
        }

        var uriBuilder = new UriBuilder(redirectUri);
        var queryParams = HttpUtility.ParseQueryString(uriBuilder.Query);
        queryParams["error"] = error;
        queryParams["error_description"] = errorDescription;
        if (!string.IsNullOrWhiteSpace(state))
            queryParams["state"] = state;
        uriBuilder.Query = queryParams.ToString();

        return Results.Redirect(uriBuilder.ToString());
    }
}

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

    /// <summary>
    /// OIDC Core §3.1.2.1 <c>max_age</c>: the greatest elapsed time, in seconds, the RP will accept
    /// since the end-user last actively authenticated. Null when absent.
    /// </summary>
    /// <remarks>
    /// Was not read at all — not by this method, not anywhere in <c>src/</c> — so a request carrying
    /// <c>max_age=0</c> was answered from a thirty-day-old cookie session with no re-authentication
    /// and no signal to the RP that its demand had been dropped. For PAR clients the parameter was
    /// even persisted into the pushed payload and then ignored.
    /// </remarks>
    public int? MaxAge { get; init; }

    /// <summary>The raw <c>max_age</c>, kept so a malformed value can be refused rather than
    /// silently treated as absent — which is the failure mode this whole parameter had.</summary>
    public string? RawMaxAge { get; init; }

    /// <summary>The space-delimited <c>prompt</c> values (OIDC Core §3.1.2.1).</summary>
    public string[] Prompts { get; init; } = [];

    /// <summary>Set by <see cref="AuthorizeRequestSupport.Validate"/>.</summary>
    public string[] RequestedScopes { get; set; } = [];

    public static AuthorizeRequest Read(IReadableRequestParameters source)
    {
        var rawMaxAge = source.Get("max_age");
        return new AuthorizeRequest
        {
            RedirectUri = source.Get("redirect_uri"),
            ResponseType = source.Get("response_type"),
            Scope = source.Get("scope"),
            State = source.Get("state"),
            CodeChallenge = source.Get("code_challenge"),
            CodeChallengeMethod = source.Get("code_challenge_method"),
            Nonce = source.Get("nonce"),
            Resources = source.GetAll("resource").Where(r => !string.IsNullOrWhiteSpace(r)).ToArray(),
            RawMaxAge = rawMaxAge,
            MaxAge = int.TryParse(rawMaxAge, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
                ? parsed
                : null,
            Prompts = (source.Get("prompt") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        };
    }

    /// <summary>
    /// True when the session's <paramref name="authTime"/> is older than <see cref="MaxAge"/> allows,
    /// so the OP MUST actively re-authenticate the end-user.
    /// </summary>
    /// <remarks>
    /// A missing <paramref name="authTime"/> with <c>max_age</c> present also demands
    /// re-authentication: the OP cannot show the session is fresh enough, and answering anyway is
    /// exactly the silent non-compliance being fixed.
    /// </remarks>
    public bool RequiresReauthentication(DateTimeOffset? authTime, DateTimeOffset now)
    {
        if (MaxAge is not { } maxAge) return false;
        if (authTime is not { } established) return true;
        return now - established > TimeSpan.FromSeconds(maxAge);
    }
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

        // Refused rather than ignored. `max_age` is a re-authentication DEMAND, so a value the OP
        // cannot parse must not degrade into "no demand" — that is indistinguishable from honouring
        // it, and the RP has no way to tell which happened.
        if (request.RawMaxAge is { Length: > 0 } && request.MaxAge is null)
            return BuildErrorRedirect(redirectUri, "invalid_request", "max_age must be a non-negative integer", state);

        var requestedScopes = request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Ordinal, NOT OrdinalIgnoreCase. RFC 6749 §3.3 makes scope tokens case-sensitive, and matching
        // loosely here was a privilege-escalation path: `Admin` satisfied this check against a registered
        // `admin`, then missed the per-user entitlement gate (IScopeStore point-reads the exact name, so a
        // case variant reads as an unregistered scope, which ScopeRoleGate deliberately leaves alone) — and
        // the IdentityAdmin policy then matched it case-insensitively. Loose here plus exact at the gate
        // plus loose at the policy is the whole bug; a case variant is simply an unknown scope.
        var invalidScopes = requestedScopes.Except(client.AllowedScopes, StringComparer.Ordinal).ToArray();
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

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

    /// <summary>
    /// RFC 9396 <c>authorization_details</c>, read only so it can be REFUSED here.
    /// </summary>
    /// <remarks>
    /// Rich Authorization Requests are implemented on the token-exchange path, not at the
    /// authorization endpoint: nothing here parsed the parameter, ProtocolAuthorizationCode carries
    /// no authority, and the consent screen never saw it. So a client that sent it got a code and an
    /// access token with no authorization_details claim and no error — and RFC 9396 §5 forbids
    /// exactly that: "The AS MUST refuse to process any unknown authorization details type … MUST
    /// abort processing and respond with an error invalid_authorization_details." Silently ignoring
    /// is neither processing nor refusing, and it is the dangerous direction: the client believes it
    /// asked for a constrained grant and received a broader one.
    /// </remarks>
    public string? AuthorizationDetails { get; init; }

    /// <summary>Set by <see cref="AuthorizeRequestSupport.Validate"/>.</summary>
    public string[] RequestedScopes { get; set; } = [];

    /// <summary>
    /// The first single-valued parameter that appeared more than once, or null. OIDC Core §3.1.2.1:
    /// "Request parameters ... MUST NOT be included more than once."
    /// </summary>
    /// <remarks>
    /// Every read took <c>FirstOrDefault()</c>, so <c>?redirect_uri=A&amp;redirect_uri=B</c> resolved
    /// to A and the duplicate was neither rejected nor logged. That was at least self-consistent here
    /// — the URI validated is the one the code is bound to and redirected to — but it leaves the
    /// server's reading of a request differing from what any proxy, log or WAF in front of it parsed,
    /// and the spec's answer is to refuse rather than to pick.
    /// </remarks>
    public string? DuplicatedParameter { get; init; }

    /// <summary>
    /// Single-valued per OIDC Core §3.1.2.1. <c>resource</c> is deliberately absent: RFC 8707 §2
    /// defines it as legitimately repeatable.
    /// </summary>
    private static readonly string[] SingleValuedParameters =
    [
        "client_id", "redirect_uri", "response_type", "scope", "state", "nonce",
        "code_challenge", "code_challenge_method", "prompt", "max_age", "request_uri",
        "authorization_details",
    ];

    /// <summary>
    /// The repeated-parameter scan over the QUERY STRING specifically, for the leg <see cref="Read"/>
    /// structurally cannot cover.
    /// </summary>
    /// <remarks>
    /// When a <c>request_uri</c> is present the parameter source becomes the PAR payload, so
    /// <see cref="Read"/> scans that payload and the query string is never examined — yet the query is
    /// exactly what a proxy, WAF or log pipeline in front of this server parses. Those normalise
    /// duplicates last-wins about as often as .NET's first-wins, so an unscanned query is the
    /// divergence this rule exists to remove: the intermediary records one <c>request_uri</c> and the
    /// AS acts on another. Scanning the query on both legs makes the guarantee unconditional.
    /// </remarks>
    public static string? FindDuplicatedQueryParameter(IReadableRequestParameters query)
        => SingleValuedParameters.FirstOrDefault(p => query.GetAll(p).Count() > 1);

    public static AuthorizeRequest Read(IReadableRequestParameters source)
    {
        var rawMaxAge = source.Get("max_age");
        return new AuthorizeRequest
        {
            DuplicatedParameter = SingleValuedParameters.FirstOrDefault(p => source.GetAll(p).Count() > 1),
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
            AuthorizationDetails = source.Get("authorization_details"),
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

    /// <summary>
    /// OIDC Core §3.1.2.1 <c>prompt=none</c>: "The Authorization Server MUST NOT display any
    /// authentication or consent user interface pages."
    /// </summary>
    /// <remarks>
    /// This is what silent renewal is built on. An RP loads the authorize URL in a hidden iframe and
    /// expects either a code or a named error; instead it got a 302 to the login SPA, which renders a
    /// login form inside an invisible frame the user cannot see. The RP has no way to distinguish
    /// that from a slow response, so silent renewal simply hangs — and on a well-behaved RP the frame
    /// is sandboxed away, so the user is never told their session ended. Worse, the OP is displaying
    /// authentication UI in a framed context, which is where clickjacking against a login form lives.
    /// </remarks>
    public bool NoInteractionAllowed => Prompts.Contains("none", StringComparer.Ordinal);

    /// <summary><c>prompt=login</c> and <c>prompt=select_account</c> both demand a fresh
    /// authentication — the latter because a single-session OP offers account choice by returning the
    /// user to the login screen.</summary>
    public bool DemandsFreshAuthentication =>
        Prompts.Contains("login", StringComparer.Ordinal) || Prompts.Contains("select_account", StringComparer.Ordinal);

    /// <summary><c>prompt=consent</c>: the RP demands the consent screen even where a stored grant
    /// would otherwise satisfy the request.</summary>
    public bool DemandsConsent => Prompts.Contains("consent", StringComparer.Ordinal);
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
    /// <param name="issuer">RFC 9207 <c>iss</c>, carried on the error redirects this builds — see
    /// <see cref="BuildErrorRedirect"/>.</param>
    public static IResult? Validate(OAuthClient client, AuthorizeRequest request, string? issuer = null)
    {
        var (redirectUri, state) = (request.RedirectUri, request.State);

        // OIDC Core §3.1.2.1: "Request parameters ... MUST NOT be included more than once." Checked
        // before anything is acted on, because past this point every read has already silently picked
        // the first occurrence.
        if (request.DuplicatedParameter is { } duplicated)
        {
            // A duplicated redirect_uri or client_id makes the reflection target itself ambiguous, so
            // the error is delivered directly rather than sent to whichever copy happened to win.
            var reflectTo = duplicated is "redirect_uri" or "client_id" ? null
                : !string.IsNullOrWhiteSpace(redirectUri) && IsRedirectUriRegistered(redirectUri, client.RedirectUris)
                    ? redirectUri
                    : null;
            return BuildErrorRedirect(reflectTo, "invalid_request",
                $"{duplicated} must not be provided more than once", state, issuer);
        }

        if (string.IsNullOrWhiteSpace(redirectUri))
            return BuildErrorRedirect(null, "invalid_request", "redirect_uri is required", state, issuer);

        if (!IsRedirectUriRegistered(redirectUri, client.RedirectUris))
            return BuildErrorRedirect(null, "invalid_request", "redirect_uri is not registered for this client", state, issuer);

        if (string.IsNullOrWhiteSpace(request.ResponseType) || request.ResponseType != "code")
            return BuildErrorRedirect(redirectUri, "unsupported_response_type", "Only response_type=code is supported", state, issuer);

        if (string.IsNullOrWhiteSpace(request.Scope))
            return BuildErrorRedirect(redirectUri, "invalid_scope", "scope is required", state, issuer);

        // Refused rather than ignored. `max_age` is a re-authentication DEMAND, so a value the OP
        // cannot parse must not degrade into "no demand" — that is indistinguishable from honouring
        // it, and the RP has no way to tell which happened.
        if (request.RawMaxAge is { Length: > 0 } && request.MaxAge is null)
            return BuildErrorRedirect(redirectUri, "invalid_request", "max_age must be a non-negative integer", state, issuer);

        // RFC 9396 §5 — refused, not ignored. See AuthorizeRequest.AuthorizationDetails for why
        // ignoring is the dangerous direction. The error names where RAR does work, so a client that
        // read authorization_details_types_supported from discovery is told which endpoint honours it
        // rather than being left to guess.
        if (!string.IsNullOrWhiteSpace(request.AuthorizationDetails))
            return BuildErrorRedirect(redirectUri, "invalid_authorization_details",
                "authorization_details is not accepted at the authorization endpoint; "
                + "request rich authorization details on the token endpoint (RFC 8693 exchange)",
                state, issuer);

        // OIDC Core §3.1.2.1: "If this parameter contains none with any other value, an error is
        // returned." The combination is self-contradictory — none forbids UI, every other value asks
        // for some — so honouring either half silently picks one for the RP.
        if (request.NoInteractionAllowed && request.Prompts.Length > 1)
            return BuildErrorRedirect(redirectUri, "invalid_request",
                "prompt=none must not be combined with any other prompt value", state, issuer);

        // Values outside the registry are refused rather than dropped: an RP that sends a prompt the
        // OP does not understand is making a demand, and answering as if it had not is the same
        // silent non-compliance max_age had.
        foreach (var prompt in request.Prompts)
        {
            if (prompt is not ("none" or "login" or "consent" or "select_account"))
                return BuildErrorRedirect(redirectUri, "invalid_request",
                    $"Unsupported prompt value '{prompt}'", state, issuer);
        }

        var requestedScopes = request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Ordinal, NOT OrdinalIgnoreCase. RFC 6749 §3.3 makes scope tokens case-sensitive, and matching
        // loosely here was a privilege-escalation path: `Admin` satisfied this check against a registered
        // `admin`, then missed the per-user entitlement gate (IScopeStore point-reads the exact name, so a
        // case variant reads as an unregistered scope, which ScopeRoleGate deliberately leaves alone) — and
        // the IdentityAdmin policy then matched it case-insensitively. Loose here plus exact at the gate
        // plus loose at the policy is the whole bug; a case variant is simply an unknown scope.
        var invalidScopes = requestedScopes.Except(client.AllowedScopes, StringComparer.Ordinal).ToArray();
        if (invalidScopes.Length > 0)
            return BuildErrorRedirect(redirectUri, "invalid_scope", $"Scopes not allowed: {string.Join(", ", invalidScopes)}", state, issuer);

        // RFC 8707 §2: each resource must be an absolute URI without a fragment, and — when the
        // client declares an audience allowlist — must appear in it. Anything else is invalid_target.
        //
        // An EMPTY Audiences list means "unset", not "deny everything". A dynamically registered
        // client cannot declare audiences (RFC 7591 has no field for them), so treating empty as
        // deny-all made `resource` unusable for every DCR client — which is every MCP client, since
        // the MCP authorization spec requires them to name the MCP server as the resource. The
        // restriction still applies wherever an operator has deliberately configured one.
        //
        // Be exact about what that costs, because §2 also requires invalid_target for a resource the
        // server does not RECOGNISE, and this cannot do that. A client with no Audiences may name any
        // absolute URI and get back an access token whose `aud` is that string, signed by this
        // tenant's key, carrying the requesting user's `sub` and whatever scopes the client is
        // allowed. Naming a resource is not access to it — the value only narrows `aud` — but the
        // remaining check then lives entirely at the resource server, which must authorize on
        // `scope` (or its own model) and must not read a matching iss + aud + sub as permission.
        //
        // Recognising a resource would need a registry of them, and there is none: Scope carries no
        // resource identifier and no IResourceStore exists, so a validation written today would pass
        // whenever the set came back empty — a check that fails open on the exact deployments that
        // never configured anything, which is worse than no check because it reads like one. Left as
        // a stated resource-server obligation (docs/configuration.md, "Audiences and resource
        // indicators") until there is something real to validate against.
        //
        // The convention is NOT uniform, and deliberately so: the RFC 8693 exchange path in
        // ProtocolTokenService reads an empty Audiences as deny, because there the subject token's own
        // `aud` is never consulted and an undeclared target would land verbatim in the minted token.
        // Here the client still has to get a user through an interactive authorization first.
        foreach (var resource in request.Resources)
        {
            if (!Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) || !string.IsNullOrEmpty(resourceUri.Fragment))
                return BuildErrorRedirect(redirectUri, "invalid_target", $"resource '{resource}' is not a valid absolute URI", state, issuer);

            if (client.Audiences.Count > 0 && !client.Audiences.Contains(resource, StringComparer.Ordinal))
                return BuildErrorRedirect(redirectUri, "invalid_target", $"resource '{resource}' is not registered for this client", state, issuer);
        }

        if (client.RequirePkce && string.IsNullOrWhiteSpace(request.CodeChallenge))
            return BuildErrorRedirect(redirectUri, "invalid_request", "code_challenge is required", state, issuer);

        // The method is checked whenever a challenge is present, not only for RequirePkce clients. `plain`
        // makes PKCE decorative — the challenge IS the verifier, so anyone positioned to read the
        // authorization request (the attack PKCE exists to stop) can redeem an intercepted code. RFC 7636
        // §4.3 also defaults a missing method to `plain`, so saying nothing degraded the same way. A
        // client that opts into PKCE without being marked RequirePkce is doing so defensively, and used to
        // get the weakest form of it. Discovery has always advertised S256 only; this agrees with it.
        if (!string.IsNullOrWhiteSpace(request.CodeChallenge)
            && !string.Equals(request.CodeChallengeMethod, "S256", StringComparison.Ordinal))
        {
            return BuildErrorRedirect(redirectUri, "invalid_request", "code_challenge_method must be S256", state, issuer);
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
        // Control characters and whitespace are refused on the RAW string, before Uri sees it. Uri
        // silently trims a trailing TAB or LF, so without this the value compared here is not the
        // value a downstream parser (a proxy, a log, the browser) reads out of the same request.
        foreach (var c in redirectUri)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                return false;
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var requestedUri))
            return false;

        // Reject URIs with fragments (per OAuth spec)
        if (!string.IsNullOrEmpty(requestedUri.Fragment))
            return false;

        // Userinfo has no legitimate role in an OAuth callback, and it was not part of the
        // component-wise comparison below — so https://evil.com@app.example.com/cb matched a
        // registered https://app.example.com/cb, and the redirect was then rebuilt WITH the userinfo
        // intact. Browsers render that host as app.example.com while some clients and log pipelines
        // read the userinfo half as the authority.
        if (!string.IsNullOrEmpty(requestedUri.UserInfo))
            return false;

        foreach (var registered in registeredUris)
        {
            if (!Uri.TryCreate(registered, UriKind.Absolute, out var registeredUri))
                continue;

            // The port is compared EXCEPT on loopback.
            //
            // RFC 8252 §7.3: "the authorization server MUST allow any port to be specified at the
            // time of the request for loopback IP redirect URIs" — a native app binds an ephemeral
            // port at runtime and cannot know it at registration time. Requiring an exact match meant
            // every native app either failed on a port it could not predict or had to register a
            // fixed port, which is the thing §7.3 exists to avoid (a fixed port can be squatted by
            // another local process). Non-loopback hosts still match exactly.
            var bothLoopback = registeredUri.IsLoopback && requestedUri.IsLoopback;

            if (string.Equals(requestedUri.Scheme, registeredUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(requestedUri.Host, registeredUri.Host, StringComparison.OrdinalIgnoreCase) &&
                (bothLoopback || requestedUri.Port == registeredUri.Port) &&
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
    /// <param name="issuer">
    /// RFC 9207 <c>iss</c>. Present on the success path but omitted here, while discovery advertises
    /// <c>authorization_response_iss_parameter_supported: true</c> — so a client that trusts the
    /// advertisement and requires <c>iss</c> on every authorization response (which is what the
    /// parameter is for) had to special-case errors, and a client that merely reads it could not tell
    /// which of several authorization servers an error came back from. That ambiguity is the mix-up
    /// attack the parameter exists to close, and an error response is a perfectly good vehicle for it.
    /// </param>
    public static IResult BuildErrorRedirect(
        string? redirectUri, string error, string errorDescription, string? state, string? issuer = null)
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
        if (!string.IsNullOrWhiteSpace(issuer))
            queryParams["iss"] = issuer;
        uriBuilder.Query = queryParams.ToString();

        return Results.Redirect(uriBuilder.ToString());
    }
}

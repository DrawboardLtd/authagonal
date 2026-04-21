using System.Web;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Authagonal.Protocol.Endpoints;

internal static class AuthorizeEndpoint
{
    public static IEndpointRouteBuilder MapProtocolAuthorizeEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/connect/authorize", async (
            HttpContext httpContext,
            IClientStore clientStore,
            IOidcSubjectResolver subjectResolver,
            IOptions<AuthagonalProtocolOptions> protocolOptions,
            ProtocolAuthorizationCodeService authCodeService,
            ProtocolPushedAuthorizationService parService,
            CancellationToken ct) =>
        {
            var clientId = httpContext.Request.Query["client_id"].FirstOrDefault();
            var initialState = httpContext.Request.Query["state"].FirstOrDefault();
            var requestUri = httpContext.Request.Query["request_uri"].FirstOrDefault();
            // Pre-lookup redirect-back target — only honoured for non-PAR flow, since a PAR
            // request keeps redirect_uri inside the pushed payload.
            var initialRedirectUri = string.IsNullOrWhiteSpace(requestUri)
                ? httpContext.Request.Query["redirect_uri"].FirstOrDefault()
                : null;

            if (string.IsNullOrWhiteSpace(clientId))
                return BuildErrorRedirect(initialRedirectUri, "invalid_request", "client_id is required", initialState);

            var client = await clientStore.GetAsync(clientId, ct);
            if (client is null)
                return BuildErrorRedirect(initialRedirectUri, "unauthorized_client", "Unknown client_id", initialState);

            IReadableRequestParameters source;
            if (!string.IsNullOrWhiteSpace(requestUri))
            {
                var record = await parService.LoadAsync(requestUri, clientId, ct);
                if (record is null)
                    return BuildErrorRedirect(null, "invalid_request", "request_uri is unknown, expired, or already consumed", initialState);
                source = new ParRequestParameters(record.Parameters);
            }
            else
            {
                if (client.RequirePushedAuthorizationRequests)
                    return BuildErrorRedirect(null, "invalid_request", "This client requires requests to be pushed via /connect/par", initialState);
                source = new QueryRequestParameters(httpContext.Request.Query);
            }

            var redirectUri = source.Get("redirect_uri");
            var responseType = source.Get("response_type");
            var scope = source.Get("scope");
            var state = source.Get("state");
            var codeChallenge = source.Get("code_challenge");
            var codeChallengeMethod = source.Get("code_challenge_method");
            var nonce = source.Get("nonce");
            var resources = source.GetAll("resource").Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();

            if (!client.Enabled)
                return BuildErrorRedirect(redirectUri, "unauthorized_client", "Client is disabled", state);

            if (string.IsNullOrWhiteSpace(redirectUri))
                return BuildErrorRedirect(null, "invalid_request", "redirect_uri is required", state);

            if (!IsRedirectUriRegistered(redirectUri, client.RedirectUris))
                return BuildErrorRedirect(null, "invalid_request", "redirect_uri is not registered for this client", state);

            if (string.IsNullOrWhiteSpace(responseType) || responseType != "code")
                return BuildErrorRedirect(redirectUri, "unsupported_response_type", "Only response_type=code is supported", state);

            if (string.IsNullOrWhiteSpace(scope))
                return BuildErrorRedirect(redirectUri, "invalid_scope", "scope is required", state);

            var requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var invalidScopes = requestedScopes.Except(client.AllowedScopes, StringComparer.OrdinalIgnoreCase).ToArray();
            if (invalidScopes.Length > 0)
                return BuildErrorRedirect(redirectUri, "invalid_scope", $"Scopes not allowed: {string.Join(", ", invalidScopes)}", state);

            // RFC 8707: validate resource indicators against registered audiences.
            foreach (var resource in resources)
            {
                if (!Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) || !string.IsNullOrEmpty(resourceUri.Fragment))
                    return BuildErrorRedirect(redirectUri, "invalid_target", $"resource '{resource}' is not a valid absolute URI", state);

                if (!client.Audiences.Contains(resource, StringComparer.Ordinal))
                    return BuildErrorRedirect(redirectUri, "invalid_target", $"resource '{resource}' is not registered for this client", state);
            }

            if (client.RequirePkce)
            {
                if (string.IsNullOrWhiteSpace(codeChallenge))
                    return BuildErrorRedirect(redirectUri, "invalid_request", "code_challenge is required", state);

                if (string.IsNullOrWhiteSpace(codeChallengeMethod) || codeChallengeMethod != "S256")
                    return BuildErrorRedirect(redirectUri, "invalid_request", "code_challenge_method must be S256", state);
            }

            // Authenticate — if the caller isn't already, challenge the host's registered scheme.
            var authScheme = protocolOptions.Value.AuthenticationScheme;
            if (httpContext.User.Identity?.IsAuthenticated != true)
            {
                return Results.Challenge(
                    new AuthenticationProperties
                    {
                        RedirectUri = $"{httpContext.Request.Path}{httpContext.Request.QueryString}"
                    },
                    [authScheme]);
            }

            var context = new OidcSubjectResolutionContext(clientId, requestedScopes, resources);
            var resolved = await subjectResolver.ResolveAsync(httpContext.User, context, ct);

            if (resolved is OidcSubjectResult.Rejected rejected)
            {
                var err = rejected.Reason switch
                {
                    OidcRejection.LoginRequired => "login_required",
                    OidcRejection.ConsentRequired => "consent_required",
                    OidcRejection.AccountSelectionRequired => "account_selection_required",
                    _ => "access_denied",
                };
                return BuildErrorRedirect(redirectUri, err, rejected.Description ?? "Subject not permitted", state);
            }

            var subject = ((OidcSubjectResult.Allowed)resolved).Subject;

            var code = await authCodeService.CreateCodeAsync(
                clientId,
                subject,
                redirectUri,
                requestedScopes.ToList(),
                codeChallenge,
                codeChallengeMethod,
                nonce,
                resources.Length > 0 ? resources : null,
                ct);

            if (!string.IsNullOrWhiteSpace(requestUri))
                await parService.RemoveAsync(requestUri, ct);

            var uriBuilder = new UriBuilder(redirectUri);
            var queryParams = HttpUtility.ParseQueryString(uriBuilder.Query);
            queryParams["code"] = code;
            if (!string.IsNullOrWhiteSpace(state))
                queryParams["state"] = state;
            uriBuilder.Query = queryParams.ToString();

            return Results.Redirect(uriBuilder.ToString());
        })
        .AllowAnonymous()
        .WithTags("OIDC");

        return app;
    }

    /// <summary>
    /// Compares redirect URIs using normalized form (scheme, host, port, path, query) to prevent
    /// bypass via implicit ports, trailing slashes, or encoding differences.
    /// </summary>
    private static bool IsRedirectUriRegistered(string redirectUri, IReadOnlyList<string> registeredUris)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var requestedUri))
            return false;

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

    private static IResult BuildErrorRedirect(string? redirectUri, string error, string errorDescription, string? state)
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

    private interface IReadableRequestParameters
    {
        string? Get(string key);
        IEnumerable<string> GetAll(string key);
    }

    private sealed class QueryRequestParameters(IQueryCollection query) : IReadableRequestParameters
    {
        public string? Get(string key) => query[key].FirstOrDefault();
        public IEnumerable<string> GetAll(string key) => query[key].Where(v => v is not null).Cast<string>();
    }

    private sealed class ParRequestParameters(Dictionary<string, string[]> parameters) : IReadableRequestParameters
    {
        public string? Get(string key) => parameters.TryGetValue(key, out var values) ? values.FirstOrDefault() : null;
        public IEnumerable<string> GetAll(string key) => parameters.TryGetValue(key, out var values) ? values : [];
    }
}

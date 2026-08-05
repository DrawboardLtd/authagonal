using System.Diagnostics;
using Authagonal.Core.Constants;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Authagonal.Core.Services;

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
            IEnumerable<IAuthHook> authHooks,
            CancellationToken ct) =>
        {
            var form = await httpContext.Request.ReadFormAsync(ct);

            var (client, authError) = await ClientAuthentication.AuthenticateAsync(
                httpContext, form, clientStore, secretVerifier, TokenGrantHandlers.TokenError, ct);
            if (authError is not null)
                return authError;

            var grantType = form["grant_type"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(grantType))
                return TokenGrantHandlers.TokenError("invalid_request", "grant_type is required");

            if (!client!.AllowedGrantTypes.Contains(grantType, StringComparer.OrdinalIgnoreCase))
                return TokenGrantHandlers.TokenError("unauthorized_client", "Grant type not allowed for this client");

            if (grantType is not (GrantTypes.AuthorizationCode or GrantTypes.RefreshToken or GrantTypes.ClientCredentials or GrantTypes.TokenExchange))
                return TokenGrantHandlers.TokenError("unsupported_grant_type", $"Grant type '{grantType}' is not supported");

            try
            {
                // The rejection gate, which this host never invoked for any grant.
                //
                // IAuthHook.OnTokenIssuedAsync is documented as "Throw to reject the token issuance", and
                // docs/extensibility.md lists it among the core lifecycle hooks and repeats that methods on the
                // critical path can abort the operation. The Server host called it; the embeddable package —
                // the one an integrator reaches for precisely to keep their own policy in the loop — did not,
                // so a hook registered to refuse issuance for a suspended tenant was accepted at registration
                // and never consulted. Before minting, so a rejection persists nothing.
                await authHooks.RunOnTokenIssuedAsync(null, client.ClientId, grantType, ct);

                return grantType switch
                {
                    GrantTypes.AuthorizationCode => await TokenGrantHandlers.HandleAuthorizationCode(form, tokenService, client.ClientId, ct),
                    GrantTypes.RefreshToken => await TokenGrantHandlers.HandleRefreshToken(form, tokenService, client.ClientId, ct),
                    GrantTypes.ClientCredentials => await TokenGrantHandlers.HandleClientCredentials(form, tokenService, client.ClientId, ct),
                    GrantTypes.TokenExchange => await TokenGrantHandlers.HandleTokenExchange(form, tokenService, client.ClientId, ct),
                    _ => throw new UnreachableException()
                };
            }
            catch (InvalidOperationException ex)
            {
                return TokenGrantHandlers.TokenError("invalid_grant", ex.Message);
            }
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .RequireTls()
        .WithTags("OIDC");

        return app;
    }
}

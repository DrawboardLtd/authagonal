using System.Text;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Endpoints;

public static class RevocationEndpoint
{
    public static IEndpointRouteBuilder MapRevocationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/revocation", async (
            HttpContext httpContext,
            ITokenService tokenService,
            IClientStore clientStore,
            CancellationToken ct) =>
        {
            var form = await httpContext.Request.ReadFormAsync(ct);

            // Authenticate client
            var (clientId, clientSecret) = ExtractClientCredentials(httpContext, form);

            if (string.IsNullOrWhiteSpace(clientId))
                return Results.Json(new { error = "invalid_client", error_description = "client_id is required" }, statusCode: 401);

            var client = await clientStore.FindByIdAsync(clientId, ct);
            if (client is null)
                return Results.Json(new { error = "invalid_client", error_description = "Unknown client" }, statusCode: 401);

            if (client.RequireClientSecret)
            {
                if (string.IsNullOrWhiteSpace(clientSecret))
                    return Results.Json(new { error = "invalid_client", error_description = "client_secret is required" }, statusCode: 401);

                var secretValid = client.ClientSecretHashes.Any(hash =>
                {
                    var hasher = new Services.PasswordHasher();
                    var result = hasher.VerifyPassword(clientSecret, hash);
                    return result is Services.PasswordVerifyResult.Success or Services.PasswordVerifyResult.SuccessRehashNeeded;
                });

                if (!secretValid)
                    return Results.Json(new { error = "invalid_client", error_description = "Invalid client credentials" }, statusCode: 401);
            }

            var token = form["token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token))
                return Results.Ok(); // Per RFC 7009, invalid tokens are not an error

            var tokenTypeHint = form["token_type_hint"].FirstOrDefault();

            // We only support revoking refresh tokens currently
            if (tokenTypeHint is null or "refresh_token")
            {
                await tokenService.RevokeRefreshTokenAsync(token, clientId, ct);
            }

            // Per RFC 7009, always return 200 OK
            return Results.Ok();
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .WithTags("OAuth");

        return app;
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
            catch (FormatException)
            {
                // Fall through
            }
        }

        return (form["client_id"].FirstOrDefault(), form["client_secret"].FirstOrDefault());
    }
}

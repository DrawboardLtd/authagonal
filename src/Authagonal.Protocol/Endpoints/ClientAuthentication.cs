using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Protocol.Services;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Protocol.Endpoints;

/// <summary>
/// OAuth client authentication shared by the token, PAR, and (Server-side) device endpoints:
/// client_secret_basic with a client_secret_post fallback, then the standard
/// lookup → enabled → secret-verification sequence.
/// </summary>
internal static class ClientAuthentication
{
    public static (string? ClientId, string? ClientSecret) ExtractClientCredentials(
        HttpContext httpContext, IFormCollection form)
    {
        // Try Authorization header first (client_secret_basic)
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
                // Malformed Basic header; fall through to form body
            }
        }

        // Fall back to client_secret_post (form body)
        return (form["client_id"].FirstOrDefault(), form["client_secret"].FirstOrDefault());
    }

    /// <summary>
    /// Authenticates the calling client. <paramref name="error"/> maps (error, description) to the
    /// endpoint's error result — endpoints differ on status codes (the token endpoint returns 400
    /// for a disabled client, PAR returns 401), so the mapping stays with the caller.
    /// </summary>
    public static async Task<(OAuthClient? Client, IResult? Error)> AuthenticateAsync(
        HttpContext httpContext,
        IFormCollection form,
        IClientStore clientStore,
        IClientSecretVerifier secretVerifier,
        Func<string, string, IResult> error,
        CancellationToken ct)
    {
        var (clientId, clientSecret) = ExtractClientCredentials(httpContext, form);

        if (string.IsNullOrWhiteSpace(clientId))
            return (null, error("invalid_client", "client_id is required"));

        var client = await clientStore.GetAsync(clientId, ct);
        if (client is null)
            return (null, error("invalid_client", "Unknown client"));

        if (!client.Enabled)
            return (null, error("unauthorized_client", "Client is disabled"));

        if (client.RequireClientSecret)
        {
            if (string.IsNullOrWhiteSpace(clientSecret))
                return (null, error("invalid_client", "client_secret is required"));

            if (!await secretVerifier.VerifyAsync(client, clientSecret, ct))
                return (null, error("invalid_client", "Invalid client credentials"));
        }

        return (client, null);
    }
}

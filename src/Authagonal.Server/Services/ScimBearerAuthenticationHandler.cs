using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Authagonal.Core.Stores;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services;

public sealed class ScimBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IScimTokenStore scimTokenStore,
    IClientStore clientStore)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var rawToken = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(rawToken))
        {
            return AuthenticateResult.Fail("Empty bearer token");
        }

        // Hash the token for lookup
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

        Logger.LogInformation("SCIM auth attempt: token length={Length}, hash prefix={HashPrefix}",
            rawToken.Length, tokenHash[..12]);

        var scimToken = await scimTokenStore.FindByHashAsync(tokenHash);
        if (scimToken is null)
        {
            Logger.LogWarning("SCIM token not found for hash prefix {HashPrefix}", tokenHash[..12]);
            return AuthenticateResult.Fail("Invalid SCIM token");
        }

        if (scimToken.IsRevoked)
        {
            return AuthenticateResult.Fail("SCIM token has been revoked");
        }

        if (scimToken.ExpiresAt.HasValue && scimToken.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            return AuthenticateResult.Fail("SCIM token has expired");
        }

        // Resolve client to get organization scoping
        var client = await clientStore.GetAsync(scimToken.ClientId);
        if (client is null)
        {
            return AuthenticateResult.Fail("Client not found for SCIM token");
        }

        var claims = new List<Claim>
        {
            new("client_id", scimToken.ClientId),
            new("token_id", scimToken.TokenId),
        };

        // For org scoping, we need a convention to derive org from client.
        // Use a claim if available, or fall back to clientId as the org scope.
        // The SCIM endpoints will use client_id for scoping.
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}

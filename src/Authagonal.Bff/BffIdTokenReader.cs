using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Bff;

/// <summary>Reads a refreshed id_token for its claims — after validating it the way the login
/// handshake validates the first one. Null on any failure: the session keeps the claims it has.</summary>
public interface IBffIdTokenReader
{
    Task<JsonWebToken?> TryReadAsync(BffTenantConfig tenant, string idToken, CancellationToken ct = default);
}

internal sealed class BffIdTokenReader(BffOidcConfig oidc, ILogger<BffIdTokenReader> log) : IBffIdTokenReader
{
    public async Task<JsonWebToken?> TryReadAsync(BffTenantConfig tenant, string idToken, CancellationToken ct = default)
    {
        var config = await oidc.GetAsync(tenant.Authority, ct);
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = config.Issuer,
            ValidAudience = tenant.ClientId,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
            ValidAlgorithms = BffClaims.AsymmetricSigningAlgorithms,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
        });
        if (validation.IsValid)
            return (JsonWebToken)validation.SecurityToken;
        log.LogWarning(validation.Exception, "BFF refreshed id_token failed validation; session claims kept.");
        return null;
    }
}

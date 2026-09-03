using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Bff;

/// <summary>
/// What a session keeps of an id_token, and the algorithms any inbound token may wear. Shared by
/// the login handshake and the refresh path: a session's claims are re-read from every refreshed
/// id_token, so a role granted after login reaches the seat at the next refresh rather than the
/// next login (a 30-day persistent session carried the login-time claims for its whole life).
/// </summary>
internal static class BffClaims
{
    internal static readonly HashSet<string> ProtocolClaims = new(StringComparer.Ordinal)
    {
        "iss", "aud", "exp", "iat", "nbf", "nonce", "at_hash", "c_hash", "s_hash",
        "azp", "jti", "sid", "auth_time", "acr", "amr", "typ",
    };

    internal static readonly string[] AsymmetricSigningAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    internal static Dictionary<string, string> Extract(JsonWebToken jwt)
    {
        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var claim in jwt.Claims)
        {
            if (ProtocolClaims.Contains(claim.Type)) continue;
            // Array claims (roles, groups) arrive as repeated claim types — space-join so the SPA
            // sees the full set (previously only the first value survived). NOTE: this assumes individual
            // values contain no spaces (true for roles/groups); a value with an embedded space would be
            // indistinguishable from two separate values downstream.
            claims[claim.Type] = claims.TryGetValue(claim.Type, out var existing)
                ? $"{existing} {claim.Value}"
                : claim.Value;
        }
        return claims;
    }
}

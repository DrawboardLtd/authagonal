using System.Security.Cryptography;
using System.Text.Json;
using Authagonal.Bff;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Authagonal.Tests;

/// <summary>
/// The BFF proxy's per-route authority gate: the outgoing bearer's authorization_details
/// claim must permit every declared "type:action" pair or the route is a 403.
/// </summary>
public sealed class BffAuthorityGateTests
{
    private static string Mint(string? authorizationDetailsJson)
    {
        var claims = new Dictionary<string, object> { ["sub"] = "user-1" };
        if (authorizationDetailsJson is not null)
        {
            using var doc = JsonDocument.Parse(authorizationDetailsJson);
            claims["authorization_details"] = doc.RootElement.Clone();
        }
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://test",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), SecurityAlgorithms.HmacSha256),
        });
    }

    [Fact]
    public void TokenWithMatchingAuthority_Passes()
    {
        var bearer = Mint("""[{"type":"email","actions":["send","read"]}]""");
        Assert.True(BffProxy.PermitsRequiredAuthority(bearer, ["email:send"]));
        Assert.True(BffProxy.PermitsRequiredAuthority(bearer, ["email:send", "email:read"]));
    }

    [Fact]
    public void TokenMissingAnAction_IsRefused()
    {
        var bearer = Mint("""[{"type":"email","actions":["read"]}]""");
        Assert.False(BffProxy.PermitsRequiredAuthority(bearer, ["email:send"]));
        Assert.False(BffProxy.PermitsRequiredAuthority(bearer, ["email:read", "calendar:read"]));
    }

    [Fact]
    public void DenyPolicy_IsRefused()
    {
        var bearer = Mint("""[{"type":"email","actions":["send"],"action_policies":{"send":"deny"}}]""");
        Assert.False(BffProxy.PermitsRequiredAuthority(bearer, ["email:send"]));
    }

    [Fact]
    public void LegacyTokenWithoutClaim_Passes()
    {
        Assert.True(BffProxy.PermitsRequiredAuthority(Mint(null), ["email:send"]));
    }

    [Fact]
    public void NamespacedConnectorType_SplitsOnLastColon()
    {
        var bearer = Mint("""[{"type":"mcp:tools.internal","actions":["search_docs"]}]""");
        Assert.True(BffProxy.PermitsRequiredAuthority(bearer, ["mcp:tools.internal:search_docs"]));
        Assert.False(BffProxy.PermitsRequiredAuthority(bearer, ["mcp:tools.internal:deploy"]));
    }

    [Fact]
    public void MalformedInputs_FailClosed()
    {
        var bearer = Mint("""[{"type":"email","actions":["send"]}]""");
        Assert.False(BffProxy.PermitsRequiredAuthority("not-a-jwt", ["email:send"]));
        Assert.False(BffProxy.PermitsRequiredAuthority(bearer, ["no-colon"]));
        Assert.False(BffProxy.PermitsRequiredAuthority(bearer, ["email:"]));
    }
}

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

    /// <summary>
    /// The gate now says WHERE as well as what. Until it did, `locations` was parsed, intersected and
    /// written into the token and then ignored by every evaluator, so a token scoped to one resource
    /// server spent its authority at any other upstream the same BFF fronted.
    /// </summary>
    [Fact]
    public void TokenPinnedToAnotherResourceServer_IsRefusedAtThisUpstream()
    {
        var bearer = Mint("""[{"type":"email","actions":["send"],"locations":["https://mail.acme.example"]}]""");

        Assert.True(BffProxy.PermitsRequiredAuthority(
            bearer, ["email:send"], "https://mail.acme.example/v1/send"));
        Assert.False(BffProxy.PermitsRequiredAuthority(
            bearer, ["email:send"], "https://crm.acme.example/v1/send"));

        // An unpinned grant is unaffected — the claim only ever narrows.
        var unpinned = Mint("""[{"type":"email","actions":["send"]}]""");
        Assert.True(BffProxy.PermitsRequiredAuthority(
            unpinned, ["email:send"], "https://crm.acme.example/v1/send"));
    }

    /// <summary>
    /// The proxy forwards blind, so a constraint it supplies no context for is skipped by default and
    /// left to the upstream. For an upstream that does not read the claim itself, "the BFF is the
    /// enforcement chokepoint" is only true if the chokepoint refuses what it cannot check.
    /// </summary>
    [Fact]
    public void StrictAuthority_RefusesAConstraintTheProxyCannotEvaluate()
    {
        var bearer = Mint("""[{"type":"email","actions":["send"],"recipient_domains":["acme.example"]}]""");

        Assert.True(BffProxy.PermitsRequiredAuthority(bearer, ["email:send"]));
        Assert.False(BffProxy.PermitsRequiredAuthority(bearer, ["email:send"], location: null, strict: true));

        // Strict is about UNEVALUATED constraints, not about having any: an unconstrained grant passes.
        var plain = Mint("""[{"type":"email","actions":["send"]}]""");
        Assert.True(BffProxy.PermitsRequiredAuthority(plain, ["email:send"], location: null, strict: true));
    }

    [Fact]
    public void AuthorityLocation_IsTheUpstreamTheRequestWillActuallyReach()
    {
        var upstream = new BffUpstream { Prefix = "/orders", TargetBaseUrl = "https://api.internal.acme/" };

        Assert.Equal("https://api.internal.acme/orders/17", BffProxy.AuthorityLocationFor(upstream, "/orders/17"));
        Assert.Equal("https://api.internal.acme", BffProxy.AuthorityLocationFor(upstream, ""));

        // Declared override, for authority minted against a public resource identifier that differs
        // from the internal address the proxy dials.
        upstream.AuthorityLocation = "https://api.acme.example";
        Assert.Equal("https://api.acme.example/orders/17", BffProxy.AuthorityLocationFor(upstream, "/orders/17"));
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

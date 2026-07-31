using Authagonal.Core.Authority;

namespace Authagonal.Tests;

/// <summary>
/// Authority-algebra and agent-profile properties that an admin's explicit decision depends on.
/// </summary>
public class AgentAuthorityHardeningTests
{
    private static AuthoritySet ParseAuthority(string json)
    {
        Assert.True(AuthorityJson.TryParse(json, out var set), $"could not parse: {json}");
        return set;
    }

    // -----------------------------------------------------------------------
    // F222 — an explicit `auto` must survive Intersect
    // -----------------------------------------------------------------------

    [Fact]
    public void ExplicitAutoPolicy_SurvivesIntersect()
    {
        // Auto is the enum's zero, and the meet dropped it as if nothing had been said — making an
        // explicit auto indistinguishable from silence. Downstream reads silence as "apply the
        // profile's HighRiskDefault", so an admin who deliberately marked a high-risk action auto had
        // that erased by any Intersect and the action fell back to ask or deny: precisely the
        // behaviour the explicit setting exists to override.
        var explicitAuto = ParseAuthority("""
            [{"type":"payments","actions":["initiate"],"action_policies":{"initiate":"auto"}}]
            """);
        var alsoAuto = ParseAuthority("""
            [{"type":"payments","actions":["initiate"],"action_policies":{"initiate":"auto"}}]
            """);

        var merged = explicitAuto.Intersect(alsoAuto);
        var grant = Assert.Single(merged.Grants);

        Assert.True(grant.ActionPolicies.ContainsKey("initiate"),
            "the explicit auto policy was dropped by the meet");
        Assert.Equal(ActionPolicy.Auto, grant.ActionPolicies["initiate"]);
    }

    [Fact]
    public void StricterPolicyStillWinsTheMeet()
    {
        // The meet must still take the stricter of the two — recording auto must not weaken it.
        var auto = ParseAuthority("""
            [{"type":"payments","actions":["initiate"],"action_policies":{"initiate":"auto"}}]
            """);
        var ask = ParseAuthority("""
            [{"type":"payments","actions":["initiate"],"action_policies":{"initiate":"ask"}}]
            """);

        var grant = Assert.Single(auto.Intersect(ask).Grants);
        Assert.Equal(ActionPolicy.Ask, grant.ActionPolicies["initiate"]);
    }

    [Fact]
    public void SilenceIsStillSilence()
    {
        // A grant that says nothing about an action must not gain a policy entry, or every action
        // would look explicitly configured and HighRiskDefault would never apply.
        var a = ParseAuthority("""[{"type":"payments","actions":["initiate"]}]""");
        var b = ParseAuthority("""[{"type":"payments","actions":["initiate"]}]""");

        var grant = Assert.Single(a.Intersect(b).Grants);
        Assert.False(grant.ActionPolicies.ContainsKey("initiate"));
    }
}

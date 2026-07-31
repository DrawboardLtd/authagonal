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

    // -----------------------------------------------------------------------
    // F148 / F149 — the two ways evaluation failed open
    // -----------------------------------------------------------------------

    [Fact]
    public void EmptyAllowlist_DeniesRatherThanPermitting()
    {
        // An allowlist that admits nothing is bottom, like Nothing. Treating it as "no constraint"
        // inverted its meaning: the residue of a conflicting intersection PERMITTED everything.
        var set = ParseAuthority("""
            [{"type":"payments","actions":["initiate"],"recipient_domains":[]}]
            """);

        Assert.False(set.Permits("payments", "initiate", location: null,
            context: new Dictionary<string, string> { ["recipient_domains"] = "acme.example" }));
    }

    [Fact]
    public void StrictMode_DeniesWhenTheCallerSuppliedNoContextForAConstraint()
    {
        var set = ParseAuthority("""
            [{"type":"payments","actions":["initiate"],"recipient_domains":["acme.example"]}]
            """);

        // Default (lenient) keeps the documented behaviour: the resource server is trusted to pass
        // every key it knows about.
        Assert.True(set.Permits("payments", "initiate"));

        // Strict inverts it for callers that can enumerate what they support — a server that simply
        // FORGETS to pass recipient_domains otherwise gets an unconstrained grant, silently.
        Assert.False(set.Permits("payments", "initiate", location: null, context: null, strict: true));
    }

    [Fact]
    public void Locations_NarrowTheGrant()
    {
        // locations was parsed, intersected and emitted, and consulted by no evaluator — so the one
        // part of a grant that says WHERE the authority applies never narrowed anything.
        var set = ParseAuthority("""
            [{"type":"payments","actions":["initiate"],"locations":["https://api.acme.example"]}]
            """);

        Assert.True(set.Permits("payments", "initiate", "https://api.acme.example"));
        Assert.False(set.Permits("payments", "initiate", "https://api.evil.example"));

        // A caller that does not know its location is unaffected, so this cannot break an evaluator
        // that has not been taught about locations yet.
        Assert.True(set.Permits("payments", "initiate"));
    }
}

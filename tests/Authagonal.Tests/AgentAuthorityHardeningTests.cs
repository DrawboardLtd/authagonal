using System.Security.Claims;
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

    [Fact]
    public void Locations_MatchAsResourceRoots_NotAsExactStrings()
    {
        // A grant names the resource server; the caller presents the concrete thing it is acting on.
        // Exact string equality would have made every real presented location miss — the same
        // fail-open as never checking at all.
        var set = ParseAuthority("""
            [{"type":"payments","actions":["initiate"],"locations":["https://api.acme.example/orders"]}]
            """);

        Assert.True(set.Permits("payments", "initiate", "https://api.acme.example/orders"));
        Assert.True(set.Permits("payments", "initiate", "https://api.acme.example/orders/17"));
        Assert.True(set.Permits("payments", "initiate", "https://API.ACME.example/orders/17"));

        // Containment is on a segment boundary and inside the same origin, so neither a
        // longest-prefix sibling nor a look-alike host slips through.
        Assert.False(set.Permits("payments", "initiate", "https://api.acme.example/orders-admin"));
        Assert.False(set.Permits("payments", "initiate", "https://api.acme.example.evil/orders"));
        Assert.False(set.Permits("payments", "initiate", "http://api.acme.example/orders"));
        Assert.False(set.Permits("payments", "initiate", "https://api.acme.example/"));

        // A non-URI location (a connector id, a queue name) still compares ordinally.
        var opaque = ParseAuthority("""[{"type":"queue","actions":["publish"],"locations":["orders-q"]}]""");
        Assert.True(opaque.Permits("queue", "publish", "orders-q"));
        Assert.False(opaque.Permits("queue", "publish", "orders-q-dlq"));
    }

    [Fact]
    public void ResourceSideEvaluator_CanReachLocationsAndStrictMode()
    {
        // Both narrowings existed on AuthoritySet and neither was reachable from AuthorityEvaluator,
        // the documented resource-side entry point — so `locations` narrowed nothing anywhere in the
        // product, and a resource server had no supported way to say "deny what I could not evaluate".
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(AuthorityClaims.AuthorizationDetails, """
                {"type":"payments","actions":["initiate"],
                 "locations":["https://api.acme.example"],"recipient_domains":["acme.example"]}
                """),
        ]));

        Assert.True(AuthorityEvaluator.Permits(principal, "payments", "initiate"));
        Assert.True(AuthorityEvaluator.Permits(
            principal, "payments", "initiate", context: null, location: "https://api.acme.example/pay"));
        Assert.False(AuthorityEvaluator.Permits(
            principal, "payments", "initiate", context: null, location: "https://api.evil.example/pay"));
        Assert.False(AuthorityEvaluator.Permits(
            principal, "payments", "initiate", context: null, location: null, strict: true));

        // And the caller can find out WHICH restriction it never evaluated, rather than only that
        // something refused.
        var set = AuthorityEvaluator.FromPrincipal(principal);
        Assert.Equal(["recipient_domains"], set.UncheckedConstraints("payments"));
        Assert.Empty(set.UncheckedConstraints(
            "payments", new Dictionary<string, string> { ["recipient_domains"] = "acme.example" }));
        Assert.Empty(set.UncheckedConstraints("no-such-type"));
    }

    [Fact]
    public void NonArrayLocationsOrActions_AreRefused_NotReadAsUnrestricted()
    {
        // A bare string was read as an EMPTY list, and empty means unrestricted for locations — so the
        // shape a hand-written authorization_details most often gets wrong quietly promoted a grant
        // pinned to one resource server into one that applies everywhere.
        Assert.False(AuthorityJson.TryParse(
            """[{"type":"payments","actions":["initiate"],"locations":"https://api.acme.example"}]""", out _));
        Assert.False(AuthorityJson.TryParse("""[{"type":"payments","actions":"initiate"}]""", out _));
        Assert.False(AuthorityJson.TryParse("""[{"type":"payments","actions":{"send":true}}]""", out _));

        // A non-string element inside the array was dropped just as silently — the same widening, one
        // entry at a time.
        Assert.False(AuthorityJson.TryParse(
            """[{"type":"payments","actions":["initiate"],"locations":["https://a.example",7]}]""", out _));

        // Absent — and an explicit JSON null — still mean genuinely unspecified.
        Assert.True(AuthorityJson.TryParse("""[{"type":"payments","actions":["initiate"]}]""", out var open));
        Assert.True(open.Permits("payments", "initiate", "https://anywhere.example"));
        Assert.True(AuthorityJson.TryParse(
            """[{"type":"payments","actions":["initiate"],"locations":null}]""", out _));
    }
}

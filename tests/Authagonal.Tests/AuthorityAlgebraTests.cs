using Authagonal.Core.Authority;

namespace Authagonal.Tests;

/// <summary>
/// The authority algebra's load-bearing property: an intersection never permits anything
/// either operand refused. Everything agentic (ceiling ∩ consent ∩ request ∩ subject) is
/// chained Intersect calls, so these are the safety proofs for the whole model.
/// </summary>
public sealed class AuthorityAlgebraTests
{
    private static AuthorityGrant Email(
        string[] actions,
        Dictionary<string, ActionPolicy>? policies = null,
        Dictionary<string, ConstraintValue>? constraints = null) => new()
    {
        Type = "email",
        Actions = actions,
        ActionPolicies = policies ?? new Dictionary<string, ActionPolicy>(),
        Constraints = constraints ?? new Dictionary<string, ConstraintValue>(),
    };

    // ── intersection semantics ──────────────────────────────────────────────────────────

    [Fact]
    public void Intersect_DropsTypesNotOnBothSides()
    {
        var a = AuthoritySet.Of(Email(["send"]), new AuthorityGrant { Type = "calendar", Actions = ["read"] });
        var b = AuthoritySet.Of(Email(["send"]));

        var result = a.Intersect(b);

        Assert.Single(result.Grants);
        Assert.Equal("email", result.Grants[0].Type);
    }

    [Fact]
    public void Intersect_ActionsAreSetIntersection()
    {
        var a = AuthoritySet.Of(Email(["send", "read", "manage_labels"]));
        var b = AuthoritySet.Of(Email(["read", "send"]));

        var result = a.Intersect(b);

        Assert.Equal(["send", "read"], result.Grants[0].Actions);
    }

    [Fact]
    public void Intersect_PolicyTakesMostRestrictive()
    {
        var ceiling = AuthoritySet.Of(Email(["send", "read"],
            policies: new() { ["send"] = ActionPolicy.Auto }));
        var consent = AuthoritySet.Of(Email(["send", "read"],
            policies: new() { ["send"] = ActionPolicy.Ask, ["read"] = ActionPolicy.Deny }));

        var result = ceiling.Intersect(consent);

        Assert.Equal(ActionPolicy.Ask, result.PolicyFor("email", "send"));
        Assert.Equal(ActionPolicy.Deny, result.PolicyFor("email", "read"));
    }

    [Fact]
    public void Intersect_ConstraintOnOneSideOnly_CarriesOver()
    {
        var a = AuthoritySet.Of(Email(["send"],
            constraints: new() { ["recipient_domains"] = ConstraintValue.Of("@acme.com") }));
        var b = AuthoritySet.Of(Email(["send"]));

        var result = a.Intersect(b);

        Assert.False(result.Permits("email", "send",
            new Dictionary<string, string> { ["recipient_domains"] = "bob@evil.example" }));
        Assert.True(result.Permits("email", "send",
            new Dictionary<string, string> { ["recipient_domains"] = "bob@acme.com" }));
    }

    [Fact]
    public void ConstraintMeet_ByShape()
    {
        // string sets intersect
        var sets = ConstraintValue.Meet(ConstraintValue.Of("a", "b"), ConstraintValue.Of("b", "c"));
        Assert.Equal(["b"], Assert.IsType<ConstraintValue.StringSet>(sets).Values);

        // numbers take the min
        var numbers = ConstraintValue.Meet(ConstraintValue.Of(50m), ConstraintValue.Of(20m));
        Assert.Equal(20m, Assert.IsType<ConstraintValue.Number>(numbers).Value);

        // booleans AND
        var flags = ConstraintValue.Meet(ConstraintValue.Of(true), ConstraintValue.Of(false));
        Assert.False(Assert.IsType<ConstraintValue.Flag>(flags).Value);

        // kind mismatch fails closed
        var mismatch = ConstraintValue.Meet(ConstraintValue.Of(50m), ConstraintValue.Of("a"));
        Assert.Same(ConstraintValue.Nothing, mismatch);
    }

    [Fact]
    public void Unrestricted_IsIdentity_And_Empty_IsAbsorbing()
    {
        var set = AuthoritySet.Of(Email(["send"]));

        Assert.Same(set, AuthoritySet.Unrestricted.Intersect(set));
        Assert.Same(set, set.Intersect(AuthoritySet.Unrestricted));
        Assert.Empty(set.Intersect(AuthoritySet.Empty).Grants);
    }

    [Fact]
    public void Intersect_IsCommutative_AndIdempotent()
    {
        var a = AuthoritySet.Of(Email(["send", "read"],
            policies: new() { ["send"] = ActionPolicy.Ask },
            constraints: new() { ["recipient_domains"] = ConstraintValue.Of("@acme.com", "*.partners.acme.com"), ["daily_cap"] = ConstraintValue.Of(50m) }));
        var b = AuthoritySet.Of(Email(["send"],
            constraints: new() { ["daily_cap"] = ConstraintValue.Of(20m) }));

        Assert.Equal(AuthorityJson.Serialize(a.Intersect(b)), AuthorityJson.Serialize(b.Intersect(a)));
        Assert.Equal(AuthorityJson.Serialize(a), AuthorityJson.Serialize(a.Intersect(a)));
    }

    /// <summary>The never-widen property, checked exhaustively over a probe grid.</summary>
    [Fact]
    public void Intersect_NeverPermitsWhatAnOperandRefused()
    {
        var a = AuthoritySet.Of(Email(["send", "read"],
            policies: new() { ["read"] = ActionPolicy.Deny },
            constraints: new() { ["recipient_domains"] = ConstraintValue.Of("@acme.com") }));
        var b = AuthoritySet.Of(
            Email(["send"], constraints: new() { ["daily_cap"] = ConstraintValue.Of(10m) }),
            new AuthorityGrant { Type = "calendar", Actions = ["book"] });

        var result = a.Intersect(b);

        string[] types = ["email", "calendar", "crm"];
        string[] actions = ["send", "read", "book", "delete"];
        var contexts = new Dictionary<string, string>?[]
        {
            null,
            new() { ["recipient_domains"] = "x@acme.com" },
            new() { ["recipient_domains"] = "x@evil.example" },
            new() { ["daily_cap"] = "5" },
            new() { ["daily_cap"] = "500" },
        };

        foreach (var type in types)
        foreach (var action in actions)
        foreach (var context in contexts)
        {
            if (result.Permits(type, action, context))
            {
                Assert.True(a.Permits(type, action, context),
                    $"intersection permits {type}:{action} but operand A refused it");
                Assert.True(b.Permits(type, action, context),
                    $"intersection permits {type}:{action} but operand B refused it");
            }
        }
    }

    // ── evaluation semantics ────────────────────────────────────────────────────────────

    [Fact]
    public void Permits_UnlistedActionOrType_IsDenied()
    {
        var set = AuthoritySet.Of(Email(["send"]));

        Assert.True(set.Permits("email", "send"));
        Assert.False(set.Permits("email", "read"));
        Assert.False(set.Permits("calendar", "read"));
    }

    [Fact]
    public void Permits_AllowlistEntryShapes()
    {
        var set = AuthoritySet.Of(Email(["send"], constraints: new()
        {
            ["recipient_domains"] = ConstraintValue.Of("@acme.com", "*.partners.acme.com", "exact-host"),
        }));

        bool Send(string value) => set.Permits("email", "send",
            new Dictionary<string, string> { ["recipient_domains"] = value });

        Assert.True(Send("bob@acme.com"));         // @suffix
        Assert.True(Send("a.partners.acme.com"));  // *. wildcard
        Assert.False(Send("partners.acme.com"));   // wildcard requires a subdomain
        Assert.True(Send("exact-host"));           // exact
        Assert.False(Send("bob@other.example"));
    }

    [Fact]
    public void Permits_NumberCap_And_FlagGate()
    {
        var set = AuthoritySet.Of(Email(["send"], constraints: new()
        {
            ["max_amount"] = ConstraintValue.Of(50m),
            ["allow_external"] = ConstraintValue.Of(false),
        }));

        Assert.True(set.Permits("email", "send", new Dictionary<string, string> { ["max_amount"] = "49.5" }));
        Assert.False(set.Permits("email", "send", new Dictionary<string, string> { ["max_amount"] = "50.01" }));
        Assert.False(set.Permits("email", "send", new Dictionary<string, string> { ["allow_external"] = "true" }));
        Assert.True(set.Permits("email", "send", new Dictionary<string, string> { ["allow_external"] = "false" }));
    }

    // ── wire format ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Json_RoundTrips_IncludingPoliciesAndConstraints()
    {
        var original = AuthoritySet.Of(Email(["send", "read"],
            policies: new() { ["send"] = ActionPolicy.Ask },
            constraints: new()
            {
                ["recipient_domains"] = ConstraintValue.Of("@acme.com"),
                ["daily_cap"] = ConstraintValue.Of(50m),
                ["sandbox"] = ConstraintValue.Of(true),
            }));

        var json = AuthorityJson.Serialize(original);
        Assert.True(AuthorityJson.TryParse(json, out var parsed));
        Assert.Equal(json, AuthorityJson.Serialize(parsed));

        Assert.Equal(ActionPolicy.Ask, parsed.PolicyFor("email", "send"));
        Assert.Equal(ActionPolicy.Auto, parsed.PolicyFor("email", "read"));
    }

    [Fact]
    public void Json_UninterpretableMember_RoundTripsOpaque_AndFailsClosed()
    {
        const string json = """[{"type":"crm","actions":["read"],"filter":{"objects":["contacts"]}}]""";
        Assert.True(AuthorityJson.TryParse(json, out var parsed));

        // preserved verbatim on the wire
        Assert.Contains("\"filter\":{\"objects\":[\"contacts\"]}", AuthorityJson.Serialize(parsed));

        // context presented for an opaque constraint fails closed
        Assert.False(parsed.Permits("crm", "read", new Dictionary<string, string> { ["filter"] = "contacts" }));
        // absent context: the constraint is uncheckable here and skipped
        Assert.True(parsed.Permits("crm", "read"));
    }

    [Fact]
    public void Json_Garbage_FailsToParse_NeverWidens()
    {
        Assert.False(AuthorityJson.TryParse("not json", out _));
        Assert.False(AuthorityJson.TryParse("{}", out _));                    // not an array
        Assert.False(AuthorityJson.TryParse("""[{"actions":["a"]}]""", out _)); // no type
        Assert.False(AuthorityJson.TryParse("""[{"type":"a","action_policies":{"x":"maybe"}}]""", out _));
    }

    [Fact]
    public void Json_DuplicateTypes_AreRefused()
    {
        // Previously asserted that duplicates meet-merge into their intersection. RFC 9396 §2 says an
        // authorization_details array MAY carry several entries of the same type, and this model —
        // keyed by type — cannot represent that. §5 does not offer "silently reinterpret": an input
        // the AS cannot represent must be refused.
        //
        // Merging never granted more than was asked, so this was never an escalation. It was a silent
        // LOSS: a caller sending two independent grants of one type got back only what they had in
        // common, and found out at the resource server, on an action it was sure it had been granted.
        const string json = """[{"type":"email","actions":["send","read"]},{"type":"email","actions":["send"]}]""";
        Assert.False(AuthorityJson.TryParse(json, out _));
    }

    [Fact]
    public void Unrestricted_HasNoWireForm()
    {
        Assert.Throws<InvalidOperationException>(() => AuthorityJson.Serialize(AuthoritySet.Unrestricted));
    }
}

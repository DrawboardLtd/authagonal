using System.Security.Claims;
using Authagonal.Core.Authority;

namespace Authagonal.Tests;

/// <summary>
/// RFC 9396 authority was broken in BOTH directions: legitimately granted authority never evaluated, and
/// illegitimate authority could be manufactured through an intersection.
/// </summary>
public class AuthorityEvaluationTests
{
    private const string Claim = AuthorityClaims.AuthorizationDetails;

    /// <summary>
    /// A JWT-to-ClaimsPrincipal conversion flattens an array claim into one claim PER ELEMENT, so
    /// <c>FindFirst(...).Value</c> was a bare object rather than the array <c>AuthorityJson.TryParse</c>
    /// requires. Parsing therefore always failed, and a failed parse is deliberately a DENY — so
    /// <c>FromPrincipal</c> returned deny-all for every token that actually carried authority. The same root
    /// cause corrupted introspection, which rebuilds the claim from the split identity.
    /// </summary>
    [Fact]
    public void Split_array_claim_is_reassembled_not_denied()
    {
        // Exactly how the handler presents a two-element authorization_details array.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(Claim, """{"type":"email","actions":["read","send"]}"""),
            new Claim(Claim, """{"type":"calendar","actions":["read"]}"""),
        ]));

        var set = AuthorityEvaluator.FromPrincipal(principal);

        Assert.False(set.IsUnrestricted);
        Assert.Equal(2, set.Grants.Count);
        Assert.True(set.Permits("email", "read"));
        Assert.True(set.Permits("calendar", "read"));
        Assert.False(set.Permits("calendar", "write"));
    }

    /// <summary>The single-grant case is the common one, and it was equally broken.</summary>
    [Fact]
    public void Single_element_claim_is_reassembled()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(Claim, """{"type":"email","actions":["read"]}"""),
        ]));

        var set = AuthorityEvaluator.FromPrincipal(principal);

        Assert.False(set.IsUnrestricted);
        Assert.True(set.Permits("email", "read"));
        Assert.False(set.Permits("email", "send"));
    }

    /// <summary>An unflattened claim holding the whole array must still work.</summary>
    [Fact]
    public void Whole_array_in_one_claim_still_parses()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(Claim, """[{"type":"email","actions":["read"]}]"""),
        ]));

        Assert.True(AuthorityEvaluator.FromPrincipal(principal).Permits("email", "read"));
    }

    /// <summary>No claim at all is unrestricted; a garbled claim is a deny. Neither may change.</summary>
    [Fact]
    public void Absent_is_unrestricted_and_garbled_is_deny()
    {
        Assert.True(AuthorityEvaluator.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity())).IsUnrestricted);

        var garbled = new ClaimsPrincipal(new ClaimsIdentity([new Claim(Claim, "not json at all")]));
        var set = AuthorityEvaluator.FromPrincipal(garbled);
        Assert.False(set.IsUnrestricted);
        Assert.Empty(set.Grants);
        Assert.False(set.Permits("email", "read"));
    }

    /// <summary>A claim value that is neither an object nor an array must deny, never widen.</summary>
    [Fact]
    public void Non_object_claim_value_denies()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(Claim, "\"just-a-string\"")]));
        var set = AuthorityEvaluator.FromPrincipal(principal);
        Assert.False(set.IsUnrestricted);
        Assert.Empty(set.Grants);
    }

    /// <summary>
    /// Empty `locations` means "unrestricted" in this model, so intersecting two DISJOINT non-empty
    /// location sets produced an empty list — which read as unrestricted. Authority widened through an
    /// intersection, inverting the operation.
    /// </summary>
    [Fact]
    public void Disjoint_locations_do_not_widen_to_unrestricted()
    {
        var a = Parse("""[{"type":"payment","actions":["initiate"],"locations":["https://bank-a.example"]}]""");
        var b = Parse("""[{"type":"payment","actions":["initiate"],"locations":["https://bank-b.example"]}]""");

        var merged = a.Intersect(b);

        // Nothing is permitted in common, so the grant must be gone — not present with empty locations.
        Assert.False(merged.IsUnrestricted);
        Assert.DoesNotContain(merged.Grants, g => g.Type == "payment");
        Assert.False(merged.Permits("payment", "initiate"));
    }

    /// <summary>Overlapping locations still intersect to the overlap.</summary>
    [Fact]
    public void Overlapping_locations_intersect_to_the_overlap()
    {
        var a = Parse("""[{"type":"payment","actions":["initiate"],"locations":["https://x.example","https://y.example"]}]""");
        var b = Parse("""[{"type":"payment","actions":["initiate"],"locations":["https://y.example"]}]""");

        var merged = a.Intersect(b);
        var grant = Assert.Single(merged.Grants);
        Assert.Equal(["https://y.example"], grant.Locations);
    }

    /// <summary>An UNSPECIFIED side still carries the other's locations over — that convention is intended.</summary>
    [Fact]
    public void Unspecified_locations_carry_the_other_side_over()
    {
        var a = Parse("""[{"type":"payment","actions":["initiate"]}]""");
        var b = Parse("""[{"type":"payment","actions":["initiate"],"locations":["https://y.example"]}]""");

        var grant = Assert.Single(a.Intersect(b).Grants);
        Assert.Equal(["https://y.example"], grant.Locations);
    }

    private static AuthoritySet Parse(string json)
    {
        Assert.True(AuthorityJson.TryParse(json, out var set), json);
        return set;
    }
}

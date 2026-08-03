using System.Security.Claims;
using System.Text.Json;
using Authagonal.Core.Authority;

namespace Authagonal.Tests;

/// <summary>
/// The narrowest possible grant minted the broadest possible token: an effective authority that serialized to
/// `[]` evaluated as UNRESTRICTED at every resource server.
/// </summary>
/// <remarks>
/// Five links, each defensible alone, and the composition inverted the model.
/// <list type="number">
/// <item><c>ConstraintValue.Meet</c> collapses disjoint string sets to an empty <c>StringSet</c> and ANY kind
/// mismatch to <c>Nothing</c> — so <c>"recipient_domains": 5</c> against a ceiling listing domains was
/// enough.</item>
/// <item><c>MergeSameType</c> stored that met value and left <c>Actions</c> populated, so <c>Intersect</c>
/// kept the grant. It already dropped a grant with disjoint LOCATIONS; the constraint case was missed.</item>
/// <item>Every mint-path guard counted <c>Grants</c>, which still held that grant, and
/// <c>PolicyFor</c> reported its actions grantable because it does not consult constraints at all.</item>
/// <item><c>AuthorityJson.ToNode</c> then DROPPED the grant — correctly: emitting a positive grant carrying a
/// non-standard denial marker reads as permitted to any spec-conforming resource server. The last grant
/// dropped leaves <c>[]</c>, and <c>!string.IsNullOrEmpty("[]")</c> is true, so the claim was written.</item>
/// <item>A JWT-to-ClaimsPrincipal conversion flattens an array claim into one claim per element, so an empty
/// array yields ZERO claims — and <c>AuthorityEvaluator.FromPrincipal</c> reads zero claims as
/// <c>Unrestricted</c>, deliberately, so that coarse scope-based tokens keep working.</item>
/// </list>
/// <para>
/// Link 5 is not a bug and must not change: a token with no authority claim genuinely is a coarse token. That
/// is precisely why <c>[]</c> can never be minted — and why omitting the claim instead is no safer. The only
/// correct answer is to refuse.
/// </para>
/// <para>
/// The sharpest path needs no attacker input at all. In the unattended <c>client_credentials</c> mode ask
/// degrades to deny, so an agent whose ceiling is entirely approval-gated — the most careful configuration an
/// admin can write — had every grant dropped and received an unrestricted token from its own valid
/// credentials. That path had no emptiness guard of any kind.
/// </para>
/// </remarks>
public sealed class EmptyAuthorityWireFormTests
{
    private static AuthorityGrant Email(
        string[] actions,
        Dictionary<string, ConstraintValue>? constraints = null,
        Dictionary<string, ActionPolicy>? policies = null) => new()
    {
        Type = "email",
        Actions = actions,
        Constraints = constraints ?? new Dictionary<string, ConstraintValue>(),
        ActionPolicies = policies ?? new Dictionary<string, ActionPolicy>(),
    };

    // ── link 5: the read that makes the rest critical ────────────────────────────────────────────────

    /// <summary>
    /// The premise. An empty array flattens to zero claims, and zero claims is unrestricted.
    /// </summary>
    /// <remarks>
    /// Asserted rather than assumed, because every other test here is only alarming if this holds. It also
    /// documents why the fix is "never mint it" rather than "read it as empty": once flattened, an empty array
    /// and an absent claim are the same principal, so the distinction is unrecoverable at the read.
    /// </remarks>
    [Fact]
    public void ZeroClaims_ReadsAsUnrestricted_WhichIsWhyTheEmptyArrayIsFatal()
    {
        var flattened = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.True(AuthorityEvaluator.FromPrincipal(flattened).IsUnrestricted);
        Assert.True(AuthorityEvaluator.Permits(flattened, "email", "send"));
        Assert.True(AuthorityEvaluator.Permits(flattened, "payments", "transfer"));
    }

    // ── link 2: the algebra keeps its never-widen invariant ──────────────────────────────────────────

    [Fact]
    public void Intersect_DropsAGrantWhoseConstraintMetToAnEmptySet()
    {
        var ceiling = AuthoritySet.Of(Email(["send"],
            new() { ["recipient_domains"] = ConstraintValue.Of("@acme.com") }));
        var request = AuthoritySet.Of(Email(["send"],
            new() { ["recipient_domains"] = ConstraintValue.Of("@attacker.example") }));

        var effective = ceiling.Intersect(request);

        Assert.Empty(effective.Grants);
        Assert.False(effective.IsUnrestricted);
    }

    /// <summary>
    /// A kind mismatch is the cheapest trigger: one request, no guessing at the ceiling's values.
    /// </summary>
    [Fact]
    public void Intersect_DropsAGrantWhoseConstraintMetToNothing()
    {
        var ceiling = AuthoritySet.Of(Email(["send"],
            new() { ["recipient_domains"] = ConstraintValue.Of("@acme.com") }));
        var request = AuthoritySet.Of(Email(["send"],
            new() { ["recipient_domains"] = ConstraintValue.Of(5m) }));

        Assert.Empty(ceiling.Intersect(request).Grants);
    }

    /// <summary>
    /// The control: a constraint that meets to a NON-empty set still narrows and survives.
    /// </summary>
    /// <remarks>
    /// Without this, "drop the grant whenever a constraint was met" would satisfy every assertion above and
    /// would break constrained delegation entirely — which is the feature, not the defect.
    /// </remarks>
    [Fact]
    public void Intersect_KeepsAGrantWhoseConstraintNarrowsToSomething()
    {
        var ceiling = AuthoritySet.Of(Email(["send"],
            new() { ["recipient_domains"] = ConstraintValue.Of("@acme.com", "@acme.co.uk") }));
        var request = AuthoritySet.Of(Email(["send"],
            new() { ["recipient_domains"] = ConstraintValue.Of("@acme.com") }));

        var effective = ceiling.Intersect(request);

        var grant = Assert.Single(effective.Grants);
        var domains = Assert.IsType<ConstraintValue.StringSet>(grant.Constraints["recipient_domains"]);
        Assert.Equal(["@acme.com"], domains.Values);
        Assert.False(AuthorityJson.SerializesToNothing(effective));
    }

    // ── link 3/4: the emptiness question is asked of the wire form ───────────────────────────────────

    /// <summary>
    /// The two sources that never touch a meet, so the algebra fix alone does not cover them.
    /// </summary>
    /// <remarks>
    /// A set assembled directly — from a stored ceiling, or from <c>MapAskPolicies(…, Deny)</c> in the
    /// unattended path — can hold a grant that <c>ToNode</c> drops without any intersection having occurred.
    /// </remarks>
    [Theory]
    [InlineData("all actions denied")]
    [InlineData("constraint is Nothing")]
    [InlineData("constraint is an empty set")]
    public void SerializesToNothing_SeesWhatGrantsCountCannot(string shape)
    {
        var set = AuthoritySet.Of(shape switch
        {
            "all actions denied" => Email(["send"], policies: new() { ["send"] = ActionPolicy.Deny }),
            "constraint is Nothing" => Email(["send"],
                new() { ["recipient_domains"] = ConstraintValue.Nothing }),
            _ => Email(["send"], new() { ["recipient_domains"] = ConstraintValue.Of() }),
        });

        // The structural set is non-empty, which is what every emptiness guard on the mint path tested.
        Assert.Single(set.Grants);

        // For the two constraint shapes the set also reports the action as GRANTABLE, because PolicyFor does
        // not consult constraints at all — so the explicit-denial check could not see them either. The
        // all-denied shape is the one PolicyFor does catch, and it still reached the mint: the guard that
        // would have fired tests the REQUESTED authority, and the unattended client_credentials path, where
        // MapAskPolicies manufactures exactly this shape, had no guard at all.
        if (shape != "all actions denied")
            Assert.NotEqual(ActionPolicy.Deny, set.PolicyFor("email", "send"));

        // The wire form grants nothing.
        Assert.True(AuthorityJson.SerializesToNothing(set));
        Assert.Equal("[]", AuthorityJson.Serialize(set));
    }

    [Fact]
    public void SerializesToNothing_IsFalseForAnOrdinarySet()
    {
        Assert.False(AuthorityJson.SerializesToNothing(AuthoritySet.Of(Email(["send"]))));
        Assert.False(AuthorityJson.SerializesToNothing(AuthoritySet.Empty));
        Assert.False(AuthorityJson.SerializesToNothing(AuthoritySet.Unrestricted));
    }

    /// <summary>
    /// The round trip that closes the loop: what the mint would have signed, read back the way a resource
    /// server reads it.
    /// </summary>
    /// <remarks>
    /// This is the assertion that states the vulnerability as one fact — an authority that permits nothing,
    /// serialized and re-read through the flattening a JWT actually performs, permitted everything.
    /// </remarks>
    [Fact]
    public void TheEmptyWireForm_ReadBackAsAJwtWould_PermittedEverything()
    {
        var set = AuthoritySet.Of(Email(["send"],
            new() { ["recipient_domains"] = ConstraintValue.Of() }));

        var wire = AuthorityJson.Serialize(set);
        Assert.Equal("[]", wire);

        // One claim per array element is what the JWT handler does, so an empty array contributes none.
        var identity = new ClaimsIdentity();
        foreach (var element in JsonDocument.Parse(wire).RootElement.EnumerateArray())
            identity.AddClaim(new Claim(AuthorityClaims.AuthorizationDetails, element.GetRawText()));

        Assert.Empty(identity.Claims);
        Assert.True(AuthorityEvaluator.Permits(new ClaimsPrincipal(identity), "payments", "transfer"));
    }
}

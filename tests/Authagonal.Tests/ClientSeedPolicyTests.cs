using Authagonal.Core.Services;

namespace Authagonal.Tests;

/// <summary>
/// Structural piece 2, second half — the two seeders decide what configuration may seed by ONE rule.
/// </summary>
/// <remarks>
/// There are two seeder classes and there have to be: <c>ProtocolSeedService</c> ships in
/// <c>Authagonal.Protocol</c>, which is embedded without <c>Authagonal.Server</c>, and
/// <c>ClientSeedService</c> is the Server host's. Merging them would point the protocol package at the server
/// package. So the policy is what got merged, and this pins the rule itself — a table of what configuration
/// may and may not put on a client, in one place both seeders read.
/// <para>
/// The drift this closes was live: the audience validation and the declared-audiences flag existed in neither
/// seeder, then in one, and the Server host's had no audiences field at all — so configuration was the one
/// write path that could still put an unbounded or non-absolute value into a signed token's <c>aud</c>.
/// </para>
/// </remarks>
public sealed class ClientSeedPolicyTests
{
    private const string AdminScope = "authagonal-admin";

    [Fact]
    public void AnOrdinaryDescriptorIsAccepted()
        => Assert.Null(ClientSeedPolicy.Reject(
            ["openid", "profile"], ["https://api.example.com"], AdminScope));

    /// <summary>
    /// No client may hold the administrative scope, however it is being created.
    /// </summary>
    /// <remarks>
    /// The admin API and dynamic registration both refuse it. A <c>client_credentials</c> client that held it
    /// could mint admin tokens indefinitely, so configuration must not be the way in.
    /// </remarks>
    [Fact]
    public void TheAdministrativeScopeIsRefused()
    {
        var why = ClientSeedPolicy.Reject(["openid", AdminScope], null, AdminScope);

        Assert.NotNull(why);
        Assert.Contains("administrative scope", why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scope entry containing whitespace is refused rather than normalised.
    /// </summary>
    /// <remarks>
    /// It expands into several scopes in the space-delimited <c>scope</c> claim downstream, which is how the
    /// reservation above came to be bypassed. Refused because the intent is ambiguous.
    /// </remarks>
    [Fact]
    public void AScopeEntryThatIsNotOneTokenIsRefused()
    {
        // Deliberately NOT containing the admin scope: that rule splits on whitespace too and would fire
        // first, so an input carrying both proves only the earlier check.
        var why = ClientSeedPolicy.Reject(["openid profile"], null, AdminScope);

        Assert.NotNull(why);
        Assert.Contains("single scope token", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/admin")]                       // a bare path: absolute only by Uri.TryCreate's inference
    [InlineData("not a uri at all")]
    [InlineData("https://api.example.com/#frag")] // a fragment has no meaning in an aud
    public void AnAudienceThatIsNotAnAbsoluteUriIsRefused(string audience)
    {
        var why = ClientSeedPolicy.Reject(["openid"], [audience], AdminScope);

        Assert.NotNull(why);
        Assert.Contains("aud", why, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreAudiencesThanTheCapIsRefused()
    {
        var tooMany = Enumerable.Range(0, ResourceAudiencePolicy.MaxAudiences + 1)
            .Select(i => $"https://api{i}.example.com")
            .ToArray();

        Assert.NotNull(ClientSeedPolicy.Reject(["openid"], tooMany, AdminScope));
    }

    /// <summary>
    /// Naming an audience declares them; naming none does not.
    /// </summary>
    /// <remarks>
    /// The distinction is load-bearing. <c>ResourceAudiencePolicy</c> reads a client that has declared and
    /// listed nothing as one that may name NO resource, so treating an absent list as a declaration would
    /// silently break every seeded client on upgrade — while treating a present list as no declaration leaves
    /// it on the permissive legacy branch forever, which is the defect this closed.
    /// </remarks>
    [Fact]
    public void DeclaringIsNamingAtLeastOne()
    {
        Assert.True(ClientSeedPolicy.Declares(["https://api.example.com"]));
        Assert.False(ClientSeedPolicy.Declares([]));
        Assert.False(ClientSeedPolicy.Declares(null));
    }

    /// <summary>
    /// An absent audience list is not a validation failure — there is nothing to validate.
    /// </summary>
    /// <remarks>
    /// The control that keeps the audience rules from becoming "every seeded client must name an audience",
    /// which would refuse most real configurations.
    /// </remarks>
    [Fact]
    public void NoAudiencesIsFine()
    {
        Assert.Null(ClientSeedPolicy.Reject(["openid"], null, AdminScope));
        Assert.Null(ClientSeedPolicy.Reject(["openid"], [], AdminScope));
    }
}

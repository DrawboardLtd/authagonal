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
/// <para>
/// The table also records what configuration MAY do, which is the half that was wrong: the administrative
/// scope is reserved against callers, not against the trust root, and refusing it in the seeders left every
/// fresh deployment unable to mint the admin token its own docs describe.
/// </para>
/// </remarks>
public sealed class ClientSeedPolicyTests
{
    private const string AdminScope = "authagonal-admin";

    [Fact]
    public void AnOrdinaryDescriptorIsAccepted()
        => Assert.Null(ClientSeedPolicy.Reject(
            ["openid", "profile"], ["https://api.example.com"]));

    /// <summary>
    /// Configuration MAY seed the administrative scope. It is the documented bootstrap, and refusing it
    /// locked deployments out of their own admin API.
    /// </summary>
    /// <remarks>
    /// The reservation stands on every path a caller can reach — the admin API, dynamic registration and
    /// <c>POST /api/v1/token</c> all answer <c>403 forbidden_scope</c> — but configuration is the trust root,
    /// not a caller. <c>docs/admin-api.md</c> names a config-seeded <c>client_credentials</c> client carrying
    /// this scope as the only way to mint the first admin token, so a seeder that refuses it means a fresh
    /// install can never reach <c>/api/v1/*</c>.
    /// <para>
    /// The second consequence was worse than the first. The seeders log at Error and SKIP the descriptor, so
    /// an operator rotating a compromised admin secret — "a config change + restart", per that same doc —
    /// wrote no new hash, and the credential they believed they had revoked kept authenticating. Every signal
    /// they could check said the rotation worked, including the admin API answering, because it was still
    /// answering to the old secret.
    /// </para>
    /// <para>
    /// This test is written as the assertion that would have failed BEFORE the fix, because a rule whose
    /// removal nothing pins is a rule that comes back.
    /// </para>
    /// </remarks>
    [Fact]
    public void ConfigurationMaySeedTheAdministrativeScope()
    {
        Assert.Null(ClientSeedPolicy.Reject(["openid", AdminScope], null));

        // The documented appsettings example, verbatim.
        Assert.Null(ClientSeedPolicy.Reject([AdminScope], null));
    }

    /// <summary>
    /// A scope entry containing whitespace is refused rather than normalised.
    /// </summary>
    /// <remarks>
    /// It expands into several scopes in the space-delimited <c>scope</c> claim downstream, so a stored client
    /// does not say what it appears to say. That is also how the administrative-scope reservation came to be
    /// bypassed on the runtime paths, which still enforce it. Refused because the intent is ambiguous.
    /// </remarks>
    [Fact]
    public void AScopeEntryThatIsNotOneTokenIsRefused()
    {
        var why = ClientSeedPolicy.Reject(["openid profile"], null);

        Assert.NotNull(why);
        Assert.Contains("single scope token", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/admin")]                       // a bare path: absolute only by Uri.TryCreate's inference
    [InlineData("not a uri at all")]
    [InlineData("https://api.example.com/#frag")] // a fragment has no meaning in an aud
    public void AnAudienceThatIsNotAnAbsoluteUriIsRefused(string audience)
    {
        var why = ClientSeedPolicy.Reject(["openid"], [audience]);

        Assert.NotNull(why);
        Assert.Contains("aud", why, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreAudiencesThanTheCapIsRefused()
    {
        var tooMany = Enumerable.Range(0, ResourceAudiencePolicy.MaxAudiences + 1)
            .Select(i => $"https://api{i}.example.com")
            .ToArray();

        Assert.NotNull(ClientSeedPolicy.Reject(["openid"], tooMany));
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
        Assert.Null(ClientSeedPolicy.Reject(["openid"], null));
        Assert.Null(ClientSeedPolicy.Reject(["openid"], []));
    }
}

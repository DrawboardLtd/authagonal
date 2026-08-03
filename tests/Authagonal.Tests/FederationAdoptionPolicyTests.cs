using Authagonal.Core.Models;
using Authagonal.Core.Services;

namespace Authagonal.Tests;

/// <summary>
/// Federated account squatting: the takeover survived two passes of fixes because every gate answered the
/// wrong question.
/// </summary>
/// <remarks>
/// Three gates existed, and all three pass during the attack:
/// <list type="bullet">
/// <item><c>!emailVerified</c> refuses an unvouched address — but <c>email_verified</c> is chosen by whoever
/// operates the upstream OP, and in this threat model that is the attacker.</item>
/// <item><c>AllowedDomains</c> is enforced only when non-empty, so the attacker leaves it empty.</item>
/// <item>The <c>ISsoDomainStore</c> routing check refuses a domain another connection owns — but the squat
/// happens BEFORE the victim onboards, so the domain has no row and there is nothing to contradict.</item>
/// </list>
/// The account is minted bearing <c>ceo@acme.com</c> with <c>EmailConfirmed = true</c>. When Acme onboards,
/// their connection genuinely IS the authority for the domain, so on the real user's first login every gate
/// agrees and the account is adopted — carrying the squatter's login binding with it.
/// <para>
/// The unasked question was never "does this connection own the domain" but "who else can already sign in to
/// this account". These tests pin the answer, and the boundary either side of it: a social login is not a
/// connection binding and must survive, and an unvouched connection does not get to evict anyone.
/// </para>
/// </remarks>
public sealed class FederationAdoptionPolicyTests
{
    private const string AcmeProvider = "oidc:acme-conn";
    private const string SquatterProvider = "oidc:attacker-conn";

    private static ExternalLoginInfo Login(string provider, string key = "sub-1") => new()
    {
        UserId = "user-1",
        Provider = provider,
        ProviderKey = key,
    };

    // ── which bindings count as foreign ──────────────────────────────────────────────────────────────

    [Fact]
    public void AnotherConnectionsBinding_IsForeign()
    {
        var foreign = FederationAdoptionPolicy.ForeignBindings(
            [Login(SquatterProvider)], AcmeProvider);

        var only = Assert.Single(foreign);
        Assert.Equal(SquatterProvider, only.Provider);
    }

    [Fact]
    public void ThisConnectionsOwnBinding_IsNotForeign()
        => Assert.Empty(FederationAdoptionPolicy.ForeignBindings([Login(AcmeProvider)], AcmeProvider));

    /// <summary>
    /// A SAML connection is a connection: the same attack arrives over either protocol.
    /// </summary>
    [Fact]
    public void ASamlConnectionsBinding_IsForeignToAnOidcConnection()
        => Assert.Single(FederationAdoptionPolicy.ForeignBindings(
            [Login("saml:attacker-conn")], AcmeProvider));

    /// <summary>
    /// Social logins are never evicted, and this is the assertion that keeps the fix from becoming a lockout.
    /// </summary>
    /// <remarks>
    /// A user who signed up with Google and whose employer later onboards SSO must keep that login. It is also
    /// not a squatting primitive: nobody can make Google assert an address they do not control, which is
    /// exactly what distinguishes it from a connection an attacker configured themselves.
    /// </remarks>
    [Theory]
    [InlineData("google")]
    [InlineData("github")]
    [InlineData("microsoft")]
    public void ASocialLogin_IsNotAConnectionBinding(string provider)
        => Assert.Empty(FederationAdoptionPolicy.ForeignBindings([Login(provider)], AcmeProvider));

    // ── the decision ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoForeignBinding_AdoptsAsBefore()
    {
        Assert.Equal(
            FederationAdoptionPolicy.Decision.Adopt,
            FederationAdoptionPolicy.Evaluate(connectionIsDomainAuthority: true, foreignBindingCount: 0));

        // Unchanged for the unvouched case too — that path has its own earlier gate.
        Assert.Equal(
            FederationAdoptionPolicy.Decision.Adopt,
            FederationAdoptionPolicy.Evaluate(connectionIsDomainAuthority: false, foreignBindingCount: 0));
    }

    /// <summary>
    /// The domain's established authority evicts the squatter rather than inheriting it.
    /// </summary>
    /// <remarks>
    /// Eviction rather than refusal, deliberately: refusing would have handed the attacker a permanent denial
    /// of service over any address they squatted first, and between a squatter and the admin-vouched IdP for
    /// the domain the IdP is the one entitled to the address.
    /// </remarks>
    [Fact]
    public void TheDomainsAuthority_EvictsTheForeignBinding()
        => Assert.Equal(
            FederationAdoptionPolicy.Decision.EvictForeignBindingsThenAdopt,
            FederationAdoptionPolicy.Evaluate(connectionIsDomainAuthority: true, foreignBindingCount: 1));

    /// <summary>
    /// A connection that is not the domain's authority does not get to evict anyone.
    /// </summary>
    /// <remarks>
    /// Otherwise the fix would be the vulnerability inverted: any connection could strip a rival's binding by
    /// asserting an address in its domain. Refusal is correct here — the account exists, this connection has
    /// no claim to it, and an administrator has to decide.
    /// </remarks>
    [Fact]
    public void AnUnvouchedConnection_IsRefused_NotAllowedToEvict()
        => Assert.Equal(
            FederationAdoptionPolicy.Decision.Refuse,
            FederationAdoptionPolicy.Evaluate(connectionIsDomainAuthority: false, foreignBindingCount: 1));

    // ── the attack, as a sequence ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole squat, stated as the states it passes through.
    /// </summary>
    /// <remarks>
    /// Written as one test because each step in isolation looks legitimate — that is why the composition
    /// survived. The point of failure is the last line: before the fix this was <c>Adopt</c>, and the
    /// squatter's binding stayed on the account the genuine CEO now used.
    /// </remarks>
    [Fact]
    public void TheSquat_EndsInEviction_NotInheritance()
    {
        // T0 — attacker's connection mints ceo@acme.com. Their own binding is the only one on the account.
        var accountLogins = new List<ExternalLoginInfo> { Login(SquatterProvider, "attacker-1") };

        // T1 — Acme onboards; their connection is the authority for acme.com.
        const bool acmeOwnsTheDomain = true;

        // T2 — the genuine CEO's first login finds the account by email.
        var foreign = FederationAdoptionPolicy.ForeignBindings(accountLogins, AcmeProvider);
        Assert.Single(foreign);

        var decision = FederationAdoptionPolicy.Evaluate(acmeOwnsTheDomain, foreign.Count);

        Assert.Equal(FederationAdoptionPolicy.Decision.EvictForeignBindingsThenAdopt, decision);
        Assert.NotEqual(FederationAdoptionPolicy.Decision.Adopt, decision);
    }
}

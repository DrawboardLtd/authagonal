using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Tests;

/// <summary>
/// Two ways an OAuth client or user reached the administrative scope, both caused by a guard normalizing
/// scope names differently from the code that consumes them.
/// </summary>
public class ScopeEscalationTests
{
    private sealed class FakeScopeStore(params Scope[] scopes) : IScopeStore
    {
        public Task<Scope?> GetAsync(string name, CancellationToken ct = default)
            // Deliberately case-SENSITIVE, mirroring the real stores: the scope name is a storage key
            // (Table RowKey / SQL sk), so a case variant is a miss, not a match.
            => Task.FromResult(scopes.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal)));
        public Task<IReadOnlyList<Scope>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Scope>>(scopes);
        public Task CreateAsync(Scope scope, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(Scope scope, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// The per-user entitlement gate resolved the scope by exact name. A case variant read as an
    /// unregistered scope, and unregistered scopes are deliberately left alone — so varying the case
    /// skipped the gate entirely, while the IdentityAdmin policy that consumes the minted claim compares
    /// case-insensitively and honoured it.
    /// </summary>
    [Theory]
    [InlineData("authagonal-admin")]
    [InlineData("Authagonal-Admin")]
    [InlineData("AUTHAGONAL-ADMIN")]
    public async Task Role_gated_scope_is_withheld_regardless_of_case(string requested)
    {
        var gate = new ScopeRoleGate(new FakeScopeStore(new Scope
        {
            Name = "authagonal-admin",
            DisplayName = "Admin",
            AllowedRoles = ["owner"],
        }));

        // A user with no qualifying role must not keep the scope, however they spell it.
        var kept = await gate.FilterAsync([requested], userRoles: ["developer"]);
        Assert.Empty(kept);
    }

    [Fact]
    public async Task Role_gated_scope_is_granted_to_a_qualifying_user()
    {
        var gate = new ScopeRoleGate(new FakeScopeStore(new Scope
        {
            Name = "authagonal-admin",
            DisplayName = "Admin",
            AllowedRoles = ["owner"],
        }));

        Assert.Equal(["authagonal-admin"], await gate.FilterAsync(["authagonal-admin"], ["owner"]));
    }

    [Fact]
    public async Task Ungated_and_unregistered_scopes_still_pass_through()
    {
        var gate = new ScopeRoleGate(new FakeScopeStore(
            new Scope { Name = "openid", DisplayName = "OpenID" }));

        // Ungated (no AllowedRoles) and genuinely unknown names are both left alone — dropping unknown
        // names here would mask a configuration mistake as a permission problem.
        var kept = await gate.FilterAsync(["openid", "not-registered"], userRoles: null);
        Assert.Equal(["openid", "not-registered"], kept);
    }

    /// <summary>
    /// AllowedScopes is joined into a space-delimited scope string on the wire, so an entry containing a
    /// space is one opaque string to a whole-string comparison but two scopes to every consumer that
    /// splits. That made an embedded space a permanent admin backdoor client.
    /// </summary>
    [Theory]
    [InlineData("authagonal-admin")]
    [InlineData("Authagonal-Admin")]
    [InlineData("authagonal-admin x")]
    [InlineData("x authagonal-admin")]
    [InlineData("x authagonal-admin y")]
    [InlineData("x\tauthagonal-admin")]
    public void Admin_scope_reservation_catches_every_spelling(string entry)
    {
        Assert.True(AdminScopeReservation.Grants([entry], "authagonal-admin"));
    }

    [Theory]
    [InlineData("openid")]
    [InlineData("authagonal-administrator")]
    [InlineData("not-authagonal-admin")]
    public void Admin_scope_reservation_does_not_over_match(string entry)
    {
        Assert.False(AdminScopeReservation.Grants([entry], "authagonal-admin"));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("tab\there")]
    public void Malformed_scope_names_are_detected(string entry)
    {
        Assert.Equal(entry, AdminScopeReservation.FindMalformedScope([entry]));
    }

    [Fact]
    public void Well_formed_scope_names_are_accepted()
    {
        Assert.Null(AdminScopeReservation.FindMalformedScope(["openid", "profile", "api:read"]));
    }
}

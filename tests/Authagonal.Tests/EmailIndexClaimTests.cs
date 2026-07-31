using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// F247 — the email→userId row is the authority for <c>FindByEmailAsync</c>, which is how password
/// login, password reset, SCIM matching and federated account linking all resolve an identity. Every
/// write of it was an unconditional Replace.
/// </summary>
/// <remarks>
/// Uniqueness rested entirely on callers doing a FindByEmailAsync first — a check-then-act with
/// several round trips of gap — so two concurrent registrations for one address could both pass their
/// check and the second silently take ownership of the first's login identifier. The store already
/// used the atomic primitive next door: CreateAsync writes the profile row with AddEntityAsync.
/// Verified against real Azure Table semantics (Azurite), because the guarantee IS the 409.
/// </remarks>
[Collection("Azurite")]
public class EmailIndexClaimTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableUserStore NewStore(string prefix)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        return new TableUserStore(T("Users"), T("Emails"), T("Logins"), T("ExtIds"), null, null, EnvPartitioner.Live);
    }

    private static AuthUser User(string id, string email) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ASecondUserCannotTakeOverAnExistingAddress()
    {
        var store = NewStore($"c{Guid.NewGuid():N}");
        await store.CreateAsync(User("u1", "shared@example.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateAsync(User("u2", "shared@example.com")));

        // The binding still names the first user — and the loser left nothing behind, because a
        // failed index claim rolls its profile row back.
        Assert.Equal("u1", (await store.FindByEmailAsync("shared@example.com"))?.Id);
        Assert.Null(await store.GetAsync("u2"));
    }

    [Fact]
    public async Task ConcurrentRegistrationsForOneAddress_LeaveExactlyOneOwner()
    {
        var store = NewStore($"c{Guid.NewGuid():N}");

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            try
            {
                await store.CreateAsync(User($"u{i}", "contended@example.com"));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        })));

        Assert.Equal(1, results.Count(won => won));

        var owner = await store.FindByEmailAsync("contended@example.com");
        Assert.NotNull(owner);
        Assert.NotNull(await store.GetAsync(owner!.Id));
    }

    [Fact]
    public async Task RewritingTheSameUsersOwnBindingIsIdempotent()
    {
        // Re-registering an address the same user already holds is ordinary — a reindex, a retried
        // write, a profile update that does not touch the email — and must not start failing.
        var store = NewStore($"c{Guid.NewGuid():N}");
        var user = User("u1", "same@example.com");
        await store.CreateAsync(user);

        user.FirstName = "Renamed";
        await store.UpdateAsync(user);

        Assert.Equal("u1", (await store.FindByEmailAsync("same@example.com"))?.Id);
    }

    [Fact]
    public async Task AnEmailChangeIntoAnOccupiedAddress_LeavesBothBindingsIntact()
    {
        var store = NewStore($"c{Guid.NewGuid():N}");
        await store.CreateAsync(User("u1", "first@example.com"));
        await store.CreateAsync(User("u2", "second@example.com"));

        var mover = (await store.GetAsync("u2"))!;
        mover.Email = "first@example.com";
        mover.NormalizedEmail = "FIRST@EXAMPLE.COM";

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(mover));

        // The claim fails before the old binding is dropped, so u2 is not stranded without a login
        // identifier and u1 keeps the one it owns.
        Assert.Equal("u1", (await store.FindByEmailAsync("first@example.com"))?.Id);
        Assert.Equal("u2", (await store.FindByEmailAsync("second@example.com"))?.Id);
    }
}

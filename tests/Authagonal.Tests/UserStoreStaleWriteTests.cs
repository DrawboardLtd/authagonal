using Amazon.DynamoDBv2;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.AwsProvider.Stores;
using Authagonal.AzureProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.SqlProvider.Sql;
using Authagonal.SqlProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// F115/F226 — a user document is written whole, so an UpdateAsync built from a stale read puts every
/// column back as it stood at that read. The store must refuse it.
/// </summary>
/// <remarks>
/// The attack the finding names: support resets a compromised account's password, which also rotates
/// the security stamp and kills every session. Anything else holding an older snapshot of that user — a
/// role change, a SCIM PATCH, an MFA reset, or a login-triggered federated profile sync an attacker can
/// fire at will — writes it back afterwards and restores the OLD PasswordHash and the OLD SecurityStamp.
/// The reset returns 204 and is silently undone.
///
/// The load-bearing detail is WHOSE revision is matched. Conditioning on one the store re-reads for
/// itself just before writing closes nothing, because the dangerous window is the caller's: read in an
/// endpoint, written several round-trips later. So these tests hold a snapshot across an intervening
/// write, exactly as an endpoint does.
/// </remarks>
[Collection("Azurite")]
public class AzureUserStoreStaleWriteTests(AzuriteFixture azurite)
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
        PasswordHash = "old-hash",
        SecurityStamp = "old-stamp",
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task AStaleWriterCannotRevertAPasswordReset()
    {
        var store = NewStore($"s{Guid.NewGuid():N}");
        await store.CreateAsync(User("u1", "victim@example.com"));

        // The slow writer reads first — a role change, a SCIM PATCH, a federated profile sync.
        var stale = await store.GetAsync("u1");
        Assert.NotNull(stale);

        // Support resets the password and rotates the stamp in between.
        var reset = await store.GetAsync("u1");
        reset!.PasswordHash = "new-hash";
        reset.SecurityStamp = "new-stamp";
        await store.UpdateAsync(reset);

        // The slow writer lands last. It must not win.
        stale.FirstName = "Renamed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(stale));

        var stored = await store.GetAsync("u1");
        Assert.Equal("new-hash", stored!.PasswordHash);
        Assert.Equal("new-stamp", stored.SecurityStamp);
    }

    [Fact]
    public async Task AConcurrentLoginDoesNotBlockAnAdministrativeWrite()
    {
        // The guard has to distinguish "someone changed something that decides an outcome" from "this
        // account signed in". If it matched the raw row revision instead, every sign-in would move it and
        // an admin write would fail whenever the account was active — and since the attacker in the
        // reported scenario controls the login rate, that would turn a silent lost update into a denial
        // of the remediation. It would also break what LoginStampConcurrencyTests already pins.
        var store = NewStore($"s{Guid.NewGuid():N}");
        await store.CreateAsync(User("u3", "busy@example.com"));

        var admin = await store.GetAsync("u3");
        Assert.NotNull(admin);

        await store.RecordSuccessfulLoginAsync("u3");
        await store.RecordSuccessfulLoginAsync("u3");

        admin!.IsActive = false;
        await store.UpdateAsync(admin);

        Assert.False((await store.GetAsync("u3"))!.IsActive);
    }

    [Fact]
    public async Task ASuccessfulWriteRefreshesTheCallersRevision()
    {
        // Read-modify-write chains within one request update the same instance twice (registration's
        // claim path does exactly this), so a successful write has to hand back the new revision or the
        // guard would break an ordinary flow rather than an attack.
        var store = NewStore($"s{Guid.NewGuid():N}");
        await store.CreateAsync(User("u2", "chain@example.com"));

        var user = await store.GetAsync("u2");
        user!.FirstName = "First";
        await store.UpdateAsync(user);
        user.LastName = "Second";
        await store.UpdateAsync(user);

        var stored = await store.GetAsync("u2");
        Assert.Equal("First", stored!.FirstName);
        Assert.Equal("Second", stored.LastName);
    }

    [Fact]
    public async Task AnExternalIdRowIsOnlyRemovableByTheUserItNames()
    {
        // F113: RemoveExternalIdAsync deleted by key alone, so once an externalId had been repointed the
        // ORIGINAL holder's next change deleted the row belonging to the new owner — and the connector's
        // deprovisioning lookup then returned nothing, silently, forever.
        var store = NewStore($"s{Guid.NewGuid():N}");
        await store.CreateAsync(User("owner", "owner@example.com"));
        await store.CreateAsync(User("other", "other@example.com"));
        await store.SetExternalIdAsync("owner", "conn", "ext-1");

        await store.RemoveExternalIdAsync("other", "conn", "ext-1");

        Assert.Equal("owner", (await store.FindByExternalIdAsync("conn", "ext-1"))?.Id);
    }

    [Fact]
    public async Task AnExternalIdAlreadyHeldCannotBeRepointed()
    {
        var store = NewStore($"s{Guid.NewGuid():N}");
        await store.CreateAsync(User("owner", "owner@example.com"));
        await store.CreateAsync(User("other", "other@example.com"));
        await store.SetExternalIdAsync("owner", "conn", "ext-1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SetExternalIdAsync("other", "conn", "ext-1"));

        Assert.Equal("owner", (await store.FindByExternalIdAsync("conn", "ext-1"))?.Id);

        // Re-asserting the owner's own binding stays idempotent — a retried sync must not 500.
        await store.SetExternalIdAsync("owner", "conn", "ext-1");
        Assert.Equal("owner", (await store.FindByExternalIdAsync("conn", "ext-1"))?.Id);
    }
}

/// <summary>
/// The same guarantees on the SQL provider, which had neither the version check on the document write
/// (F115/F226) nor the conditional email/externalId index claims (F247/F113). Run over SQLite, which is
/// in-process — the semantics under test are the dialect-independent ones (row version, insert-if-absent,
/// conditional delete), all of which SQLite implements the same way PostgreSQL does.
/// </summary>
public sealed class SqlUserStoreStaleWriteTests : IAsyncLifetime
{
    private SqlDataSource _source = null!;

    public Task InitializeAsync()
    {
        _source = SqlTestSource.Sqlite();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _source.DisposeAsync();

    private async Task<SqlUserStore> NewStoreAsync()
    {
        async Task<SqlTable> T(string name)
        {
            await _source.EnsureTableAsync(name);
            return new SqlTable(_source, name);
        }
        return new SqlUserStore(
            await T("Users"), await T("UserEmails"), await T("UserLogins"), await T("UserExternalIds"),
            null, null, EnvPartitioner.Live);
    }

    private static AuthUser User(string id, string email) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        PasswordHash = "old-hash",
        SecurityStamp = "old-stamp",
        LockoutEnabled = true,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task AStaleWriterCannotRevertAPasswordReset()
    {
        var store = await NewStoreAsync();
        await store.CreateAsync(User("u1", "victim@example.com"));

        var stale = await store.GetAsync("u1");
        Assert.NotNull(stale);

        var reset = await store.GetAsync("u1");
        reset!.PasswordHash = "new-hash";
        reset.SecurityStamp = "new-stamp";
        await store.UpdateAsync(reset);

        stale.FirstName = "Renamed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(stale));

        var stored = await store.GetAsync("u1");
        Assert.Equal("new-hash", stored!.PasswordHash);
        Assert.Equal("new-stamp", stored.SecurityStamp);
    }

    [Fact]
    public async Task AStaleWriterCannotClearALockoutItNeverSaw()
    {
        // F226's named consequence: an admin profile update built from a pre-lockout snapshot writes the
        // whole document back, resetting the failure count and dropping LockoutEnd — unlocking an account
        // mid brute-force. The login stamps move the row version, so the snapshot is refused.
        var store = await NewStoreAsync();
        await store.CreateAsync(User("u2", "locked@example.com"));

        var stale = await store.GetAsync("u2");
        Assert.NotNull(stale);

        Assert.True(await store.RecordFailedLoginAsync("u2", maxAttempts: 1, TimeSpan.FromMinutes(30)));

        stale.CompanyName = "Acme";
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(stale));

        var stored = await store.GetAsync("u2");
        Assert.NotNull(stored!.LockoutEnd);
    }

    [Fact]
    public async Task ASecondUserCannotTakeOverAnExistingAddress()
    {
        // F247: the email→userId row is what FindByEmailAsync resolves for password login, reset, SCIM
        // matching and federated linking. It was an unconditional upsert on this backend, so the second
        // of two concurrent registrations for one address took ownership of the first's login identifier.
        var store = await NewStoreAsync();
        await store.CreateAsync(User("u1", "shared@example.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateAsync(User("u2", "shared@example.com")));

        Assert.Equal("u1", (await store.FindByEmailAsync("shared@example.com"))?.Id);
        // The loser left nothing behind: a profile row no lookup can reach is the split-brain state the
        // claim exists to prevent.
        Assert.Null(await store.GetAsync("u2"));
    }

    [Fact]
    public async Task AnExternalIdRowIsOnlyRemovableByTheUserItNames()
    {
        var store = await NewStoreAsync();
        await store.CreateAsync(User("owner", "owner@example.com"));
        await store.CreateAsync(User("other", "other@example.com"));
        await store.SetExternalIdAsync("owner", "conn", "ext-1");

        await store.RemoveExternalIdAsync("other", "conn", "ext-1");

        Assert.Equal("owner", (await store.FindByExternalIdAsync("conn", "ext-1"))?.Id);

        // Its owner can still release it.
        await store.RemoveExternalIdAsync("owner", "conn", "ext-1");
        Assert.Null(await store.FindByExternalIdAsync("conn", "ext-1"));
    }

    [Fact]
    public async Task AnExternalIdAlreadyHeldCannotBeRepointed()
    {
        var store = await NewStoreAsync();
        await store.CreateAsync(User("owner", "owner@example.com"));
        await store.CreateAsync(User("other", "other@example.com"));
        await store.SetExternalIdAsync("owner", "conn", "ext-1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SetExternalIdAsync("other", "conn", "ext-1"));

        Assert.Equal("owner", (await store.FindByExternalIdAsync("conn", "ext-1"))?.Id);
    }
}

/// <summary>
/// F226 on the backend that ships the version column — the <c>_v</c> attribute the class doc says
/// "backs optimistic full-document writes". It was tested against the store's OWN read, which is not the
/// window that matters, and the login stamps did not move it at all.
/// </summary>
[Collection("Dynamo")]
public class DynamoUserStoreStaleWriteTests(DynamoFixture dynamo)
{
    private readonly IAmazonDynamoDB _db = dynamo.CreateClient();

    private async Task<DynamoUserStore> NewStoreAsync(string prefix)
    {
        async Task<DynamoTable> T(string name)
        {
            await DynamoTableProvisioner.EnsureTableAsync(_db, $"{prefix}{name}");
            return new DynamoTable(_db, $"{prefix}{name}");
        }
        return new DynamoUserStore(
            await T("Users"), await T("Emails"), await T("Logins"), await T("ExtIds"), null, null,
            EnvPartitioner.Live);
    }

    private static AuthUser User(string id, string email) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        PasswordHash = "old-hash",
        SecurityStamp = "old-stamp",
        LockoutEnabled = true,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task AStaleWriterCannotRevertAPasswordReset()
    {
        var store = await NewStoreAsync($"sw{Guid.NewGuid():N}");
        await store.CreateAsync(User("u1", "victim@example.com"));

        var stale = await store.GetAsync("u1");
        Assert.NotNull(stale);

        var reset = await store.GetAsync("u1");
        reset!.PasswordHash = "new-hash";
        reset.SecurityStamp = "new-stamp";
        await store.UpdateAsync(reset);

        stale.FirstName = "Renamed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(stale));

        var stored = await store.GetAsync("u1");
        Assert.Equal("new-hash", stored!.PasswordHash);
        Assert.Equal("new-stamp", stored.SecurityStamp);
    }

    [Fact]
    public async Task AStaleWriterCannotClearALockoutItNeverSaw()
    {
        // The exploit F226 names. UserItemAsync rewrites the whole login-state attribute group from the
        // caller's model, and Dyn.PutDate omits a null — which on a full PutItem DELETES lockoutEnd. So a
        // profile update built from a pre-lockout snapshot unlocked a locked-out account mid brute-force.
        // The lockout stamp now moves _v, so that snapshot no longer matches.
        var store = await NewStoreAsync($"sw{Guid.NewGuid():N}");
        await store.CreateAsync(User("u2", "locked@example.com"));

        var stale = await store.GetAsync("u2");
        Assert.NotNull(stale);

        Assert.True(await store.RecordFailedLoginAsync("u2", maxAttempts: 1, TimeSpan.FromMinutes(30)));

        stale.CompanyName = "Acme";
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(stale));

        var stored = await store.GetAsync("u2");
        Assert.NotNull(stored!.LockoutEnd);
    }

    [Fact]
    public async Task ASecondUserCannotTakeOverAnExistingAddress()
    {
        var store = await NewStoreAsync($"sw{Guid.NewGuid():N}");
        await store.CreateAsync(User("u1", "shared@example.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateAsync(User("u2", "shared@example.com")));

        Assert.Equal("u1", (await store.FindByEmailAsync("shared@example.com"))?.Id);
        Assert.Null(await store.GetAsync("u2"));
    }

    [Fact]
    public async Task AnExternalIdRowIsOnlyRemovableByTheUserItNames()
    {
        var store = await NewStoreAsync($"sw{Guid.NewGuid():N}");
        await store.CreateAsync(User("owner", "owner@example.com"));
        await store.CreateAsync(User("other", "other@example.com"));
        await store.SetExternalIdAsync("owner", "conn", "ext-1");

        await store.RemoveExternalIdAsync("other", "conn", "ext-1");
        Assert.Equal("owner", (await store.FindByExternalIdAsync("conn", "ext-1"))?.Id);

        await store.RemoveExternalIdAsync("owner", "conn", "ext-1");
        Assert.Null(await store.FindByExternalIdAsync("conn", "ext-1"));
    }

    [Fact]
    public async Task AnExternalIdAlreadyHeldCannotBeRepointed()
    {
        var store = await NewStoreAsync($"sw{Guid.NewGuid():N}");
        await store.CreateAsync(User("owner", "owner@example.com"));
        await store.CreateAsync(User("other", "other@example.com"));
        await store.SetExternalIdAsync("owner", "conn", "ext-1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SetExternalIdAsync("other", "conn", "ext-1"));

        Assert.Equal("owner", (await store.FindByExternalIdAsync("conn", "ext-1"))?.Id);
    }
}

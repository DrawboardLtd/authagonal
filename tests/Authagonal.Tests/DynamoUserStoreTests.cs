using Amazon.DynamoDBv2;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.AwsProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// DynamoUserStore behavior against real DynamoDB semantics (DynamoDB Local) in the default
/// (plaintext / passthrough) mode — the same surface the Azurite suites pin for the Azure store:
/// CRUD + index upkeep, external ids/logins, search, native cursor paging, the whole-population
/// enumerations, and the attribute-only login-state stamps.
/// </summary>
[Collection("Dynamo")]
public class DynamoUserStoreTests(DynamoFixture dynamo)
{
    private readonly IAmazonDynamoDB _db = dynamo.CreateClient();

    private async Task<DynamoUserStore> NewStoreAsync(string prefix, bool nameIndexes = true, bool extendedIndexes = true)
    {
        async Task<DynamoTable> T(string name)
        {
            await DynamoTableProvisioner.EnsureTableAsync(_db, $"{prefix}{name}");
            return new DynamoTable(_db, $"{prefix}{name}");
        }
        return new DynamoUserStore(
            await T("Users"), await T("Emails"), await T("Logins"), await T("ExtIds"),
            nameIndexes ? await T("FirstNames") : null,
            nameIndexes ? await T("LastNames") : null,
            EnvPartitioner.Live,
            userEmailDomains: extendedIndexes ? await T("EmailDomains") : null,
            userEmailLocalPrefixes: extendedIndexes ? await T("EmailLocalPrefixes") : null);
    }

    private static AuthUser SampleUser(string id, string email, string? first = null, string? last = null) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        FirstName = first,
        LastName = last,
        PasswordHash = "hash-" + id,
        LockoutEnabled = true,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
        Roles = ["user"],
        CustomAttributes = new Dictionary<string, string> { ["team"] = "core" },
    };

    [Fact]
    public async Task Create_Get_RoundTrips_Profile()
    {
        var store = await NewStoreAsync("rt");
        await store.CreateAsync(SampleUser("u1", "alice@example.com", "Alice", "Smith"));

        var read = await store.GetAsync("u1");
        Assert.NotNull(read);
        Assert.Equal("alice@example.com", read!.Email);
        Assert.Equal("Alice", read.FirstName);
        Assert.Equal(["user"], read.Roles);
        Assert.Equal("core", read.CustomAttributes["team"]);
        Assert.True(await store.ExistsAsync("u1"));
        Assert.Null(await store.GetAsync("missing"));
    }

    [Fact]
    public async Task FindByEmail_FollowsEmailChange()
    {
        var store = await NewStoreAsync("em");
        var user = SampleUser("u1", "old@example.com");
        await store.CreateAsync(user);
        Assert.Equal("u1", (await store.FindByEmailAsync("old@example.com"))?.Id);

        user.Email = "new@example.com";
        user.NormalizedEmail = "NEW@EXAMPLE.COM";
        await store.UpdateAsync(user);

        Assert.Null(await store.FindByEmailAsync("old@example.com"));
        Assert.Equal("u1", (await store.FindByEmailAsync("new@example.com"))?.Id);
    }

    [Fact]
    public async Task ExternalIds_SetFindRemove()
    {
        var store = await NewStoreAsync("ext");
        await store.CreateAsync(SampleUser("u1", "e@example.com"));
        await store.SetExternalIdAsync("u1", "client-a", "ext-123");

        Assert.Equal("u1", (await store.FindByExternalIdAsync("client-a", "ext-123"))?.Id);
        Assert.Null(await store.FindByExternalIdAsync("client-a", "other"));

        await store.RemoveExternalIdAsync("u1", "client-a", "ext-123");
        Assert.Null(await store.FindByExternalIdAsync("client-a", "ext-123"));
    }

    [Fact]
    public async Task Logins_AddFindListRemove()
    {
        var store = await NewStoreAsync("lg");
        await store.CreateAsync(SampleUser("u1", "l@example.com"));
        await store.AddLoginAsync(new ExternalLoginInfo { UserId = "u1", Provider = "saml", ProviderKey = "name-id-1", DisplayName = "Alice" });
        await store.AddLoginAsync(new ExternalLoginInfo { UserId = "u1", Provider = "google", ProviderKey = "g-1" });

        var found = await store.FindLoginAsync("saml", "name-id-1");
        Assert.Equal("u1", found?.UserId);
        Assert.Equal("Alice", found?.DisplayName);

        var logins = await store.GetLoginsAsync("u1");
        Assert.Equal(2, logins.Count);

        await store.RemoveLoginAsync("u1", "saml", "name-id-1");
        Assert.Null(await store.FindLoginAsync("saml", "name-id-1"));
        Assert.Single(await store.GetLoginsAsync("u1"));
    }

    [Fact]
    public async Task Search_MatchesEmailPrefix_And_NamePrefix()
    {
        var store = await NewStoreAsync("srch");
        await store.CreateAsync(SampleUser("u1", "wendy@example.com", "Wendy", "Chen"));
        await store.CreateAsync(SampleUser("u2", "walter@example.com", "Walter", "Doe"));
        await store.CreateAsync(SampleUser("u3", "bob@example.com", "Bob", "Wendell"));

        var byEmailPrefix = await store.SearchAsync("wen");
        Assert.Contains(byEmailPrefix, u => u.Id == "u1"); // email prefix
        Assert.Contains(byEmailPrefix, u => u.Id == "u3"); // last-name prefix

        var exact = await store.SearchAsync("walter@example.com");
        Assert.Equal("u2", Assert.Single(exact).Id);
    }

    [Fact]
    public async Task SearchByEmailDomain_ReturnsMembers()
    {
        var store = await NewStoreAsync("dom");
        await store.CreateAsync(SampleUser("u1", "a@acme.com"));
        await store.CreateAsync(SampleUser("u2", "b@acme.com"));
        await store.CreateAsync(SampleUser("u3", "c@other.com"));

        var acme = await store.SearchByEmailDomainAsync("acme.com");
        Assert.Equal(2, acme.Count);
        Assert.DoesNotContain(acme, u => u.Id == "u3");
        Assert.Empty(await store.SearchByEmailDomainAsync("nobody.com"));
    }

    [Fact]
    public async Task ListPage_CursorWalksWholePopulation_NoDuplicates()
    {
        var store = await NewStoreAsync("pg");
        for (var i = 0; i < 30; i++)
            await store.CreateAsync(SampleUser($"u{i:d2}", $"user{i:d2}@example.com"));

        var seen = new HashSet<string>();
        string? token = null;
        var pages = 0;
        do
        {
            var page = await store.ListPageAsync(null, 10, token);
            foreach (var u in page.Users)
                Assert.True(seen.Add(u.Id), $"duplicate {u.Id} across pages");
            token = page.ContinuationToken;
            Assert.True(++pages < 20, "paging did not terminate");
        }
        while (token is not null);

        Assert.Equal(30, seen.Count);
    }

    [Fact]
    public async Task ListByScimClientPage_FiltersByClient()
    {
        var store = await NewStoreAsync("scim");
        for (var i = 0; i < 6; i++)
        {
            var u = SampleUser($"u{i}", $"s{i}@example.com");
            u.ScimProvisionedByClientId = i % 2 == 0 ? "client-a" : "client-b";
            await store.CreateAsync(u);
        }

        var seen = new List<string>();
        string? token = null;
        do
        {
            var page = await store.ListByScimClientPageAsync("client-a", 2, token);
            seen.AddRange(page.Users.Select(u => u.Id));
            token = page.ContinuationToken;
        }
        while (token is not null);

        Assert.Equal(3, seen.Count);
        Assert.All(seen, id => Assert.Contains(id, new[] { "u0", "u2", "u4" }));
    }

    [Fact]
    public async Task EnumerateUserIds_And_LoginStates_StreamEveryone()
    {
        var store = await NewStoreAsync("enum");
        await store.CreateAsync(SampleUser("u1", "e1@example.com"));
        await store.CreateAsync(SampleUser("u2", "e2@example.com"));
        await store.RecordSuccessfulLoginAsync("u2");

        var ids = new List<string>();
        await foreach (var id in store.EnumerateUserIdsAsync()) ids.Add(id);
        Assert.Equal(["u1", "u2"], ids.OrderBy(x => x));

        var states = new Dictionary<string, UserLoginState>();
        await foreach (var s in store.EnumerateLoginStatesAsync()) states[s.Id] = s;
        Assert.Equal(2, states.Count);
        Assert.Null(states["u1"].LastLoginAt);
        Assert.NotNull(states["u2"].LastLoginAt);
        Assert.True(states["u1"].IsActive);
    }

    [Fact]
    public async Task RecordFailedLogin_LocksAtThreshold_AndSuccessClears()
    {
        var store = await NewStoreAsync("lock");
        await store.CreateAsync(SampleUser("u1", "lk@example.com"));

        Assert.False(await store.RecordFailedLoginAsync("u1", 3, TimeSpan.FromMinutes(15)));
        Assert.False(await store.RecordFailedLoginAsync("u1", 3, TimeSpan.FromMinutes(15)));
        Assert.True(await store.RecordFailedLoginAsync("u1", 3, TimeSpan.FromMinutes(15)));

        var locked = await store.GetAsync("u1");
        Assert.NotNull(locked!.LockoutEnd);
        Assert.True(locked.LockoutEnd > DateTimeOffset.UtcNow);
        Assert.Equal(0, locked.AccessFailedCount); // reset on lock

        await store.RecordSuccessfulLoginAsync("u1", rehashedPassword: "rehashed");
        var after = await store.GetAsync("u1");
        Assert.Null(after!.LockoutEnd);
        Assert.Equal(0, after.AccessFailedCount);
        Assert.NotNull(after.LastLoginAt);
        Assert.Equal("rehashed", after.PasswordHash);
    }

    [Fact]
    public async Task ParallelFailedLogins_DoNotLoseIncrements()
    {
        var store = await NewStoreAsync("race");
        await store.CreateAsync(SampleUser("u1", "race@example.com"));

        // 5 concurrent failures with maxAttempts=5: with atomic counting exactly one caller
        // observes the lock (a lost increment would mean none does).
        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => store.RecordFailedLoginAsync("u1", 5, TimeSpan.FromMinutes(15))));
        Assert.Equal(1, results.Count(locked => locked));
    }

    [Fact]
    public async Task Delete_RemovesProfileAndEveryIndex()
    {
        var store = await NewStoreAsync("del");
        await store.CreateAsync(SampleUser("u1", "gone@acme.com", "Greta", "Gone"));
        await store.AddLoginAsync(new ExternalLoginInfo { UserId = "u1", Provider = "saml", ProviderKey = "nid" });

        await store.DeleteAsync("u1");

        Assert.Null(await store.GetAsync("u1"));
        Assert.Null(await store.FindByEmailAsync("gone@acme.com"));
        Assert.Null(await store.FindLoginAsync("saml", "nid"));
        Assert.Empty(await store.SearchByEmailDomainAsync("acme.com"));
        Assert.Empty(await store.SearchAsync("gret"));
    }

    [Fact]
    public async Task Update_OfMissingUser_Creates()
    {
        var store = await NewStoreAsync("upsert");
        await store.UpdateAsync(SampleUser("u1", "up@example.com"));
        Assert.Equal("u1", (await store.FindByEmailAsync("up@example.com"))?.Id);
    }
    /// <summary>
    /// A row that predates the promoted login-state group keeps its password through a login stamp.
    /// </summary>
    /// <remarks>
    /// <c>ReadUserAsync</c> treated the presence of the single <c>failedCount</c> marker as proof that the whole
    /// promoted group was authoritative, and BOTH stamps create <c>failedCount</c> on a row that lacks it while
    /// writing no <c>pwd</c>: the failed-login stamp materialises the group deliberately, and the success stamp
    /// carries <c>pwd</c> only on a rehash. So one login attempt published <c>PasswordHash = null</c>, and
    /// <c>AuthEndpoints</c> forces a Failed verify whenever the stored hash is empty — the account could never
    /// log in again, and forgot-password issues no reset for an account with no local password, so self-service
    /// recovery was closed too. One request per victim, unauthenticated, permanent.
    /// <para>
    /// Rows arrive without the group from a restore, which writes raw rows — so this lands in exactly the
    /// situation where an operator is least able to absorb it. <c>SqlUserStore</c> refuses the partial stamp,
    /// with a comment naming this failure; the Dynamo store never learned it.
    /// </para>
    /// <para>
    /// The overlay now consults each attribute, so the document's hash survives — which also REPAIRS rows
    /// already damaged in production rather than only preventing new damage.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]   // a failed login materialises the group
    [InlineData(false)]  // …and so does a successful one
    public async Task LoginStampOnARowWithoutThePromotedGroup_KeepsThePasswordHash(bool failedLogin)
    {
        var prefix = $"ls{Guid.NewGuid():N}".Substring(0, 12);
        var store = await NewStoreAsync(prefix);

        var user = SampleUser("legacy-1", "legacy@example.com");
        await store.CreateAsync(user);

        // Strip the promoted group, leaving only the document — the shape a raw-row restore produces.
        var table = new DynamoTable(_db, $"{prefix}Users");
        var item = await table.GetAsync(EnvPartitioner.Live.PK("legacy-1"), "profile");
        Assert.NotNull(item);
        foreach (var attr in new[] { "failedCount", "pwd", "pwdPending", "lastLogin", "lockEnabled" })
            item!.Remove(attr);
        await table.PutAsync(item!);

        // Sanity: the document still carries the hash, so the overlay is the only thing that can lose it.
        Assert.Equal("hash-legacy-1", (await store.GetAsync("legacy-1"))!.PasswordHash);

        if (failedLogin)
            await store.RecordFailedLoginAsync("legacy-1", maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(5));
        else
            await store.RecordSuccessfulLoginAsync("legacy-1");

        var after = await store.GetAsync("legacy-1");
        Assert.NotNull(after);
        Assert.Equal("hash-legacy-1", after!.PasswordHash);
    }
}

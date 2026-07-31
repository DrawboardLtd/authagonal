using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.SqlProvider.Clustering;
using Authagonal.SqlProvider.Sql;
using Authagonal.SqlProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Behavioural coverage for the self-hosted SQL provider, run identically against SQLite and
/// PostgreSQL. The two dialects only differ in DDL, JSON accessor and connection, so anything that
/// diverges is a dialect bug — running one suite over both is what catches it.
/// <para>
/// The emphasis is on the semantics the auth server actually depends on and that a naive SQL port
/// would get wrong: exactly-once redemption under concurrency, compare-and-set on rotation, lost-update
/// freedom on the lockout counter, byte-ordinal key ranges, and cursor paging that neither skips nor
/// repeats.
/// </para>
/// </summary>
public abstract class SqlProviderTestsBase : IAsyncLifetime
{
    private SqlDataSource _source = null!;

    protected abstract SqlDataSource CreateSource();

    public Task InitializeAsync()
    {
        _source = CreateSource();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _source.DisposeAsync();

    private async Task<SqlTable> T(string name)
    {
        await _source.EnsureTableAsync(name);
        return new SqlTable(_source, name);
    }

    private static readonly EnvPartitioner Live = EnvPartitioner.Live;

    // ── the primitives everything else rests on ──────────────────────────────────

    [Fact]
    public async Task DeleteReturning_HasExactlyOneWinnerUnderConcurrency()
    {
        var table = await T("Race");
        await table.PutAsync(new SqlRow("code", "grant") { Data = "payload" });

        var winners = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
            await table.DeleteIfExistsReturningAsync("code", "grant") is not null));

        Assert.Equal(1, winners.Count(w => w));
    }

    [Fact]
    public async Task UpdateIfAttrNull_TransitionsOnceAndNeverInserts()
    {
        var table = await T("Cas");
        await table.PutAsync(new SqlRow("k", "grant") { Data = "v1" });

        var marked = new SqlRow("k", "grant") { Data = "v2" };
        marked.PutS("consumedAt", "2026-07-29T00:00:00.0000000+00:00");

        Assert.True(await table.UpdateIfAttrNullAsync(marked, "consumedAt"));
        Assert.False(await table.UpdateIfAttrNullAsync(marked, "consumedAt"));

        // Must not resurrect a row that was deleted concurrently.
        Assert.False(await table.UpdateIfAttrNullAsync(new SqlRow("ghost", "grant"), "consumedAt"));
        Assert.Null(await table.GetAsync("ghost", "grant"));
    }

    [Fact]
    public async Task UpdateAttrs_LosesNoIncrementsUnderContention()
    {
        var table = await T("Counter");
        await table.PutAsync(new SqlRow("c", "row"));

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            await table.UpdateAttrsAsync("c", "row", r => { r.PutN("n", r.GetN("n") + 1); return true; }, maxAttempts: 50)));

        Assert.Equal(20, (await table.GetAsync("c", "row"))!.GetN("n"));
    }

    [Fact]
    public async Task UpdateAttrs_LeavesTheDocumentUntouched()
    {
        var table = await T("Overlay");
        await table.PutAsync(new SqlRow("u", "profile") { Data = "ciphertext" });

        await table.UpdateAttrsAsync("u", "profile", r => { r.PutN("failedCount", 3); return true; });

        var row = await table.GetAsync("u", "profile");
        Assert.Equal("ciphertext", row!.Data);
        Assert.Equal(3, row.GetN("failedCount"));
    }

    [Fact]
    public async Task KeyRangesAreByteOrdinal_NotLinguistic()
    {
        // The whole key scheme (prefix bounds, env ranges, the "{day}#~" expiry bound) assumes byte
        // order. Under a linguistic collation 'AB' sorts after 'aa' and '~' sorts before letters, and
        // these scans silently return the wrong rows.
        var table = await T("Ordering");
        foreach (var pk in new[] { "AB", "AC", "aa", "sandbox|x", "sandbox|y", "live" })
            await table.PutAsync(new SqlRow(pk, "config"));
        await table.PutAsync(new SqlRow("exp_0", "2026-07-29#abc"));
        await table.PutAsync(new SqlRow("exp_0", "2026-07-30#abc"));

        var prefixed = await Collect(table.QueryAsync(new SqlKeyFilter { PkPrefix = "A" }));
        Assert.Equal(["AB", "AC"], prefixed.Select(r => r.Pk));

        var env = await Collect(table.QueryAsync(new SqlKeyFilter { Sk = "config", PkFrom = "sandbox|", PkUntil = "sandbox|~" }));
        Assert.Equal(["sandbox|x", "sandbox|y"], env.Select(r => r.Pk));

        var expired = await Collect(table.QueryAsync(SqlKeyFilter.Partition("exp_0") with { SkAtMost = "2026-07-29#~" }));
        Assert.Single(expired);
        Assert.Equal("2026-07-29#abc", expired[0].Sk);
    }

    [Fact]
    public async Task CursorPaging_CoversEveryRowExactlyOnce()
    {
        var table = await T("Paging");
        for (var i = 0; i < 25; i++) await table.PutAsync(new SqlRow("p", $"{i:D3}"));

        var seen = new List<string>();
        string? token = null;
        var pages = 0;
        do
        {
            var (rows, next) = await table.ScanPageAsync(SqlKeyFilter.Partition("p"), token, 7);
            seen.AddRange(rows.Select(r => r.Sk));
            token = next;
        }
        while (token is not null && ++pages < 20);

        Assert.Null(token);
        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
    }

    [Fact]
    public async Task EnumerationToleratesDeletingAsItGoes()
    {
        // The index-cleanup and expiry-sweep loops delete while walking a query. On SQLite a write
        // issued under a live reader on the same database would contend with itself.
        var table = await T("Sweep");
        for (var i = 0; i < 1200; i++) await table.PutAsync(new SqlRow("bulk", $"{i:D5}"));

        var visited = 0;
        await foreach (var row in table.QueryPartitionAsync("bulk"))
        {
            visited++;
            await table.DeleteAsync(row.Pk, row.Sk);
        }

        Assert.Equal(1200, visited);
        Assert.Empty(await Collect(table.QueryPartitionAsync("bulk")));
    }

    // ── grants ───────────────────────────────────────────────────────────────────

    private async Task<SqlGrantStore> GrantStoreAsync() => new(
        await T("Grants"), await T("GrantsBySubject"), await T("GrantsByExpiry"),
        Live, NullLogger<SqlGrantStore>.Instance);

    private static PersistedGrant Grant(string key, string subject = "user-1", string client = "web", string type = "authorization_code")
        => new()
        {
            Key = key,
            Type = type,
            SubjectId = subject,
            ClientId = client,
            Data = """{"scope":"openid"}""",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };

    [Fact]
    public async Task Grant_RoundTripsAndIsIndexedBySubject()
    {
        var store = await GrantStoreAsync();
        await store.StoreAsync(Grant("code-1"));
        await store.StoreAsync(Grant("code-2", client: "cli"));

        var read = await store.GetAsync("code-1");
        Assert.NotNull(read);
        Assert.Equal("web", read!.ClientId);
        Assert.Null(await store.GetAsync("nope"));

        Assert.Equal(2, (await store.GetBySubjectAsync("user-1")).Count);

        await store.RemoveAllBySubjectAndClientAsync("user-1", "cli");
        Assert.Single(await store.GetBySubjectAsync("user-1"));
        Assert.Null(await store.GetAsync("code-2"));
    }

    [Fact]
    public async Task Grant_IsRedeemableExactlyOnce()
    {
        var store = await GrantStoreAsync();
        await store.StoreAsync(Grant("single-use"));

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.TryConsumeAsync("single-use")));

        Assert.Equal(1, results.Count(r => r));
        Assert.Null(await store.GetAsync("single-use"));
        Assert.Empty(await store.GetBySubjectAsync("user-1"));
    }

    [Fact]
    public async Task Grant_MarkConsumedHasOneWinnerAndKeepsTheRow()
    {
        var store = await GrantStoreAsync();
        await store.StoreAsync(Grant("refresh-1", type: "refresh_token"));

        var grant = await store.GetAsync("refresh-1");
        grant!.Key = "refresh-1"; // grants read back from storage carry no key
        grant.Data = """{"successor":"refresh-2"}""";

        Assert.True(await store.TryMarkConsumedAsync(grant));
        // A racing rotation must lose, and the row must survive for replay detection.
        Assert.False(await store.TryMarkConsumedAsync(grant));

        var after = await store.GetAsync("refresh-1");
        Assert.NotNull(after);
        Assert.NotNull(after!.ConsumedAt);
        Assert.Contains("successor", after.Data);
    }

    [Fact]
    public async Task Grant_ExpirySweepRemovesOnlyExpiredAcrossShards()
    {
        var store = await GrantStoreAsync();
        // Enough keys that every expiry shard is exercised.
        for (var i = 0; i < 12; i++)
        {
            var expired = Grant($"old-{i}");
            expired.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-2);
            await store.StoreAsync(expired);
            await store.StoreAsync(Grant($"live-{i}"));
        }

        await store.RemoveExpiredAsync(DateTimeOffset.UtcNow.AddDays(-1));

        for (var i = 0; i < 12; i++)
        {
            Assert.Null(await store.GetAsync($"old-{i}"));
            Assert.NotNull(await store.GetAsync($"live-{i}"));
        }
        Assert.Equal(12, (await store.GetBySubjectAsync("user-1")).Count);
    }

    // ── users ────────────────────────────────────────────────────────────────────

    private async Task<SqlUserStore> UserStoreAsync(IChangeWriter? tombstones = null) => new(
        await T("Users"), await T("UserEmails"), await T("UserLogins"), await T("UserExternalIds"),
        await T("UserFirstNames"), await T("UserLastNames"), Live, tombstones,
        await T("UserEmailDomains"), await T("UserEmailLocalPrefixes"));

    private static AuthUser User(string id, string email, string? first = null, string? last = null, string? org = null)
        => new()
        {
            Id = id,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = first,
            LastName = last,
            OrganizationId = org,
            PasswordHash = "hash",
            LockoutEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task User_RoundTripsAndIsFoundByEveryIndex()
    {
        var store = await UserStoreAsync();
        await store.CreateAsync(User("u1", "alice@acme.com", "Alice", "Anderson", "org-1"));
        await store.SetExternalIdAsync("u1", "web", "ext-42");
        await store.AddLoginAsync(new ExternalLoginInfo
        {
            UserId = "u1", Provider = "google", ProviderKey = "google-sub-1", DisplayName = "Alice",
        });

        Assert.Equal("alice@acme.com", (await store.GetAsync("u1"))?.Email);
        Assert.Equal("u1", (await store.FindByEmailAsync("ALICE@ACME.COM"))?.Id);
        Assert.Equal("u1", (await store.FindByExternalIdAsync("web", "ext-42"))?.Id);
        Assert.Equal("u1", (await store.FindLoginAsync("google", "google-sub-1"))?.UserId);
        Assert.Single(await store.GetLoginsAsync("u1"));
        Assert.True(await store.ExistsAsync("u1"));
        Assert.Null(await store.GetAsync("missing"));
    }

    /// <summary>
    /// CreateAsync is insert-only. It used to be an upsert on this provider (INSERT … ON CONFLICT DO
    /// UPDATE), so creating a user whose id already existed silently replaced that account's password
    /// hash, roles, MFA flag and email — while the Azure provider failed closed on the same call. The one
    /// caller that does not generate its own id is OIDC JIT federation with UseUpstreamSubjectAsUserId,
    /// where the id is the upstream `sub`.
    /// </summary>
    [Fact]
    public async Task User_CreateDoesNotOverwriteAnExistingAccount()
    {
        var store = await UserStoreAsync();
        await store.CreateAsync(User("u1", "victim@acme.com", "Victim", "Real", "org-1"));

        var victim = await store.GetAsync("u1");
        victim!.PasswordHash = "victim-hash";
        victim.Roles = ["user"];
        await store.UpdateAsync(victim);

        // A second create on the same id must be refused, not applied.
        var collide = User("u1", "attacker@evil.example", "Attacker", "Fake", "org-2");
        collide.PasswordHash = "attacker-hash";
        collide.Roles = ["tenant-admin"];
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(collide));

        var after = await store.GetAsync("u1");
        Assert.Equal("victim@acme.com", after!.Email);
        Assert.Equal("victim-hash", after.PasswordHash);
        Assert.Contains("user", after.Roles);
        Assert.DoesNotContain("tenant-admin", after.Roles);

        // The victim is still findable by their own email, and the attacker's never resolves to them.
        Assert.Equal("u1", (await store.FindByEmailAsync("VICTIM@ACME.COM"))?.Id);
        Assert.Null(await store.FindByEmailAsync("ATTACKER@EVIL.EXAMPLE"));
    }

    [Fact]
    public async Task User_EmailChangeMovesTheIndexWithNoStaleHit()
    {
        var store = await UserStoreAsync();
        await store.CreateAsync(User("u1", "old@acme.com", "Alice"));

        var user = await store.GetAsync("u1");
        user!.Email = "new@example.com";
        user.NormalizedEmail = "NEW@EXAMPLE.COM";
        await store.UpdateAsync(user);

        Assert.Equal("u1", (await store.FindByEmailAsync("new@example.com"))?.Id);
        Assert.Null(await store.FindByEmailAsync("old@acme.com"));
        Assert.Single(await store.SearchByEmailDomainAsync("example.com"));
        Assert.Empty(await store.SearchByEmailDomainAsync("acme.com"));
    }

    [Fact]
    public async Task User_DeleteClearsEveryIndex()
    {
        var store = await UserStoreAsync();
        await store.CreateAsync(User("u1", "alice@acme.com", "Alice", "Anderson"));
        await store.SetExternalIdAsync("u1", "web", "ext-42");
        await store.AddLoginAsync(new ExternalLoginInfo { UserId = "u1", Provider = "google", ProviderKey = "g-1" });

        await store.DeleteAsync("u1");

        Assert.Null(await store.GetAsync("u1"));
        Assert.Null(await store.FindByEmailAsync("alice@acme.com"));
        Assert.Null(await store.FindLoginAsync("google", "g-1"));
        Assert.Empty(await store.SearchAsync("ALICE"));
        Assert.Empty(await store.SearchByEmailDomainAsync("acme.com"));
    }

    [Fact]
    public async Task User_SearchMatchesIdEmailAndNamePrefixes()
    {
        var store = await UserStoreAsync();
        await store.CreateAsync(User("u1", "alice@acme.com", "Alice", "Anderson"));
        await store.CreateAsync(User("u2", "bob@acme.com", "Bob", "Brown"));

        Assert.Equal("u1", (await store.SearchAsync("u1")).Single().Id);
        Assert.Equal("u1", (await store.SearchAsync("alice@acme.com")).Single().Id);
        Assert.Equal("u1", (await store.SearchAsync("ALIC")).Single().Id);
        Assert.Equal("u1", (await store.SearchAsync("ANDER")).Single().Id);
        Assert.Empty(await store.SearchAsync("zzz"));
        Assert.Equal(2, (await store.SearchByEmailDomainAsync("acme.com")).Count);
    }

    [Fact]
    public async Task User_CursorPagingAndOrgFilterAgree()
    {
        var store = await UserStoreAsync();
        for (var i = 0; i < 12; i++)
            await store.CreateAsync(User($"u{i:D2}", $"user{i}@acme.com", org: i % 2 == 0 ? "org-a" : "org-b"));

        var ids = new List<string>();
        string? token = null;
        var pages = 0;
        do
        {
            var page = await store.ListPageAsync("org-a", 3, token);
            ids.AddRange(page.Users.Select(u => u.Id));
            token = page.ContinuationToken;
        }
        while (token is not null && ++pages < 20);

        Assert.Equal(6, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Equal(0, int.Parse(id[1..]) % 2));

        var (offsetPage, hasMore) = await store.ListAsync("org-a", 0, 4);
        Assert.Equal(4, offsetPage.Count);
        Assert.True(hasMore);

        Assert.Equal(12, await CountAsync(store.EnumerateUserIdsAsync()));
        Assert.Equal(12, await CountAsync(store.EnumerateLoginStatesAsync()));
    }

    [Fact]
    public async Task User_LockoutCounterSurvivesParallelFailures()
    {
        var store = await UserStoreAsync();
        await store.CreateAsync(User("u1", "alice@acme.com"));

        // 4 concurrent failures against a threshold of 5 must land 4 increments — a plain
        // read-modify-write would lose some and let an attacker exceed the threshold.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            store.RecordFailedLoginAsync("u1", maxAttempts: 5, TimeSpan.FromMinutes(15))));

        Assert.Equal(4, (await store.GetAsync("u1"))!.AccessFailedCount);

        var locked = await store.RecordFailedLoginAsync("u1", maxAttempts: 5, TimeSpan.FromMinutes(15));
        Assert.True(locked);

        var lockedUser = await store.GetAsync("u1");
        Assert.NotNull(lockedUser!.LockoutEnd);
        Assert.Equal(0, lockedUser.AccessFailedCount);
    }

    [Fact]
    public async Task User_SuccessfulLoginClearsLockoutAndKeepsTheProfile()
    {
        var store = await UserStoreAsync();
        await store.CreateAsync(User("u1", "alice@acme.com", "Alice", "Anderson"));
        await store.RecordFailedLoginAsync("u1", maxAttempts: 5, TimeSpan.FromMinutes(15));

        await store.RecordSuccessfulLoginAsync("u1", rehashedPassword: "rehashed");

        var user = await store.GetAsync("u1");
        Assert.Equal(0, user!.AccessFailedCount);
        Assert.Null(user.LockoutEnd);
        Assert.NotNull(user.LastLoginAt);
        Assert.Equal("rehashed", user.PasswordHash);
        // The document is not rewritten by the stamp, so the rest of the profile must survive it.
        Assert.Equal("Alice", user.FirstName);
        Assert.Equal("alice@acme.com", user.Email);
    }

    [Fact]
    public async Task User_LoginStampOnAMissingUserIsANoOp()
    {
        var store = await UserStoreAsync();
        await store.RecordSuccessfulLoginAsync("ghost");
        Assert.False(await store.RecordFailedLoginAsync("ghost", 5, TimeSpan.FromMinutes(15)));
        Assert.Null(await store.GetAsync("ghost"));
    }

    [Fact]
    public async Task User_ScimClientListingIsFiltered()
    {
        var store = await UserStoreAsync();
        var scim = User("u1", "scim@acme.com");
        scim.ScimProvisionedByClientId = "okta";
        await store.CreateAsync(scim);
        await store.CreateAsync(User("u2", "local@acme.com"));

        var (users, _) = await store.ListByScimClientAsync("okta", 0, 10);
        Assert.Equal("u1", users.Single().Id);
    }

    // ── config stores ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClientStore_RoundTripsAndLists()
    {
        var store = new SqlClientStore(await T("Clients"), Live);
        await store.UpsertAsync(new OAuthClient
        {
            ClientId = "web",
            ClientName = "Web App",
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            RedirectUris = ["https://app.example.com/callback"],
            AllowedScopes = ["openid", "profile"],
            RequirePkce = true,
            MfaPolicy = MfaPolicy.Required,
        });
        await store.UpsertAsync(new OAuthClient { ClientId = "cli", ClientName = "CLI" });

        var read = await store.GetAsync("web");
        Assert.Equal("Web App", read!.ClientName);
        Assert.Equal(["authorization_code", "refresh_token"], read.AllowedGrantTypes);
        Assert.Equal(MfaPolicy.Required, read.MfaPolicy);
        Assert.Equal(2, (await store.GetAllAsync()).Count);

        await store.DeleteAsync("cli");
        Assert.Single(await store.GetAllAsync());
        Assert.Null(await store.GetAsync("cli"));
    }

    [Fact]
    public async Task SigningKeyStore_TracksTheActiveKey()
    {
        var store = new SqlSigningKeyStore(await T("SigningKeys"), Live);
        await store.StoreAsync(new SigningKeyInfo
        {
            KeyId = "k1", Algorithm = "RS256", KeyMaterialJson = "{}", IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
        });
        await store.StoreAsync(new SigningKeyInfo
        {
            KeyId = "k0", Algorithm = "RS256", KeyMaterialJson = "{}", IsActive = false,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-90), ExpiresAt = DateTimeOffset.UtcNow,
        });

        Assert.Equal("k1", (await store.GetActiveKeyAsync())?.KeyId);
        Assert.Equal(2, (await store.GetAllAsync()).Count);

        await store.DeactivateKeyAsync("k1");
        Assert.Null(await store.GetActiveKeyAsync());

        await store.DeleteAsync("k0");
        Assert.Single(await store.GetAllAsync());
    }

    [Fact]
    public async Task RoleAndScopeStores_LookUpByName()
    {
        var roles = new SqlRoleStore(await T("Roles"), Live);
        await roles.CreateAsync(new Role { Id = "r1", Name = "admin", CreatedAt = DateTimeOffset.UtcNow });
        Assert.Equal("r1", (await roles.GetByNameAsync("admin"))?.Id);
        Assert.Null(await roles.GetByNameAsync("nope"));
        Assert.Single(await roles.ListAsync());

        var scopes = new SqlScopeStore(await T("Scopes"), Live);
        await scopes.CreateAsync(new Scope { Name = "api.read", DisplayName = "Read", CreatedAt = DateTimeOffset.UtcNow });
        Assert.Equal("Read", (await scopes.GetAsync("api.read"))?.DisplayName);
        await scopes.DeleteAsync("api.read");
        Assert.Empty(await scopes.ListAsync());
    }

    [Fact]
    public async Task SsoDomainStore_DeletesByConnection()
    {
        var store = new SqlSsoDomainStore(await T("SsoDomains"), Live);
        await store.UpsertAsync(new SsoDomain { Domain = "Acme.com", ProviderType = "saml", ConnectionId = "c1", Scheme = "saml-c1" });
        await store.UpsertAsync(new SsoDomain { Domain = "acme.net", ProviderType = "saml", ConnectionId = "c1", Scheme = "saml-c1" });
        await store.UpsertAsync(new SsoDomain { Domain = "other.com", ProviderType = "oidc", ConnectionId = "c2", Scheme = "oidc-c2" });

        Assert.Equal("c1", (await store.GetAsync("ACME.COM"))?.ConnectionId); // domain keys are case-folded
        Assert.Equal(3, (await store.GetAllAsync()).Count);

        await store.DeleteByConnectionAsync("c1");
        Assert.Equal("other.com", (await store.GetAllAsync()).Single().Domain);
    }

    [Fact]
    public async Task RevokedTokenStore_IgnoresRevocationsPastNaturalExpiry()
    {
        var store = new SqlRevokedTokenStore(await T("RevokedTokens"), Live);
        await store.AddAsync("jti-live", DateTimeOffset.UtcNow.AddMinutes(5), "web");
        await store.AddAsync("jti-stale", DateTimeOffset.UtcNow.AddMinutes(-5), "web");

        Assert.True(await store.IsRevokedAsync("jti-live"));
        Assert.False(await store.IsRevokedAsync("jti-stale"));
        Assert.False(await store.IsRevokedAsync("never-seen"));
    }

    [Fact]
    public async Task ScimTokenStore_KeepsForwardAndReverseRowsInSync()
    {
        var store = new SqlScimTokenStore(await T("ScimTokens"), Live);
        await store.StoreAsync(new ScimToken
        {
            TokenId = "t1", ClientId = "okta", TokenHash = "hash-1", CreatedAt = DateTimeOffset.UtcNow,
        });

        Assert.Equal("t1", (await store.FindByHashAsync("hash-1"))?.TokenId);
        Assert.Single(await store.GetByClientAsync("okta"));

        await store.RevokeAsync("t1", "okta");
        Assert.True((await store.FindByHashAsync("hash-1"))!.IsRevoked);

        await store.DeleteAsync("t1", "okta");
        Assert.Null(await store.FindByHashAsync("hash-1"));
        Assert.Empty(await store.GetByClientAsync("okta"));
    }

    [Fact]
    public async Task MfaStore_ConsumesAChallengeExactlyOnceAndClearsTheWebAuthnIndex()
    {
        var store = new SqlMfaStore(
            await T("MfaCredentials"), await T("MfaChallenges"), await T("MfaWebAuthnIndex"), Live);

        await store.StoreChallengeAsync(new MfaChallenge
        {
            ChallengeId = "ch-1", UserId = "u1",
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        });
        Assert.NotNull(await store.GetChallengeAsync("ch-1"));

        var consumed = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ConsumeChallengeAsync("ch-1")));
        Assert.Equal(1, consumed.Count(c => c is not null));

        await store.StoreChallengeAsync(new MfaChallenge
        {
            ChallengeId = "ch-old", UserId = "u1",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        Assert.Null(await store.GetChallengeAsync("ch-old"));

        byte[] credentialId = [1, 2, 3, 4];
        await store.StoreWebAuthnCredentialIdMappingAsync(credentialId, "u1", "cred-1");
        Assert.Equal(("u1", "cred-1"), await store.FindByWebAuthnCredentialIdAsync(credentialId));
        await store.DeleteWebAuthnCredentialIdMappingAsync(credentialId);
        Assert.Null(await store.FindByWebAuthnCredentialIdAsync(credentialId));
    }

    [Fact]
    public async Task OidcStateStore_IsStrictlySingleUse()
    {
        var store = new SqlOidcStateStore(await T("OidcStateStore"), TimeSpan.FromMinutes(10));
        await store.StoreAsync("state-1", "conn-1", "https://app/cb", "verifier", "nonce");

        var consumed = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ConsumeAsync("state-1")));
        Assert.Equal(1, consumed.Count(c => c is not null));
        Assert.Equal("conn-1", consumed.Single(c => c is not null)!.ConnectionId);
        Assert.Null(await store.ConsumeAsync("state-1"));
    }

    [Fact]
    public async Task SamlReplayCache_DetectsReplayedAssertionsAndConsumesRequests()
    {
        var cache = new SqlSamlReplayCache(await T("SamlReplayCache"), TimeSpan.FromMinutes(10));

        await cache.StoreRequestAsync("req-1", "conn-1", "https://app/return");
        var state = await cache.ValidateAndConsumeRequestAsync("req-1");
        Assert.Equal("conn-1", state?.ConnectionId);
        Assert.Equal("https://app/return", state?.ReturnUrl);
        Assert.Null(await cache.ValidateAndConsumeRequestAsync("req-1")); // replay

        var firstSightings = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => cache.CheckAndStoreAssertionIdAsync("assertion-1")));
        Assert.Equal(1, firstSightings.Count(f => f));
    }

    [Fact]
    public async Task UpstreamRefreshTokenStore_HidesExpiredTokens()
    {
        var store = new SqlUpstreamRefreshTokenStore(await T("UpstreamRefreshTokens"), Live);
        await store.SetAsync("u1", "conn", "sess", "refresh-token", DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal("refresh-token", await store.GetAsync("u1", "conn", "sess"));

        await store.SetAsync("u1", "conn", "old", "stale", DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Null(await store.GetAsync("u1", "conn", "old"));

        await store.RemoveAsync("u1", "conn", "sess");
        Assert.Null(await store.GetAsync("u1", "conn", "sess"));
    }

    // ── change log, TTL reaper, clustering ───────────────────────────────────────

    [Fact]
    public async Task ChangeWriter_RecordsTombstonesForDeletes()
    {
        var tombstoneTable = await T("Tombstones");
        var writer = new SqlChangeWriter(tombstoneTable);
        var store = new SqlClientStore(await T("Clients"), Live, writer);

        await store.UpsertAsync(new OAuthClient { ClientId = "web", ClientName = "Web" });
        await store.DeleteAsync("web");
        await store.DeleteAsync("never-existed");

        var rows = await Collect(tombstoneTable.QueryPartitionAsync("Clients"));
        var row = Assert.Single(rows);
        Assert.Equal("web|config", row.Sk);
        Assert.Equal("D", row.GetStr("op"));
        Assert.NotEqual(default, row.GetDate("deletedAt"));
    }

    [Fact]
    public async Task ExpiryReaper_RemovesOnlyRowsPastTheirTtl()
    {
        var table = await T("Ttl");
        await table.PutAsync(new SqlRow("t", "dead") { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        await table.PutAsync(new SqlRow("t", "live") { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
        await table.PutAsync(new SqlRow("t", "forever"));

        Assert.Equal(1, await table.DeleteExpiredAsync(DateTimeOffset.UtcNow));

        var remaining = await Collect(table.QueryPartitionAsync("t"));
        Assert.Equal(["forever", "live"], remaining.Select(r => r.Sk));
    }

    [Fact]
    public async Task Lease_AdmitsOneHolderAndAllowsTakeoverOnlyAfterExpiry()
    {
        var leases = new SqlLeaseProvider(await T("ClusterLeases"), NullLogger<SqlLeaseProvider>.Instance);

        Assert.True(await leases.TryAcquireOrRenewAsync("tokens", "node-a", TimeSpan.FromMinutes(1)));
        Assert.True(await leases.TryAcquireOrRenewAsync("tokens", "node-a", TimeSpan.FromMinutes(1))); // renew
        Assert.False(await leases.TryAcquireOrRenewAsync("tokens", "node-b", TimeSpan.FromMinutes(1)));

        // A release by a node that no longer holds it must not free the live lease.
        await leases.ReleaseAsync("tokens", "node-b");
        Assert.False(await leases.TryAcquireOrRenewAsync("tokens", "node-b", TimeSpan.FromMinutes(1)));

        await leases.ReleaseAsync("tokens", "node-a");
        Assert.True(await leases.TryAcquireOrRenewAsync("tokens", "node-b", TimeSpan.FromMinutes(1)));

        // An expired lease is takeable.
        Assert.True(await leases.TryAcquireOrRenewAsync("jobs", "node-a", TimeSpan.FromSeconds(-1)));
        Assert.True(await leases.TryAcquireOrRenewAsync("jobs", "node-b", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task ClusterBus_DeliversPublishedEventsToSubscribers()
    {
        var bus = new SqlClusterEventBus(
            await T("ClusterEvents"), TimeSpan.FromMilliseconds(50), NullLogger<SqlClusterEventBus>.Instance);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = bus.Subscribe("keys", (payload, _) =>
        {
            received.TrySetResult(System.Text.Encoding.UTF8.GetString(payload.Span));
            return Task.CompletedTask;
        });

        await bus.StartAsync(CancellationToken.None);
        try
        {
            await bus.PublishAsync("keys", System.Text.Encoding.UTF8.GetBytes("rotated"));
            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(received.Task, completed);
            Assert.Equal("rotated", await received.Task);
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
            await bus.DisposeAsync();
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static async Task<List<SqlRow>> Collect(IAsyncEnumerable<SqlRow> rows)
    {
        var list = new List<SqlRow>();
        await foreach (var row in rows) list.Add(row);
        return list;
    }

    private static async Task<int> CountAsync<T>(IAsyncEnumerable<T> items)
    {
        var count = 0;
        await foreach (var _ in items) count++;
        return count;
    }
}

/// <summary>The suite against SQLite — the zero-dependency single-node backend.</summary>
public sealed class SqliteProviderTests : SqlProviderTestsBase
{
    protected override SqlDataSource CreateSource() => SqlTestSource.Sqlite();
}

/// <summary>
/// The same suite against a real PostgreSQL server, on a database with a linguistic (ICU) collation —
/// see <see cref="PostgresFixture"/> for why that collation is the interesting one.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresProviderTests(PostgresFixture postgres) : SqlProviderTestsBase
{
    protected override SqlDataSource CreateSource() => SqlTestSource.Postgres(postgres.ConnectionString);
}

/// <summary>
/// F126 — a table provisioned out-of-band without the byte-ordinal collation pin must be refused, not
/// silently accepted.
/// </summary>
/// <remarks>
/// EnsureTableAsync is all <c>CREATE ... IF NOT EXISTS</c>, so against an existing table it verified
/// nothing — and the rest of the suite could never catch that, because every table it touches is one
/// this code created (with the pin). The failure mode is silent under-matching, not an error: on the
/// ICU-collated database this fixture uses, <c>'}'</c> sorts before <c>'|'</c>, so a half-open prefix
/// scan for <c>"login|"</c> excludes every row it was meant to return and an external-login lookup
/// simply comes back empty.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresCollationVerificationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task PreExistingTableWithoutTheCollationPin_IsRefusedWithTheFix()
    {
        var schema = $"verify_{Guid.NewGuid():N}";
        const string table = "Grants";

        await using (var connection = new Npgsql.NpgsqlConnection(postgres.ConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            // What a DBA's migration or a Terraform module writes when it does not know the pin
            // matters: plain TEXT columns, inheriting the database's ICU collation.
            cmd.CommandText =
                $"CREATE SCHEMA \"{schema}\";" +
                $"CREATE TABLE \"{schema}\".\"{table}\" (" +
                "  pk TEXT NOT NULL, sk TEXT NOT NULL, data TEXT," +
                "  attrs JSONB NOT NULL DEFAULT '{}'::jsonb, version BIGINT NOT NULL DEFAULT 0," +
                "  expires_at TEXT, PRIMARY KEY (pk, sk));";
            await cmd.ExecuteNonQueryAsync();
        }

        var source = SqlTestSource.Postgres(postgres.ConnectionString, schema);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.EnsureTableAsync(table));

        // The message has to be actionable — an operator who hits this at startup needs the DDL, not
        // a complaint.
        Assert.Contains("collation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COLLATE \"C\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("REINDEX", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATableThisCodeCreated_Passes()
    {
        var source = SqlTestSource.Postgres(postgres.ConnectionString);
        await source.EnsureTableAsync("Grants");
    }
}

using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// Increment 3 (cont.): the externalId blind index (mirrors email) and the NEW email-domain index
/// ("all users @X"). externalId keys the lookup on a token; domain search is exact-match on HMAC(domain).
/// Both stay findable across the migration window (dual-read) and behave as today when off. Azurite.
/// </summary>
[Collection("Azurite")]
public class ExternalIdAndDomainIndexTests(AzuriteFixture azurite)
{
    private sealed class FakeTokenizer : IIndexTokenizer
    {
        public const string Prefix = "tok_";
        public static string Token(string v) => Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v)));
        public Task<string> TokenizeAsync(string value, CancellationToken ct = default) => Task.FromResult(Token(value));
        public Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(values.Select(Token).ToList());
    }

    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableUserStore NewStore(string prefix, IIndexTokenizer? tokenizer)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        return new TableUserStore(T("Users"), T("Emails"), T("Logins"), T("ExtIds"), null, null,
            EnvPartitioner.Live, indexTokenizer: tokenizer, userEmailDomainsTable: T("EmailDomains"),
            userEmailLocalPrefixesTable: T("EmailLocalPrefixes"));
    }

    private static AuthUser User(string id, string email) => new()
    {
        Id = id,
        Email = email.ToLowerInvariant(),
        NormalizedEmail = email.ToUpperInvariant(),
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<bool> RowExists<T>(string table, string pk, string rk) where T : class, ITableEntity
    {
        try { await _svc.GetTableClient(table).GetEntityAsync<T>(pk, rk); return true; }
        catch (RequestFailedException ex) when (ex.Status == 404) { return false; }
    }

    // ── externalId ──────────────────────────────────────────────

    [Fact]
    public async Task ExternalId_KeyedOnToken_AndResolves()
    {
        var prefix = $"extid{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        await store.CreateAsync(User("u1", "ada@acme.test"));
        await store.SetExternalIdAsync("u1", "client-a", "EXT-123");

        Assert.True(await RowExists<UserExternalIdEntity>($"{prefix}ExtIds", FakeTokenizer.Token("client-a|EXT-123"), UserExternalIdEntity.LookupRowKey));
        Assert.False(await RowExists<UserExternalIdEntity>($"{prefix}ExtIds", "client-a|EXT-123", UserExternalIdEntity.LookupRowKey));
        Assert.Equal("u1", (await store.FindByExternalIdAsync("client-a", "EXT-123"))!.Id);
    }

    [Fact]
    public async Task ExternalId_LegacyPlaintext_FoundViaDualRead()
    {
        var prefix = $"extid{Guid.NewGuid():N}";
        var plain = NewStore(prefix, tokenizer: null);
        await plain.CreateAsync(User("u1", "ada@acme.test"));
        await plain.SetExternalIdAsync("u1", "client-a", "EXT-123"); // plaintext-keyed

        var found = await NewStore(prefix, new FakeTokenizer()).FindByExternalIdAsync("client-a", "EXT-123");
        Assert.Equal("u1", found!.Id);
    }

    [Fact]
    public async Task ExternalId_Remove_DeletesLookup()
    {
        var prefix = $"extid{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        await store.CreateAsync(User("u1", "ada@acme.test"));
        await store.SetExternalIdAsync("u1", "client-a", "EXT-123");
        await store.RemoveExternalIdAsync("u1", "client-a", "EXT-123");

        Assert.Null(await store.FindByExternalIdAsync("client-a", "EXT-123"));
    }

    // Mirrors TableUserStore.DomainBucketOf — the domain index buckets members by userId so a big
    // single-domain tenant doesn't hammer one partition. Kept in sync deliberately: a bucketing change
    // should break these layout assertions.
    private static string Bucket(string userId)
    {
        uint h = 2166136261u;
        foreach (var ch in userId) { h ^= ch; h *= 16777619u; }
        return ((int)(h % 16u)).ToString("x");
    }

    // ── domain ──────────────────────────────────────────────────

    [Fact]
    public async Task Domain_SearchFindsUsersAtDomain_KeyedOnToken()
    {
        var prefix = $"dom{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        await store.CreateAsync(User("u1", "ada@acme.test"));
        await store.CreateAsync(User("u2", "bob@acme.test"));
        await store.CreateAsync(User("u3", "eve@other.test"));

        var atAcme = await store.SearchByEmailDomainAsync("acme.test");
        Assert.Equal(new[] { "u1", "u2" }, atAcme.Select(u => u.Id).OrderBy(x => x).ToArray());

        // Keyed on the token (never the plaintext domain), bucketed by userId.
        Assert.True(await RowExists<UserEmailDomainEntity>($"{prefix}EmailDomains", $"{FakeTokenizer.Token("ACME.TEST")}-{Bucket("u1")}", "u1"));
        Assert.False(await RowExists<UserEmailDomainEntity>($"{prefix}EmailDomains", FakeTokenizer.Token("ACME.TEST"), "u1")); // not unbucketed
        Assert.False(await RowExists<UserEmailDomainEntity>($"{prefix}EmailDomains", "ACME.TEST", "u1")); // not plaintext
    }

    [Fact]
    public async Task Domain_MembersSpreadAcrossBuckets()
    {
        var prefix = $"dom{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        // Enough same-domain users that they can't all land in one bucket.
        for (var i = 0; i < 40; i++) await store.CreateAsync(User($"u{i}", $"user{i}@acme.test"));

        var found = await store.SearchByEmailDomainAsync("acme.test", maxResults: 100);
        Assert.Equal(40, found.Count); // fan-out over buckets finds every member

        var distinctPartitions = Enumerable.Range(0, 40)
            .Select(i => Bucket($"u{i}")).Distinct().Count();
        Assert.True(distinctPartitions > 1, "domain members should spread across multiple bucket partitions");
    }

    [Fact]
    public async Task Domain_Off_UsesPlaintextKey_AndSearchWorks()
    {
        var prefix = $"dom{Guid.NewGuid():N}";
        var store = NewStore(prefix, tokenizer: null);
        await store.CreateAsync(User("u1", "ada@acme.test"));

        Assert.True(await RowExists<UserEmailDomainEntity>($"{prefix}EmailDomains", $"ACME.TEST-{Bucket("u1")}", "u1")); // plaintext key, bucketed
        Assert.Equal("u1", (await store.SearchByEmailDomainAsync("acme.test")).Single().Id);
    }

    [Fact]
    public async Task Domain_EmailChange_MovesUserToNewDomain()
    {
        var prefix = $"dom{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        await store.CreateAsync(User("u1", "ada@acme.test"));

        await store.UpdateAsync(User("u1", "ada@other.test"));

        Assert.Empty(await store.SearchByEmailDomainAsync("acme.test"));
        Assert.Equal("u1", (await store.SearchByEmailDomainAsync("other.test")).Single().Id);
    }

    // ── email local-part prefix ─────────────────────────────────

    [Fact]
    public async Task EmailLocalPrefix_Tokenized_SearchByLocalPartPrefix()
    {
        var prefix = $"elp{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        await store.CreateAsync(User("u1", "alistair@acme.test")); // no name set — isolates the email path
        await store.CreateAsync(User("u2", "bob@acme.test"));

        var hits = await store.SearchAsync("ali");
        Assert.Equal(new[] { "u1" }, hits.Select(u => u.Id).ToArray());

        // Indexed as HMAC(prefix) -> userId, never the plaintext local part.
        Assert.True(await RowExists<UserEmailLocalPrefixEntity>($"{prefix}EmailLocalPrefixes", FakeTokenizer.Token("ALI"), "u1"));
        Assert.False(await RowExists<UserEmailLocalPrefixEntity>($"{prefix}EmailLocalPrefixes", "ALI", "u1"));
    }

    [Fact]
    public async Task EmailLocalPrefix_EmailChange_MovesPrefixes()
    {
        var prefix = $"elp{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        await store.CreateAsync(User("u1", "alistair@acme.test"));

        await store.UpdateAsync(User("u1", "wendy@acme.test"));

        Assert.Empty(await store.SearchAsync("ali"));
        Assert.Equal(new[] { "u1" }, (await store.SearchAsync("wen")).Select(u => u.Id).ToArray());
    }

    [Fact]
    public async Task EmailLocalPrefix_Off_UsesRangeScan_NoPrefixRows()
    {
        var prefix = $"elp{Guid.NewGuid():N}";
        var store = NewStore(prefix, tokenizer: null);
        await store.CreateAsync(User("u1", "alistair@acme.test"));

        Assert.Equal(new[] { "u1" }, (await store.SearchAsync("ali")).Select(u => u.Id).ToArray()); // range scan
        Assert.False(await RowExists<UserEmailLocalPrefixEntity>($"{prefix}EmailLocalPrefixes", "ALI", "u1")); // not written when off
    }

    [Fact]
    public async Task Domain_Delete_RemovesFromIndex()
    {
        var prefix = $"dom{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        await store.CreateAsync(User("u1", "ada@acme.test"));
        await store.DeleteAsync("u1");

        Assert.Empty(await store.SearchByEmailDomainAsync("acme.test"));
    }
}

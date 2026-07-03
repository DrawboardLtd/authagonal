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
            EnvPartitioner.Live, indexTokenizer: tokenizer, userEmailDomainsTable: T("EmailDomains"));
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

        // Keyed on the token, not the plaintext domain.
        Assert.True(await RowExists<UserEmailDomainEntity>($"{prefix}EmailDomains", FakeTokenizer.Token("ACME.TEST"), "u1"));
        Assert.False(await RowExists<UserEmailDomainEntity>($"{prefix}EmailDomains", "ACME.TEST", "u1"));
    }

    [Fact]
    public async Task Domain_Off_UsesPlaintextKey_AndSearchWorks()
    {
        var prefix = $"dom{Guid.NewGuid():N}";
        var store = NewStore(prefix, tokenizer: null);
        await store.CreateAsync(User("u1", "ada@acme.test"));

        Assert.True(await RowExists<UserEmailDomainEntity>($"{prefix}EmailDomains", "ACME.TEST", "u1")); // plaintext key
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

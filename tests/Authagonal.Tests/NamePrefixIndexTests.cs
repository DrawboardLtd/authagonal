using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// Increment 4 of searchable PII encryption: the name prefix index. A keyed HMAC destroys ordering, so
/// "starts with p" can't be a range scan once names are tokenized — each prefix of a name is indexed as
/// its own token row and prefix search becomes an exact-match token lookup. Off, the legacy 2-char
/// partition + range scan is used unchanged. Both coexist during migration (search reads both). Azurite.
/// </summary>
[Collection("Azurite")]
public class NamePrefixIndexTests(AzuriteFixture azurite)
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
        return new TableUserStore(T("Users"), T("Emails"), T("Logins"), T("ExtIds"), T("FirstNames"), T("LastNames"),
            EnvPartitioner.Live, indexTokenizer: tokenizer);
    }

    private static AuthUser User(string id, string email, string? first = null, string? last = null) => new()
    {
        Id = id,
        Email = email.ToLowerInvariant(),
        NormalizedEmail = email.ToUpperInvariant(),
        FirstName = first,
        LastName = last,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<bool> RowExists(string table, string pk, string rk)
    {
        try { await _svc.GetTableClient(table).GetEntityAsync<TableEntity>(pk, rk); return true; }
        catch (RequestFailedException ex) when (ex.Status == 404) { return false; }
    }

    private static string[] Ids(IReadOnlyList<AuthUser> users) => users.Select(u => u.Id).OrderBy(x => x).ToArray();

    [Fact]
    public async Task Tokenized_PrefixSearch_FindsByLastNamePrefix()
    {
        var prefix = $"name{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        await store.CreateAsync(User("u1", "a@x.test", last: "Smith"));
        await store.CreateAsync(User("u2", "b@x.test", last: "Smiley"));
        await store.CreateAsync(User("u3", "c@x.test", last: "Jones"));

        var hits = await store.SearchAsync("smi");
        Assert.Equal(new[] { "u1", "u2" }, Ids(hits));
    }

    [Fact]
    public async Task Tokenized_KeysOnPrefixTokens_NotLegacyRow()
    {
        var prefix = $"name{Guid.NewGuid():N}";
        await NewStore(prefix, new FakeTokenizer()).CreateAsync(User("u1", "a@x.test", first: "Smith"));

        // Prefix-token rows for "SM" and the full "SMITH", keyed on the token, RowKey = userId.
        Assert.True(await RowExists($"{prefix}FirstNames", FakeTokenizer.Token("SM"), "u1"));
        Assert.True(await RowExists($"{prefix}FirstNames", FakeTokenizer.Token("SMITH"), "u1"));
        // No legacy row (2-char PK + "{name}|{userId}" RowKey).
        Assert.False(await RowExists($"{prefix}FirstNames", "SM", "SMITH|u1"));
    }

    [Fact]
    public async Task Off_LegacyRangeScan_StillWorks()
    {
        // Regression for the untouched legacy path.
        var prefix = $"name{Guid.NewGuid():N}";
        var store = NewStore(prefix, tokenizer: null);
        await store.CreateAsync(User("u1", "a@x.test", last: "Smith"));
        await store.CreateAsync(User("u2", "b@x.test", last: "Smiley"));

        Assert.Equal(new[] { "u1", "u2" }, Ids(await store.SearchAsync("smi")));
        Assert.True(await RowExists($"{prefix}LastNames", "SM", "SMITH|u1")); // legacy scheme at rest
    }

    [Fact]
    public async Task Tokenized_NameChange_MovesPrefixTokens()
    {
        var prefix = $"name{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        await store.CreateAsync(User("u1", "a@x.test", first: "Smith"));

        await store.UpdateAsync(User("u1", "a@x.test", first: "Jones"));

        Assert.Empty(await store.SearchAsync("smi"));
        Assert.Equal(new[] { "u1" }, Ids(await store.SearchAsync("jon")));
    }

    [Fact]
    public async Task Tokenized_FindsLegacyRows_DuringMigration()
    {
        var prefix = $"name{Guid.NewGuid():N}";
        await NewStore(prefix, tokenizer: null).CreateAsync(User("u1", "a@x.test", last: "Smith")); // legacy rows

        var hits = await NewStore(prefix, new FakeTokenizer()).SearchAsync("smi"); // dual-read
        Assert.Equal(new[] { "u1" }, Ids(hits));
    }

    [Fact]
    public async Task Tokenized_ShortQuery_BelowMin_NoNameHit()
    {
        var prefix = $"name{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        await store.CreateAsync(User("u1", "a@x.test", last: "Smith"));

        Assert.Empty(await store.SearchAsync("s")); // 1 char < NamePrefixMin (2)
    }
}

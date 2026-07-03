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
/// Increment 3 of searchable PII encryption: the email blind index. With an <see cref="IIndexTokenizer"/>
/// wired, the email lookup row is keyed on a deterministic token (not the plaintext address), so a table
/// dump exposes no emails — yet exact login lookup still works. A migration-window dual-read keeps
/// not-yet-backfilled plaintext rows findable. Verified against real Azure Table semantics (Azurite).
/// </summary>
[Collection("Azurite")]
public class EmailBlindIndexTests(AzuriteFixture azurite)
{
    /// <summary>Deterministic, table-key-safe fake tokenizer (SHA-256 hex) — stands in for the Vault HMAC.</summary>
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
            EnvPartitioner.Live, indexTokenizer: tokenizer);
    }

    private async Task<bool> EmailRowExists(string prefix, string partitionKey)
    {
        try
        {
            await _svc.GetTableClient($"{prefix}Emails")
                .GetEntityAsync<UserEmailEntity>(partitionKey, UserEmailEntity.LookupRowKey);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return false; }
    }

    private static AuthUser User(string id, string email) => new()
    {
        Id = id,
        Email = email.ToLowerInvariant(),
        NormalizedEmail = email.ToUpperInvariant(),
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Create_KeysEmailLookupOnToken_NotPlaintext()
    {
        var prefix = $"blindemail{Guid.NewGuid():N}";
        await NewStore(prefix, new FakeTokenizer()).CreateAsync(User("u1", "ada@acme.test"));

        Assert.True(await EmailRowExists(prefix, FakeTokenizer.Token("ADA@ACME.TEST")));  // tokenized key present
        Assert.False(await EmailRowExists(prefix, "ADA@ACME.TEST"));                       // plaintext key absent
    }

    [Fact]
    public async Task FindByEmail_ResolvesViaToken()
    {
        var prefix = $"blindemail{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        await store.CreateAsync(User("u1", "ada@acme.test"));

        var found = await store.FindByEmailAsync("ada@acme.test");
        Assert.Equal("u1", found!.Id);
    }

    [Fact]
    public async Task LegacyPlaintextRow_StillFound_ViaDualRead()
    {
        // Written before tokenization was enabled → plaintext-keyed row.
        var prefix = $"blindemail{Guid.NewGuid():N}";
        await NewStore(prefix, tokenizer: null).CreateAsync(User("u1", "ada@acme.test"));
        Assert.True(await EmailRowExists(prefix, "ADA@ACME.TEST")); // confirm it's plaintext at rest

        // A tokenizer-enabled store still finds it (token miss → plaintext fallback).
        var found = await NewStore(prefix, new FakeTokenizer()).FindByEmailAsync("ada@acme.test");
        Assert.Equal("u1", found!.Id);
    }

    [Fact]
    public async Task Off_KeepsPlaintextKey_AndLookupWorks()
    {
        var prefix = $"blindemail{Guid.NewGuid():N}";
        var store = NewStore(prefix, tokenizer: null);
        await store.CreateAsync(User("u1", "ada@acme.test"));

        Assert.True(await EmailRowExists(prefix, "ADA@ACME.TEST"));   // plaintext key (current behavior)
        Assert.Equal("u1", (await store.FindByEmailAsync("ada@acme.test"))!.Id);
    }

    [Fact]
    public async Task EmailChange_MovesTokenRow()
    {
        var prefix = $"blindemail{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeTokenizer());
        await store.CreateAsync(User("u1", "ada@acme.test"));

        var updated = User("u1", "bob@acme.test");
        await store.UpdateAsync(updated);

        Assert.False(await EmailRowExists(prefix, FakeTokenizer.Token("ADA@ACME.TEST"))); // old token row gone
        Assert.True(await EmailRowExists(prefix, FakeTokenizer.Token("BOB@ACME.TEST")));  // new token row present
        Assert.Equal("u1", (await store.FindByEmailAsync("bob@acme.test"))!.Id);
        Assert.Null(await store.FindByEmailAsync("ada@acme.test"));
    }
}

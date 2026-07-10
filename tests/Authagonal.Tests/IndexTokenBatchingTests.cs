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
/// IO-conflation guards for <see cref="TableUserStore"/>: every profile write/delete computes ALL the
/// blind-index tokens it needs in ONE <see cref="IIndexTokenizer.TokenizeBatchAsync"/> round-trip (one
/// Vault HMAC call in Cloud) instead of one call per index, and <see cref="TableUserStore.AddLoginAsync"/>
/// encrypts each column once and shares the ciphertext across the forward/reverse rows. Counting fakes
/// pin the round-trip counts; Azurite pins that behavior (lookups, dual-read, legacy drops) is unchanged.
/// </summary>
[Collection("Azurite")]
public class IndexTokenBatchingTests(AzuriteFixture azurite)
{
    private sealed class CountingTokenizer : IIndexTokenizer
    {
        private int _single, _batch;
        public int SingleCalls => _single;
        public int BatchCalls => _batch;

        public const string Prefix = "tok_";
        public static string Token(string v) => Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v)));

        public Task<string> TokenizeAsync(string value, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _single);
            return Task.FromResult(Token(value));
        }

        public Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _batch);
            return Task.FromResult<IReadOnlyList<string>>(values.Select(Token).ToList());
        }
    }

    /// <summary>
    /// Reversible fake whose ciphertext is NONCED ("enc:{n}:{b64}") — two Protect calls over the same
    /// plaintext yield different tokens, so "both rows carry the same ciphertext" can only pass if the
    /// value was encrypted once and shared (what AddLoginAsync now does).
    /// </summary>
    private sealed class CountingCipher : IFieldCipher
    {
        private int _single, _batch, _nonce;
        public int SingleProtectCalls => _single;
        public int BatchProtectCalls => _batch;

        public const string Prefix = "enc:";

        private string Encrypt(string p) => $"{Prefix}{Interlocked.Increment(ref _nonce)}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(p))}";

        public Task<string> ProtectAsync(string plaintext, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _single);
            return Task.FromResult(Encrypt(plaintext));
        }

        public Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _batch);
            return Task.FromResult<IReadOnlyList<string>>(plaintexts.Select(Encrypt).ToList());
        }

        public Task<string> ResolveAsync(string stored, CancellationToken ct = default)
            => Task.FromResult(stored.StartsWith(Prefix, StringComparison.Ordinal)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(stored.Split(':')[2]))
                : stored);
    }

    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableUserStore NewStore(string prefix, CountingCipher cipher, CountingTokenizer tokenizer)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        return new TableUserStore(T("Users"), T("Emails"), T("Logins"), T("ExtIds"), T("FirstNames"), T("LastNames"),
            EnvPartitioner.Live, fieldCipher: cipher, indexTokenizer: tokenizer,
            userEmailDomainsTable: T("Domains"), userEmailLocalPrefixesTable: T("LocalPrefixes"));
    }

    private static AuthUser User(string id, string email, string first = "Ada", string last = "Lovelace") => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        FirstName = first,
        LastName = last,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<bool> RowExists<T>(string table, string pk, string rk) where T : class, ITableEntity
    {
        try { await _svc.GetTableClient(table).GetEntityAsync<T>(pk, rk); return true; }
        catch (RequestFailedException ex) when (ex.Status == 404) { return false; }
    }

    [Fact]
    public async Task Create_ComputesAllIndexTokens_InOneBatch()
    {
        var prefix = $"iob{Guid.NewGuid():N}";
        var tok = new CountingTokenizer();
        var cipher = new CountingCipher();
        var store = NewStore(prefix, cipher, tok);

        await store.CreateAsync(User("u1", "ada@acme.test"));

        // One tokenize batch covers email + domain + local-part prefixes + both name prefix sets;
        // one encrypt batch covers the profile PII fields. No per-index singles.
        Assert.Equal(1, tok.BatchCalls);
        Assert.Equal(0, tok.SingleCalls);
        Assert.Equal(1, cipher.BatchProtectCalls);
        Assert.Equal(0, cipher.SingleProtectCalls);

        // Every index the batch fed is live.
        Assert.True(await RowExists<UserEmailEntity>($"{prefix}Emails", CountingTokenizer.Token("ADA@ACME.TEST"), UserEmailEntity.LookupRowKey));
        Assert.True(await RowExists<TableEntity>($"{prefix}FirstNames", CountingTokenizer.Token("ADA"), "u1"));
        Assert.True(await RowExists<TableEntity>($"{prefix}LastNames", CountingTokenizer.Token("LOVELACE"), "u1"));
        Assert.True(await RowExists<TableEntity>($"{prefix}LocalPrefixes", CountingTokenizer.Token("ADA"), "u1"));
        var byEmail = await store.FindByEmailAsync("ada@acme.test");
        Assert.Equal("u1", byEmail!.Id);
        Assert.Equal(new[] { "u1" }, (await store.SearchByEmailDomainAsync("acme.test")).Select(u => u.Id).ToArray());
    }

    [Fact]
    public async Task Update_EmailAndBothNamesChanged_OneBatch_IndexesMove()
    {
        var prefix = $"iob{Guid.NewGuid():N}";
        var tok = new CountingTokenizer();
        var store = NewStore(prefix, new CountingCipher(), tok);
        await store.CreateAsync(User("u1", "ada@acme.test"));

        var b0 = tok.BatchCalls;
        var s0 = tok.SingleCalls;
        await store.UpdateAsync(User("u1", "grace@hopper.test", first: "Grace", last: "Hopper"));

        // New-side + old-side tokens (email, local prefixes, domains, both name prefix sets) in one batch.
        Assert.Equal(1, tok.BatchCalls - b0);
        Assert.Equal(0, tok.SingleCalls - s0);

        // Old index rows gone, new ones live.
        Assert.False(await RowExists<UserEmailEntity>($"{prefix}Emails", CountingTokenizer.Token("ADA@ACME.TEST"), UserEmailEntity.LookupRowKey));
        Assert.True(await RowExists<UserEmailEntity>($"{prefix}Emails", CountingTokenizer.Token("GRACE@HOPPER.TEST"), UserEmailEntity.LookupRowKey));
        Assert.False(await RowExists<TableEntity>($"{prefix}FirstNames", CountingTokenizer.Token("ADA"), "u1"));
        Assert.True(await RowExists<TableEntity>($"{prefix}FirstNames", CountingTokenizer.Token("GRACE"), "u1"));
        Assert.False(await RowExists<TableEntity>($"{prefix}LocalPrefixes", CountingTokenizer.Token("ADA"), "u1"));
        Assert.True(await RowExists<TableEntity>($"{prefix}LocalPrefixes", CountingTokenizer.Token("GRACE"), "u1"));
        Assert.Null(await store.FindByEmailAsync("ada@acme.test"));
        Assert.Equal("u1", (await store.FindByEmailAsync("grace@hopper.test"))!.Id);
        Assert.Empty(await store.SearchByEmailDomainAsync("acme.test"));
        Assert.Equal(new[] { "u1" }, (await store.SearchByEmailDomainAsync("hopper.test")).Select(u => u.Id).ToArray());
    }

    [Fact]
    public async Task Delete_ComputesAllIndexTokens_InOneBatch()
    {
        var prefix = $"iob{Guid.NewGuid():N}";
        var tok = new CountingTokenizer();
        var store = NewStore(prefix, new CountingCipher(), tok);
        await store.CreateAsync(User("u1", "ada@acme.test"));

        var b0 = tok.BatchCalls;
        var s0 = tok.SingleCalls;
        await store.DeleteAsync("u1");

        Assert.Equal(1, tok.BatchCalls - b0);
        Assert.Equal(0, tok.SingleCalls - s0);

        Assert.Null(await store.GetAsync("u1"));
        Assert.Null(await store.FindByEmailAsync("ada@acme.test"));
        Assert.False(await RowExists<UserEmailEntity>($"{prefix}Emails", CountingTokenizer.Token("ADA@ACME.TEST"), UserEmailEntity.LookupRowKey));
        Assert.False(await RowExists<TableEntity>($"{prefix}FirstNames", CountingTokenizer.Token("ADA"), "u1"));
        Assert.False(await RowExists<TableEntity>($"{prefix}LastNames", CountingTokenizer.Token("LOVELACE"), "u1"));
        Assert.False(await RowExists<TableEntity>($"{prefix}LocalPrefixes", CountingTokenizer.Token("ADA"), "u1"));
        Assert.Empty(await store.SearchByEmailDomainAsync("acme.test"));
    }

    [Fact]
    public async Task Delete_RemovesAllLogins_Concurrently()
    {
        var prefix = $"iob{Guid.NewGuid():N}";
        var store = NewStore(prefix, new CountingCipher(), new CountingTokenizer());
        await store.CreateAsync(User("u1", "ada@acme.test"));
        await store.AddLoginAsync(new ExternalLoginInfo { UserId = "u1", Provider = "saml", ProviderKey = "ada@idp.test", DisplayName = "Ada" });
        await store.AddLoginAsync(new ExternalLoginInfo { UserId = "u1", Provider = "oidc", ProviderKey = "sub-123" });
        await store.AddLoginAsync(new ExternalLoginInfo { UserId = "u1", Provider = "github", ProviderKey = "gh-9" });

        await store.DeleteAsync("u1");

        Assert.Empty(await store.GetLoginsAsync("u1"));
        Assert.Null(await store.FindLoginAsync("saml", "ada@idp.test"));
        Assert.Null(await store.FindLoginAsync("oidc", "sub-123"));
        Assert.Null(await store.FindLoginAsync("github", "gh-9"));
    }

    [Fact]
    public async Task Reindex_ComputesAllIndexTokens_InOneBatch_NoRedundantDomainToken()
    {
        var prefix = $"iob{Guid.NewGuid():N}";
        var tok = new CountingTokenizer();
        var store = NewStore(prefix, new CountingCipher(), tok);
        await store.CreateAsync(User("u1", "ada@acme.test"));

        var b0 = tok.BatchCalls;
        var s0 = tok.SingleCalls;
        await store.ReindexUserAsync("u1");

        // The dropLegacy domain comparison reuses the batch token instead of re-HMACing the domain.
        Assert.Equal(1, tok.BatchCalls - b0);
        Assert.Equal(0, tok.SingleCalls - s0);

        Assert.Equal("u1", (await store.FindByEmailAsync("ada@acme.test"))!.Id);
        Assert.Equal(new[] { "u1" }, (await store.SearchByEmailDomainAsync("acme.test")).Select(u => u.Id).ToArray());
    }

    [Fact]
    public async Task Search_OneExactTokenize_OneBatchForPrefixLookups()
    {
        var prefix = $"iob{Guid.NewGuid():N}";
        var tok = new CountingTokenizer();
        var store = NewStore(prefix, new CountingCipher(), tok);
        await store.CreateAsync(User("u1", "ada@acme.test"));
        await store.CreateAsync(User("u2", "adam@other.test", first: "Adam", last: "Smith"));

        var b0 = tok.BatchCalls;
        var s0 = tok.SingleCalls;
        var hits = await store.SearchAsync("ada");

        // 1 single = the exact-email lookup (FindByEmailAsync); 1 batch = the email-local-part + name
        // prefix tokens together (previously three separate tokenize calls).
        Assert.Equal(1, tok.SingleCalls - s0);
        Assert.Equal(1, tok.BatchCalls - b0);
        Assert.Equal(new[] { "u1", "u2" }, hits.Select(u => u.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task AddLogin_EncryptsOnce_SharesCiphertextAcrossForwardAndReverse()
    {
        var prefix = $"iob{Guid.NewGuid():N}";
        var tok = new CountingTokenizer();
        var cipher = new CountingCipher();
        var store = NewStore(prefix, cipher, tok);
        await store.CreateAsync(User("u1", "ada@acme.test"));

        var b0 = cipher.BatchProtectCalls;
        var s0 = cipher.SingleProtectCalls;
        var t0 = tok.SingleCalls;
        await store.AddLoginAsync(new ExternalLoginInfo { UserId = "u1", Provider = "saml", ProviderKey = "ada@idp.test", DisplayName = "Ada L" });

        // One HMAC for the composite key, one encrypt batch for ProviderKey + DisplayName — reused verbatim
        // on both rows (the nonced fake ciphertext would differ if either value were encrypted twice).
        Assert.Equal(1, tok.SingleCalls - t0);
        Assert.Equal(1, cipher.BatchProtectCalls - b0);
        Assert.Equal(0, cipher.SingleProtectCalls - s0);

        var token = CountingTokenizer.Token("saml|ada@idp.test");
        var forward = (await _svc.GetTableClient($"{prefix}Logins").GetEntityAsync<UserLoginEntity>(token, UserLoginEntity.LookupRowKey)).Value;
        var reverse = (await _svc.GetTableClient($"{prefix}Logins").GetEntityAsync<UserLoginEntity>("u1", $"{UserLoginEntity.LoginRowKeyPrefix}{token}")).Value;
        Assert.StartsWith(CountingCipher.Prefix, forward.ProviderKey);
        Assert.StartsWith(CountingCipher.Prefix, forward.DisplayName);
        Assert.Equal(forward.ProviderKey, reverse.ProviderKey);
        Assert.Equal(forward.DisplayName, reverse.DisplayName);

        // Both directions still round-trip to plaintext.
        var found = await store.FindLoginAsync("saml", "ada@idp.test");
        Assert.Equal("u1", found!.UserId);
        Assert.Equal("ada@idp.test", found.ProviderKey);
        Assert.Equal("Ada L", found.DisplayName);
        var listed = Assert.Single(await store.GetLoginsAsync("u1"));
        Assert.Equal("ada@idp.test", listed.ProviderKey);
        Assert.Equal("Ada L", listed.DisplayName);
    }
}

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
/// The cold-row backfill primitive: <see cref="TableUserStore.ReindexUserAsync"/> migrates a user written
/// before encryption was enabled — plaintext profile + plaintext-keyed indexes — to the current scheme
/// (encrypted profile + tokenized index rows, legacy rows removed), while login lookup and name search keep
/// working throughout. Azurite.
/// </summary>
[Collection("Azurite")]
public class ReindexBackfillTests(AzuriteFixture azurite)
{
    private sealed class FakeCipher : IFieldCipher
    {
        public const string Prefix = "enc:";
        public Task<string> ProtectAsync(string p, CancellationToken ct = default)
            => Task.FromResult(Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(p)));
        public Task<string> ResolveAsync(string s, CancellationToken ct = default)
            => Task.FromResult(s.StartsWith(Prefix, StringComparison.Ordinal)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(s[Prefix.Length..])) : s);
    }

    private sealed class FakeTokenizer : IIndexTokenizer
    {
        public const string Prefix = "tok_";
        public static string Token(string v) => Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v)));
        public Task<string> TokenizeAsync(string value, CancellationToken ct = default) => Task.FromResult(Token(value));
        public Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(values.Select(Token).ToList());
    }

    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableUserStore NewStore(string prefix, IFieldCipher? cipher, IIndexTokenizer? tokenizer)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        return new TableUserStore(T("Users"), T("Emails"), T("Logins"), T("ExtIds"), T("FirstNames"), T("LastNames"),
            EnvPartitioner.Live, fieldCipher: cipher, indexTokenizer: tokenizer, userEmailDomainsTable: T("EmailDomains"));
    }

    private async Task<bool> RowExists<T>(string table, string pk, string rk) where T : class, ITableEntity
    {
        try { await _svc.GetTableClient(table).GetEntityAsync<T>(pk, rk); return true; }
        catch (RequestFailedException ex) when (ex.Status == 404) { return false; }
    }

    [Fact]
    public async Task Reindex_MigratesPlaintextUser_ToEncryptedAndTokenized()
    {
        var prefix = $"backfill{Guid.NewGuid():N}";
        // Legacy state: created before encryption — plaintext profile + plaintext-keyed indexes.
        var plain = NewStore(prefix, cipher: null, tokenizer: null);
        await plain.CreateAsync(new AuthUser
        {
            Id = "u1",
            Email = "ada@acme.test",
            NormalizedEmail = "ADA@ACME.TEST",
            FirstName = "Ada",
            LastName = "Lovelace",
            Phone = "+15551234567",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // Turn encryption on and backfill this cold row.
        var enc = NewStore(prefix, new FakeCipher(), new FakeTokenizer());
        await enc.ReindexUserAsync("u1");

        // Profile PII is now ciphertext.
        var raw = await _svc.GetTableClient($"{prefix}Users").GetEntityAsync<UserEntity>("u1", UserEntity.ProfileRowKey);
        Assert.StartsWith(FakeCipher.Prefix, raw.Value.Email);
        Assert.StartsWith(FakeCipher.Prefix, raw.Value.LastName);
        Assert.StartsWith(FakeCipher.Prefix, raw.Value.Phone);

        // Email index migrated to the token key; legacy plaintext row removed.
        Assert.True(await RowExists<UserEmailEntity>($"{prefix}Emails", FakeTokenizer.Token("ADA@ACME.TEST"), UserEmailEntity.LookupRowKey));
        Assert.False(await RowExists<UserEmailEntity>($"{prefix}Emails", "ADA@ACME.TEST", UserEmailEntity.LookupRowKey));

        // Name index migrated to prefix tokens; legacy range-scan row removed.
        Assert.True(await RowExists<TableEntity>($"{prefix}LastNames", FakeTokenizer.Token("LOV"), "u1"));
        Assert.False(await RowExists<TableEntity>($"{prefix}LastNames", "LO", "LOVELACE|u1"));

        // Lookup + search + round-trip all still work through the encrypted store.
        var byEmail = await enc.FindByEmailAsync("ada@acme.test");
        Assert.Equal("u1", byEmail!.Id);
        Assert.Equal("ada@acme.test", byEmail.Email);       // decrypts back
        Assert.Equal("Lovelace", byEmail.LastName);
        Assert.Equal(new[] { "u1" }, (await enc.SearchAsync("lov")).Select(u => u.Id).ToArray());
        Assert.Equal(new[] { "u1" }, (await enc.SearchByEmailDomainAsync("acme.test")).Select(u => u.Id).ToArray());
    }

    [Fact]
    public async Task Reindex_IsIdempotent()
    {
        var prefix = $"backfill{Guid.NewGuid():N}";
        var enc = NewStore(prefix, new FakeCipher(), new FakeTokenizer());
        await enc.CreateAsync(new AuthUser
        {
            Id = "u1", Email = "ada@acme.test", NormalizedEmail = "ADA@ACME.TEST",
            FirstName = "Ada", LastName = "Lovelace", IsActive = true, CreatedAt = DateTimeOffset.UtcNow,
        });

        // Running the backfill over an already-encrypted user must not corrupt it.
        await enc.ReindexUserAsync("u1");
        await enc.ReindexUserAsync("u1");

        var got = await enc.FindByEmailAsync("ada@acme.test");
        Assert.Equal("u1", got!.Id);
        Assert.Equal("ada@acme.test", got.Email);
        Assert.Equal("Lovelace", got.LastName);
    }

    [Fact]
    public async Task MigrateExternalIdIndex_ReKeysLegacyPlaintextRow_AndKeepsLookupLive()
    {
        var prefix = $"extid{Guid.NewGuid():N}";

        // Legacy state: externalId written before tokenization → plaintext "{clientId}|{externalId}" PK.
        var plain = NewStore(prefix, cipher: null, tokenizer: null);
        await plain.CreateAsync(new AuthUser
        {
            Id = "u1", Email = "e@x.test", NormalizedEmail = "E@X.TEST", IsActive = true, CreatedAt = DateTimeOffset.UtcNow,
        });
        await plain.SetExternalIdAsync("u1", "clientA", "ext-123");
        Assert.True(await RowExists<UserExternalIdEntity>($"{prefix}ExtIds", "clientA|ext-123", UserExternalIdEntity.LookupRowKey));

        // Turn tokenization on and migrate the cold index row.
        var enc = NewStore(prefix, new FakeCipher(), new FakeTokenizer());
        Assert.Equal(1, await enc.MigrateExternalIdIndexAsync(dryRun: false));

        // Row now lives at the token PK; the legacy plaintext row is gone.
        Assert.True(await RowExists<UserExternalIdEntity>($"{prefix}ExtIds", FakeTokenizer.Token("clientA|ext-123"), UserExternalIdEntity.LookupRowKey));
        Assert.False(await RowExists<UserExternalIdEntity>($"{prefix}ExtIds", "clientA|ext-123", UserExternalIdEntity.LookupRowKey));

        // Lookup still resolves through the encrypted store.
        Assert.Equal("u1", (await enc.FindByExternalIdAsync("clientA", "ext-123"))!.Id);
    }

    [Fact]
    public async Task MigrateExternalIdIndex_DryRun_CountsWithoutWriting()
    {
        var prefix = $"extid{Guid.NewGuid():N}";
        var plain = NewStore(prefix, cipher: null, tokenizer: null);
        await plain.CreateAsync(new AuthUser
        {
            Id = "u1", Email = "e@x.test", NormalizedEmail = "E@X.TEST", IsActive = true, CreatedAt = DateTimeOffset.UtcNow,
        });
        await plain.SetExternalIdAsync("u1", "clientA", "ext-123");

        var enc = NewStore(prefix, new FakeCipher(), new FakeTokenizer());
        Assert.Equal(1, await enc.MigrateExternalIdIndexAsync(dryRun: true));

        // Nothing moved — the legacy row is untouched and no token row was written.
        Assert.True(await RowExists<UserExternalIdEntity>($"{prefix}ExtIds", "clientA|ext-123", UserExternalIdEntity.LookupRowKey));
        Assert.False(await RowExists<UserExternalIdEntity>($"{prefix}ExtIds", FakeTokenizer.Token("clientA|ext-123"), UserExternalIdEntity.LookupRowKey));
    }

    [Fact]
    public async Task MigrateExternalIdIndex_IsIdempotent_AndSkipsAlreadyTokenizedRows()
    {
        var prefix = $"extid{Guid.NewGuid():N}";
        var enc = NewStore(prefix, new FakeCipher(), new FakeTokenizer());
        await enc.CreateAsync(new AuthUser
        {
            Id = "u1", Email = "e@x.test", NormalizedEmail = "E@X.TEST", IsActive = true, CreatedAt = DateTimeOffset.UtcNow,
        });
        await enc.SetExternalIdAsync("u1", "clientA", "ext-123"); // fresh write → already a token row

        // No legacy rows to move, and a re-run stays 0 — the lookup keeps working.
        Assert.Equal(0, await enc.MigrateExternalIdIndexAsync(dryRun: false));
        Assert.Equal(0, await enc.MigrateExternalIdIndexAsync(dryRun: false));
        Assert.Equal("u1", (await enc.FindByExternalIdAsync("clientA", "ext-123"))!.Id);
    }

    private static ExternalLoginInfo Login(string userId = "u1", string provider = "saml",
        string providerKey = "nameid@corp.test", string? displayName = "Ada Lovelace")
        => new() { UserId = userId, Provider = provider, ProviderKey = providerKey, DisplayName = displayName };

    [Fact]
    public async Task UserLogins_FreshWrite_TokenizesKeys_EncryptsColumns_RoundTrips()
    {
        var prefix = $"logins{Guid.NewGuid():N}";
        var enc = NewStore(prefix, new FakeCipher(), new FakeTokenizer());
        await enc.AddLoginAsync(Login());

        var token = FakeTokenizer.Token("saml|nameid@corp.test");
        // Forward row at the token PK, reverse row at "login|{token}"; no plaintext-keyed rows.
        Assert.True(await RowExists<UserLoginEntity>($"{prefix}Logins", token, UserLoginEntity.LookupRowKey));
        Assert.True(await RowExists<UserLoginEntity>($"{prefix}Logins", "u1", $"{UserLoginEntity.LoginRowKeyPrefix}{token}"));
        Assert.False(await RowExists<UserLoginEntity>($"{prefix}Logins", "saml|nameid@corp.test", UserLoginEntity.LookupRowKey));

        // Recoverable columns encrypted at rest; Provider stays plaintext.
        var raw = await _svc.GetTableClient($"{prefix}Logins").GetEntityAsync<UserLoginEntity>(token, UserLoginEntity.LookupRowKey);
        Assert.StartsWith(FakeCipher.Prefix, raw.Value.ProviderKey);
        Assert.StartsWith(FakeCipher.Prefix, raw.Value.DisplayName!);
        Assert.Equal("saml", raw.Value.Provider);

        // Lookups decrypt back to plaintext.
        var found = await enc.FindLoginAsync("saml", "nameid@corp.test");
        Assert.Equal("u1", found!.UserId);
        Assert.Equal("nameid@corp.test", found.ProviderKey);
        Assert.Equal("Ada Lovelace", found.DisplayName);
        Assert.Equal(new[] { "nameid@corp.test" }, (await enc.GetLoginsAsync("u1")).Select(l => l.ProviderKey).ToArray());
    }

    [Fact]
    public async Task MigrateUserLogins_ReKeysAndEncrypts_LegacyRows()
    {
        var prefix = $"logins{Guid.NewGuid():N}";
        // Legacy: written before tokenization/encryption → plaintext keys + columns.
        var plain = NewStore(prefix, cipher: null, tokenizer: null);
        await plain.AddLoginAsync(Login());
        Assert.True(await RowExists<UserLoginEntity>($"{prefix}Logins", "saml|nameid@corp.test", UserLoginEntity.LookupRowKey));

        var enc = NewStore(prefix, new FakeCipher(), new FakeTokenizer());
        Assert.Equal(2, await enc.MigrateUserLoginsAsync(dryRun: false)); // forward + reverse

        var token = FakeTokenizer.Token("saml|nameid@corp.test");
        Assert.True(await RowExists<UserLoginEntity>($"{prefix}Logins", token, UserLoginEntity.LookupRowKey));
        Assert.True(await RowExists<UserLoginEntity>($"{prefix}Logins", "u1", $"{UserLoginEntity.LoginRowKeyPrefix}{token}"));
        Assert.False(await RowExists<UserLoginEntity>($"{prefix}Logins", "saml|nameid@corp.test", UserLoginEntity.LookupRowKey));
        Assert.False(await RowExists<UserLoginEntity>($"{prefix}Logins", "u1", $"{UserLoginEntity.LoginRowKeyPrefix}saml|nameid@corp.test"));

        var raw = await _svc.GetTableClient($"{prefix}Logins").GetEntityAsync<UserLoginEntity>(token, UserLoginEntity.LookupRowKey);
        Assert.StartsWith(FakeCipher.Prefix, raw.Value.ProviderKey);

        // Lookups still resolve through the encrypted store.
        Assert.Equal("u1", (await enc.FindLoginAsync("saml", "nameid@corp.test"))!.UserId);
        Assert.Single(await enc.GetLoginsAsync("u1"));
    }

    [Fact]
    public async Task MigrateUserLogins_DryRun_CountsWithoutWriting()
    {
        var prefix = $"logins{Guid.NewGuid():N}";
        var plain = NewStore(prefix, cipher: null, tokenizer: null);
        await plain.AddLoginAsync(Login());

        var enc = NewStore(prefix, new FakeCipher(), new FakeTokenizer());
        Assert.Equal(2, await enc.MigrateUserLoginsAsync(dryRun: true));
        Assert.True(await RowExists<UserLoginEntity>($"{prefix}Logins", "saml|nameid@corp.test", UserLoginEntity.LookupRowKey));
        Assert.False(await RowExists<UserLoginEntity>($"{prefix}Logins", FakeTokenizer.Token("saml|nameid@corp.test"), UserLoginEntity.LookupRowKey));
    }

    [Fact]
    public async Task MigrateUserLogins_IsIdempotent()
    {
        var prefix = $"logins{Guid.NewGuid():N}";
        var enc = NewStore(prefix, new FakeCipher(), new FakeTokenizer());
        await enc.AddLoginAsync(Login());
        Assert.Equal(0, await enc.MigrateUserLoginsAsync(dryRun: false));
        Assert.Equal(0, await enc.MigrateUserLoginsAsync(dryRun: false));
        Assert.Equal("u1", (await enc.FindLoginAsync("saml", "nameid@corp.test"))!.UserId);
    }

    [Fact]
    public async Task RemoveLogin_UnderEncryption_RemovesBothRows()
    {
        var prefix = $"logins{Guid.NewGuid():N}";
        var enc = NewStore(prefix, new FakeCipher(), new FakeTokenizer());
        await enc.AddLoginAsync(Login());
        await enc.RemoveLoginAsync("u1", "saml", "nameid@corp.test");
        Assert.Null(await enc.FindLoginAsync("saml", "nameid@corp.test"));
        Assert.Empty(await enc.GetLoginsAsync("u1"));
    }

    private TableProvisioningAppStore NewProvStore(string prefix, IFieldCipher? cipher)
    {
        var c = _svc.GetTableClient($"{prefix}ProvApps");
        c.CreateIfNotExists();
        return new TableProvisioningAppStore(c, EnvPartitioner.Live, null, cipher);
    }

    [Fact]
    public async Task ProvisioningApp_ApiKey_EncryptedAtRest_DecryptsOnRead()
    {
        var prefix = $"prov{Guid.NewGuid():N}";
        var enc = NewProvStore(prefix, new FakeCipher());
        await enc.UpsertAsync(new ProvisioningAppConfig { AppId = "app1", Name = "App 1", CallbackUrl = "https://x.test", ApiKey = "secret-key-123" });

        // At rest: ciphertext. On read (Get + GetAll): plaintext.
        var raw = await _svc.GetTableClient($"{prefix}ProvApps").GetEntityAsync<ProvisioningAppEntity>(ProvisioningAppEntity.AppsPartition, "app1");
        Assert.StartsWith(FakeCipher.Prefix, raw.Value.ApiKey!);
        Assert.Equal("secret-key-123", (await enc.GetAsync("app1"))!.ApiKey);
        Assert.Equal("secret-key-123", (await enc.GetAllAsync()).Single().ApiKey);
    }

    [Fact]
    public async Task MigrateProvisioningApps_EncryptsLegacyPlaintext_Idempotent()
    {
        var prefix = $"prov{Guid.NewGuid():N}";
        // Legacy: written before encryption (Null cipher) → plaintext ApiKey at rest.
        var plain = NewProvStore(prefix, cipher: null);
        await plain.UpsertAsync(new ProvisioningAppConfig { AppId = "app1", Name = "App 1", CallbackUrl = "https://x.test", ApiKey = "legacy-key" });
        var before = await _svc.GetTableClient($"{prefix}ProvApps").GetEntityAsync<ProvisioningAppEntity>(ProvisioningAppEntity.AppsPartition, "app1");
        Assert.Equal("legacy-key", before.Value.ApiKey);

        var enc = NewProvStore(prefix, new FakeCipher());
        Assert.Equal(1, await enc.MigrateProvisioningAppsAsync(dryRun: true));   // counts, no write
        Assert.Equal(1, await enc.MigrateProvisioningAppsAsync(dryRun: false));  // migrates
        var after = await _svc.GetTableClient($"{prefix}ProvApps").GetEntityAsync<ProvisioningAppEntity>(ProvisioningAppEntity.AppsPartition, "app1");
        Assert.StartsWith(FakeCipher.Prefix, after.Value.ApiKey!);               // now ciphertext
        Assert.Equal("legacy-key", (await enc.GetAsync("app1"))!.ApiKey);        // still readable
        Assert.Equal(0, await enc.MigrateProvisioningAppsAsync(dryRun: false));  // already encrypted → skipped
    }
}

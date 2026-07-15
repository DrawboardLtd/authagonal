using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.AwsProvider.Stores;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The Dynamo store's at-rest crypto: the profile document encrypts via <see cref="IFieldCipher"/>,
/// lookup keys become blind-index tokens via <see cref="IIndexTokenizer"/>, legacy plaintext rows
/// keep resolving through the dual-read, and the reindex/migration surface moves them to the current
/// scheme — the Dynamo counterparts of PiiEncryptionTests / EmailBlindIndexTests / ReindexBackfillTests.
/// </summary>
[Collection("Dynamo")]
public class DynamoUserStoreCryptoTests(DynamoFixture dynamo)
{
    private sealed class FakeCipher : IFieldCipher
    {
        public const string Prefix = "enc:";
        public Task<string> ProtectAsync(string plaintext, CancellationToken ct = default)
            => Task.FromResult(Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext)));
        public Task<string> ResolveAsync(string stored, CancellationToken ct = default)
            => Task.FromResult(stored.StartsWith(Prefix, StringComparison.Ordinal)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(stored[Prefix.Length..]))
                : stored);
    }

    /// <summary>Deterministic keyed-HMAC stand-in: hex-encodes the value (never contains '|').</summary>
    private sealed class FakeTokenizer : IIndexTokenizer
    {
        public Task<string> TokenizeAsync(string value, CancellationToken ct = default)
            => Task.FromResult(Convert.ToHexString(Encoding.UTF8.GetBytes("tok:" + value)));
        public async Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values, CancellationToken ct = default)
        {
            var r = new string[values.Count];
            for (var i = 0; i < values.Count; i++) r[i] = await TokenizeAsync(values[i], ct);
            return r;
        }
    }

    private readonly IAmazonDynamoDB _db = dynamo.CreateClient();

    private async Task<(DynamoUserStore Plain, DynamoUserStore Crypto)> NewStoresAsync(string prefix)
    {
        async Task<DynamoTable> T(string name)
        {
            await DynamoTableProvisioner.EnsureTableAsync(_db, $"{prefix}{name}");
            return new DynamoTable(_db, $"{prefix}{name}");
        }
        var users = await T("Users");
        var emails = await T("Emails");
        var logins = await T("Logins");
        var extIds = await T("ExtIds");
        var firsts = await T("FirstNames");
        var lasts = await T("LastNames");
        var domains = await T("EmailDomains");
        var locals = await T("EmailLocalPrefixes");

        var plain = new DynamoUserStore(users, emails, logins, extIds, firsts, lasts, EnvPartitioner.Live,
            userEmailDomains: domains, userEmailLocalPrefixes: locals);
        var crypto = new DynamoUserStore(users, emails, logins, extIds, firsts, lasts, EnvPartitioner.Live,
            userEmailDomains: domains, userEmailLocalPrefixes: locals,
            fieldCipher: new FakeCipher(), indexTokenizer: new FakeTokenizer());
        return (plain, crypto);
    }

    private async Task<Dictionary<string, AttributeValue>?> RawItemAsync(string table, string pk, string sk)
        => await new DynamoTable(_db, table).GetAsync(pk, sk);

    private static AuthUser SampleUser(string id, string email, string? first = null, string? last = null) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        FirstName = first,
        LastName = last,
        PasswordHash = "hash",
        LockoutEnabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Profile_IsCiphertextAtRest_AndRoundTrips()
    {
        var (_, crypto) = await NewStoresAsync("cenc");
        await crypto.CreateAsync(SampleUser("u1", "secret@example.com", "Alice", "Smith"));

        var raw = await RawItemAsync("cencUsers", "u1", "profile");
        Assert.NotNull(raw);
        var data = raw!["data"].S!;
        Assert.StartsWith(FakeCipher.Prefix, data);
        Assert.DoesNotContain("secret@example.com", data);

        var read = await crypto.GetAsync("u1");
        Assert.Equal("secret@example.com", read!.Email);
        Assert.Equal("Alice", read.FirstName);
    }

    [Fact]
    public async Task EmailIndex_IsTokenKeyed_AndLookupWorks()
    {
        var (_, crypto) = await NewStoresAsync("ctok");
        await crypto.CreateAsync(SampleUser("u1", "alice@example.com"));

        // No plaintext-keyed lookup row exists...
        Assert.Null(await RawItemAsync("ctokEmails", "ALICE@EXAMPLE.COM", "lookup"));
        // ...but the blind-index lookup resolves.
        Assert.Equal("u1", (await crypto.FindByEmailAsync("alice@example.com"))?.Id);
    }

    [Fact]
    public async Task Search_UnderTokenization_MatchesNameAndEmailLocalPrefix()
    {
        var (_, crypto) = await NewStoresAsync("csrch");
        await crypto.CreateAsync(SampleUser("u1", "wendy@example.com", "Wendy", "Chen"));
        await crypto.CreateAsync(SampleUser("u2", "bob@example.com", "Bob", "Wendell"));

        var hits = await crypto.SearchAsync("wen");
        Assert.Contains(hits, u => u.Id == "u1"); // email local-part prefix
        Assert.Contains(hits, u => u.Id == "u2"); // last-name prefix
    }

    [Fact]
    public async Task DomainSearch_UnderTokenization_Works()
    {
        var (_, crypto) = await NewStoresAsync("cdom");
        await crypto.CreateAsync(SampleUser("u1", "a@acme.com"));
        await crypto.CreateAsync(SampleUser("u2", "b@acme.com"));

        var acme = await crypto.SearchByEmailDomainAsync("acme.com");
        Assert.Equal(2, acme.Count);
    }

    [Fact]
    public async Task LegacyPlaintextRows_ResolveViaDualRead_AndReindexMovesThem()
    {
        var (plain, crypto) = await NewStoresAsync("cmig");
        await plain.CreateAsync(SampleUser("u1", "legacy@example.com", "Lena", "Legacy"));

        // Dual-read: the crypto store finds the plaintext-keyed row.
        Assert.Equal("u1", (await crypto.FindByEmailAsync("legacy@example.com"))?.Id);
        // The plaintext-keyed email row exists pre-reindex.
        Assert.NotNull(await RawItemAsync("cmigEmails", "LEGACY@EXAMPLE.COM", "lookup"));

        await crypto.ReindexUserAsync("u1");

        // Profile re-encrypted, legacy email row dropped, lookup still resolves.
        var raw = await RawItemAsync("cmigUsers", "u1", "profile");
        Assert.StartsWith(FakeCipher.Prefix, raw!["data"].S);
        Assert.Null(await RawItemAsync("cmigEmails", "LEGACY@EXAMPLE.COM", "lookup"));
        Assert.Equal("u1", (await crypto.FindByEmailAsync("legacy@example.com"))?.Id);
        Assert.Contains(await crypto.SearchAsync("len"), u => u.Id == "u1");
    }

    [Fact]
    public async Task MigrateExternalIdIndex_MovesLegacyRows()
    {
        var (plain, crypto) = await NewStoresAsync("cext");
        await plain.CreateAsync(SampleUser("u1", "x@example.com"));
        await plain.SetExternalIdAsync("u1", "client-a", "ext-1");

        Assert.Equal(1, await crypto.MigrateExternalIdIndexAsync(dryRun: true));
        Assert.Equal(1, await crypto.MigrateExternalIdIndexAsync(dryRun: false));
        Assert.Equal(0, await crypto.MigrateExternalIdIndexAsync(dryRun: true)); // idempotent

        Assert.Null(await RawItemAsync("cextExtIds", "client-a|ext-1", "lookup"));
        Assert.Equal("u1", (await crypto.FindByExternalIdAsync("client-a", "ext-1"))?.Id);
    }

    [Fact]
    public async Task MigrateUserLogins_MovesAndEncryptsLegacyRows()
    {
        var (plain, crypto) = await NewStoresAsync("clog");
        await plain.CreateAsync(SampleUser("u1", "y@example.com"));
        await plain.AddLoginAsync(new ExternalLoginInfo { UserId = "u1", Provider = "saml", ProviderKey = "person@corp.com", DisplayName = "Person" });

        // Dual-read resolves the legacy row before migration.
        Assert.Equal("u1", (await crypto.FindLoginAsync("saml", "person@corp.com"))?.UserId);

        Assert.Equal(2, await crypto.MigrateUserLoginsAsync(dryRun: true)); // forward + reverse
        Assert.Equal(2, await crypto.MigrateUserLoginsAsync(dryRun: false));
        Assert.Equal(0, await crypto.MigrateUserLoginsAsync(dryRun: true));

        Assert.Null(await RawItemAsync("clogLogins", "saml|person@corp.com", "lookup"));
        var found = await crypto.FindLoginAsync("saml", "person@corp.com");
        Assert.Equal("u1", found?.UserId);
        Assert.Equal("Person", found?.DisplayName);
        Assert.Single(await crypto.GetLoginsAsync("u1"));
    }

    [Fact]
    public async Task LoginColumns_AreCiphertextAtRest()
    {
        var (_, crypto) = await NewStoresAsync("clenc");
        await crypto.CreateAsync(SampleUser("u1", "z@example.com"));
        await crypto.AddLoginAsync(new ExternalLoginInfo { UserId = "u1", Provider = "saml", ProviderKey = "nameid@corp.com", DisplayName = "Zed" });

        // Scan the raw table: no attribute anywhere carries the plaintext NameId.
        static string S(Dictionary<string, AttributeValue> item, string name)
            => item.TryGetValue(name, out var v) ? v.S ?? "" : "";
        var table = new DynamoTable(_db, "clencLogins");
        await foreach (var item in table.ScanAsync())
        {
            Assert.DoesNotContain("nameid@corp.com", S(item, "providerKey"));
            Assert.DoesNotContain("nameid@corp.com", S(item, "pk"));
            Assert.DoesNotContain("nameid@corp.com", S(item, "sk"));
        }

        var found = await crypto.FindLoginAsync("saml", "nameid@corp.com");
        Assert.Equal("nameid@corp.com", found?.ProviderKey);
    }

    [Fact]
    public async Task LoginStamps_DoNotRewriteTheDocument()
    {
        var (_, crypto) = await NewStoresAsync("cstamp");
        await crypto.CreateAsync(SampleUser("u1", "stamp@example.com"));
        var before = (await RawItemAsync("cstampUsers", "u1", "profile"))!["data"].S;

        await crypto.RecordSuccessfulLoginAsync("u1");
        await crypto.RecordFailedLoginAsync("u1", 5, TimeSpan.FromMinutes(15));

        var after = (await RawItemAsync("cstampUsers", "u1", "profile"))!["data"].S;
        Assert.Equal(before, after); // attribute-only stamps — the ciphertext never round-trips

        var read = await crypto.GetAsync("u1");
        Assert.NotNull(read!.LastLoginAt);       // overlay wins over the stale document
        Assert.Equal(1, read.AccessFailedCount);
    }

    [Fact]
    public async Task EnumerateLoginStates_NeverDecrypts_ForCurrentRows()
    {
        var (_, _) = await NewStoresAsync("ctrip");
        var tripwire = new TripwireCipher();
        async Task<DynamoTable> T(string name) => new(_db, $"ctrip{name}");
        var store = new DynamoUserStore(
            await T("Users"), await T("Emails"), await T("Logins"), await T("ExtIds"), null, null,
            EnvPartitioner.Live, fieldCipher: tripwire, indexTokenizer: new FakeTokenizer());

        await store.CreateAsync(SampleUser("u1", "trip@example.com"));
        tripwire.Armed = true; // any ResolveAsync from here on throws

        var states = new List<UserLoginState>();
        await foreach (var s in store.EnumerateLoginStatesAsync()) states.Add(s);
        Assert.Single(states);
    }

    /// <summary>Protects normally; throws on Resolve once armed — proves a path never decrypts.</summary>
    private sealed class TripwireCipher : IFieldCipher
    {
        public bool Armed;
        public Task<string> ProtectAsync(string plaintext, CancellationToken ct = default)
            => Task.FromResult("enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext)));
        public Task<string> ResolveAsync(string stored, CancellationToken ct = default)
            => Armed
                ? throw new InvalidOperationException("login-state enumeration must not decrypt")
                : Task.FromResult(stored.StartsWith("enc:", StringComparison.Ordinal)
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(stored[4..]))
                    : stored);
    }
}

using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// Increment 1 of at-rest PII encryption: the Tier-1 fields (Phone, CompanyName,
/// CustomAttributes) are encrypted at the entity level via <see cref="IFieldCipher"/>, so a raw
/// table dump exposes ciphertext, while a read round-trips back to plaintext. Email and names
/// stay plaintext at this increment (they gain blind indexes later). Verified against real Azure
/// Table semantics (Azurite).
/// </summary>
[Collection("Azurite")]
public class PiiEncryptionTests(AzuriteFixture azurite)
{
    /// <summary>
    /// Reversible, prefix-tagged fake cipher that mirrors TenantSecretCipher's contract: Protect
    /// tags ciphertext with a marker; Resolve reverses tagged input and passes anything else
    /// (legacy plaintext) through unchanged.
    /// </summary>
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

    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableUserStore NewStore(string prefix, IFieldCipher? cipher)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        return new TableUserStore(T("Users"), T("Emails"), T("Logins"), T("ExtIds"), null, null,
            EnvPartitioner.Live, fieldCipher: cipher);
    }

    /// <summary>
    /// F329 — staged sign-up PII is inside the encrypted set.
    /// </summary>
    /// <remarks>
    /// The Azure store encrypts a fixed column list, and PendingClaimJson was not on it — yet it
    /// serialises exactly the fields the list protects: first name, last name and the caller-supplied
    /// custom attributes, staged for a not-yet-confirmed registration. A table dump therefore exposed
    /// in cleartext precisely the PII the scheme exists to hide, for every user mid-signup. The AWS
    /// and SQL stores encrypt the whole serialized document, so they never had this gap — which is
    /// also why the shared provider-parity tests could not have caught it.
    /// </remarks>
    [Fact]
    public async Task PendingClaimJson_IsEncryptedAtRest()
    {
        var prefix = $"pc{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeCipher());

        var user = SampleUser("u-pending", "pending@example.com");
        user.PendingClaimJson = "{\"firstName\":\"Ada\",\"lastName\":\"Lovelace\"}";
        await store.CreateAsync(user);

        var raw = await RawProfile(prefix, "u-pending");
        Assert.NotNull(raw.PendingClaimJson);
        Assert.StartsWith(FakeCipher.Prefix, raw.PendingClaimJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Lovelace", raw.PendingClaimJson, StringComparison.Ordinal);

        // …and it round-trips, so the confirm path still reads what registration staged.
        var read = await store.GetAsync("u-pending");
        Assert.Equal(user.PendingClaimJson, read!.PendingClaimJson);
    }

    // Reads the profile row WITHOUT going through the store's decrypt, i.e. what a dump would show.
    private async Task<UserEntity> RawProfile(string prefix, string userId)
        => (await _svc.GetTableClient($"{prefix}Users")
            .GetEntityAsync<UserEntity>(userId, UserEntity.ProfileRowKey)).Value;

    private static AuthUser SampleUser(string id, string email) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        FirstName = "Ada",
        LastName = "Lovelace",
        CompanyName = "Analytical Engines Ltd",
        Phone = "+15551234567",
        CustomAttributes = new Dictionary<string, string> { ["department"] = "Research", ["badge"] = "0001" },
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Create_EncryptsAllPiiAtRest()
    {
        var prefix = $"piienc{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeCipher());
        await store.CreateAsync(SampleUser("u1", "ada@acme.test"));

        var raw = await RawProfile(prefix, "u1");

        // Every PII field is ciphertext in the profile row — a dump of Users exposes nothing.
        Assert.StartsWith(FakeCipher.Prefix, raw.Email);
        Assert.StartsWith(FakeCipher.Prefix, raw.NormalizedEmail);
        Assert.StartsWith(FakeCipher.Prefix, raw.FirstName);
        Assert.StartsWith(FakeCipher.Prefix, raw.LastName);
        Assert.StartsWith(FakeCipher.Prefix, raw.Phone);
        Assert.StartsWith(FakeCipher.Prefix, raw.CompanyName);
        Assert.StartsWith(FakeCipher.Prefix, raw.CustomAttributesJson);
        Assert.DoesNotContain("ada@acme.test", raw.Email);
        Assert.DoesNotContain("ADA@ACME.TEST", raw.NormalizedEmail);
        Assert.DoesNotContain("Lovelace", raw.LastName);
        Assert.DoesNotContain("+15551234567", raw.Phone);
        Assert.DoesNotContain("Research", raw.CustomAttributesJson);
    }

    [Fact]
    public async Task Get_RoundTripsDecryptedValues()
    {
        var prefix = $"piienc{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeCipher());
        await store.CreateAsync(SampleUser("u1", "ada@acme.test"));

        var got = await store.GetAsync("u1");

        Assert.NotNull(got);
        Assert.Equal("ada@acme.test", got!.Email);
        Assert.Equal("Ada", got.FirstName);
        Assert.Equal("Lovelace", got.LastName);
        Assert.Equal("+15551234567", got.Phone);
        Assert.Equal("Analytical Engines Ltd", got.CompanyName);
        Assert.Equal("Research", got.CustomAttributes["department"]);
        Assert.Equal("0001", got.CustomAttributes["badge"]);

        // And exact login lookup still resolves (plaintext index here — no tokenizer in this test).
        Assert.Equal("u1", (await store.FindByEmailAsync("ada@acme.test"))!.Id);
    }

    [Fact]
    public async Task LegacyPlaintextRow_StillReadable_AfterEncryptionEnabled()
    {
        // Written before encryption was turned on (no cipher) → plaintext at rest.
        var prefix = $"piienc{Guid.NewGuid():N}";
        await NewStore(prefix, cipher: null).CreateAsync(SampleUser("u1", "ada@acme.test"));

        var raw = await RawProfile(prefix, "u1");
        Assert.Equal("+15551234567", raw.Phone); // confirm it really is plaintext at rest

        // A cipher-enabled store reads it fine (Resolve passes legacy plaintext through).
        var encStore = NewStore(prefix, new FakeCipher());
        var got = await encStore.GetAsync("u1");
        Assert.Equal("+15551234567", got!.Phone);
        Assert.Equal("Analytical Engines Ltd", got.CompanyName);

        // And lookup by email (plaintext index) is unaffected.
        var byEmail = await encStore.FindByEmailAsync("ada@acme.test");
        Assert.Equal("u1", byEmail!.Id);
    }

    [Fact]
    public async Task EmptyCustomAttributes_StaysPlaintext()
    {
        var prefix = $"piienc{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeCipher());
        var user = SampleUser("u1", "ada@acme.test");
        user.CustomAttributes = new Dictionary<string, string>();
        user.Phone = null;
        await store.CreateAsync(user);

        var raw = await RawProfile(prefix, "u1");
        Assert.Equal("{}", raw.CustomAttributesJson); // empty map reveals nothing → no Vault round-trip
        Assert.Null(raw.Phone);                       // null field is left alone

        var got = await store.GetAsync("u1");
        Assert.Empty(got!.CustomAttributes);
        Assert.Null(got.Phone);
    }
}

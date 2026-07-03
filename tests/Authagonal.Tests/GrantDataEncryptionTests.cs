using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Entities;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Grant-store at-rest hardening: the raw refresh-token / device-code handle is no longer persisted
/// (only its SHA-256 in PartitionKey), and <see cref="PersistedGrant.Data"/> — which embeds the full
/// OidcSubject (email, name, claims) — is encrypted via <see cref="IFieldCipher"/>. So a dump of the
/// Grants / GrantsBySubject tables yields no live tokens and no session PII, while reads round-trip.
/// Verified against real Azure Table semantics (Azurite).
/// </summary>
[Collection("Azurite")]
public class GrantDataEncryptionTests(AzuriteFixture azurite)
{
    /// <summary>Reversible prefix-tagged fake cipher mirroring TenantSecretCipher's contract (see PiiEncryptionTests).</summary>
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

    private const string Handle = "refresh-handle-abc123";
    private const string PiiData = "{\"sub\":\"user-1\",\"email\":\"ada@acme.test\",\"name\":\"Ada Lovelace\"}";

    private TableGrantStore NewStore(string prefix, IFieldCipher? cipher)
    {
        TableClient T(string name)
        {
            var c = _svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        return new TableGrantStore(T("Grants"), T("GrantsBySubject"), T("GrantsByExpiry"),
            EnvPartitioner.Live, NullLogger<TableGrantStore>.Instance, fieldCipher: cipher);
    }

    private static PersistedGrant SampleGrant() => new()
    {
        Key = Handle,
        Type = "refresh_token",
        SubjectId = "user-1",
        ClientId = "app1",
        Data = PiiData,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
    };

    // Reads a row WITHOUT the store's decrypt — i.e. exactly what a table dump would expose.
    private async Task<TableEntity> RawRow(string prefix, string table, string pk, string rk)
        => (await _svc.GetTableClient($"{prefix}{table}").GetEntityAsync<TableEntity>(pk, rk)).Value;

    private static IEnumerable<string> StringValues(TableEntity e) => e.Select(kv => kv.Value).OfType<string>();

    [Fact]
    public async Task Store_EncryptsData_AndDoesNotPersistHandle_AtRest()
    {
        var prefix = $"grantenc{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeCipher());
        await store.StoreAsync(SampleGrant());

        var hashedKey = TableGrantStore.HashKey(Handle);

        // Primary Grants row.
        var raw = await RawRow(prefix, "Grants", EnvPartitioner.Live.PK(hashedKey), GrantEntity.GrantRowKey);
        Assert.StartsWith(FakeCipher.Prefix, raw.GetString("Data"));           // Data is ciphertext
        Assert.False(raw.ContainsKey("Key"));                                  // raw handle column is gone
        Assert.DoesNotContain(StringValues(raw), v => v.Contains(Handle));     // handle nowhere in the row
        Assert.DoesNotContain(StringValues(raw), v => v.Contains("ada@acme.test")); // subject PII not in clear

        // Subject-index GrantsBySubject row carries the same exposure and gets the same treatment.
        var sub = await RawRow(prefix, "GrantsBySubject", EnvPartitioner.Live.PK("user-1"), $"refresh_token|{hashedKey}");
        Assert.StartsWith(FakeCipher.Prefix, sub.GetString("Data"));
        Assert.False(sub.ContainsKey("Key"));
        Assert.DoesNotContain(StringValues(sub), v => v.Contains(Handle));
        Assert.DoesNotContain(StringValues(sub), v => v.Contains("ada@acme.test"));
    }

    [Fact]
    public async Task Get_RoundTripsDecryptedData_AndDoesNotReturnHandle()
    {
        var prefix = $"grantenc{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeCipher());
        await store.StoreAsync(SampleGrant());

        var got = await store.GetAsync(Handle);

        Assert.NotNull(got);
        Assert.Equal(PiiData, got!.Data);          // decrypts back to plaintext for the OIDC engine
        Assert.Equal("refresh_token", got.Type);
        Assert.Equal(string.Empty, got.Key);       // handle is not read back from storage
    }

    [Fact]
    public async Task GetBySubject_RoundTripsDecryptedData()
    {
        var prefix = $"grantenc{Guid.NewGuid():N}";
        var store = NewStore(prefix, new FakeCipher());
        await store.StoreAsync(SampleGrant());

        var grants = await store.GetBySubjectAsync("user-1");

        var g = Assert.Single(grants);
        Assert.Equal(PiiData, g.Data);
    }

    [Fact]
    public async Task NoCipher_StoresPlaintext_OssBehaviourUnchanged()
    {
        // Library/OSS hosts construct the store without a cipher → passthrough, plaintext at rest as before.
        var prefix = $"grantenc{Guid.NewGuid():N}";
        var store = NewStore(prefix, cipher: null);
        await store.StoreAsync(SampleGrant());

        var raw = await RawRow(prefix, "Grants", EnvPartitioner.Live.PK(TableGrantStore.HashKey(Handle)), GrantEntity.GrantRowKey);
        Assert.Equal(PiiData, raw.GetString("Data"));   // unchanged plaintext
        Assert.False(raw.ContainsKey("Key"));           // but the handle is still never persisted
    }

    [Fact]
    public async Task LegacyPlaintextData_StillReadable_AfterEncryptionEnabled()
    {
        // Grant written before encryption was turned on (no cipher) → plaintext Data at rest.
        var prefix = $"grantenc{Guid.NewGuid():N}";
        await NewStore(prefix, cipher: null).StoreAsync(SampleGrant());

        // Now read through a cipher-enabled store: ResolveAsync passes unrecognised plaintext through.
        var got = await NewStore(prefix, new FakeCipher()).GetAsync(Handle);

        Assert.NotNull(got);
        Assert.Equal(PiiData, got!.Data);
    }
}

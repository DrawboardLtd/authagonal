using System.Net;
using System.Net.Http.Json;
using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.SqlProvider.Sql;
using Authagonal.SqlProvider.Stores;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// F179 — the private signing key goes through the same at-rest seam as everything else.
/// </summary>
/// <remarks>
/// KeyMaterialJson holds the full JWK, private scalar included, and it was written in the clear.
/// Anyone who could read the primary data store could mint a token this server would be trusted to
/// have signed — for any user, any scope, any session. That is the one secret whose exposure is not
/// degraded access but complete impersonation of the issuer, and it sat beside the data it protects.
/// Exercised over SQLite, where the whole store is inspectable.
/// </remarks>
public sealed class SigningKeyAtRestTests
{
    /// <summary>Reversible, prefix-tagged, and passes unrecognised input through — the IFieldCipher
    /// contract, so a legacy plaintext row keeps loading.</summary>
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

    private const string PrivateJwk = """{"kty":"EC","crv":"P-256","d":"THE-PRIVATE-SCALAR","x":"X","y":"Y"}""";

    private static SigningKeyInfo Key(string id = "k1") => new()
    {
        KeyId = id,
        Algorithm = "ES256",
        KeyMaterialJson = PrivateJwk,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
    };

    [Fact]
    public async Task PrivateKeyMaterial_IsNotStoredInTheClear()
    {
        await using var source = SqlTestSource.Sqlite();
        await source.EnsureTableAsync("SigningKeys");
        var table = new SqlTable(source, "SigningKeys");

        var store = new SqlSigningKeyStore(table, EnvPartitioner.Live, null, new FakeCipher());
        await store.StoreAsync(Key());

        // Read the row beneath the store, i.e. what a database dump would show.
        var raw = await table.GetAsync(EnvPartitioner.Live.PK("signing"), "k1");
        Assert.NotNull(raw);
        var stored = raw!.GetStr("keyMaterialJson");
        Assert.StartsWith(FakeCipher.Prefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("THE-PRIVATE-SCALAR", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectedKeyMaterial_RoundTripsThroughEveryReadPath()
    {
        await using var source = SqlTestSource.Sqlite();
        await source.EnsureTableAsync("SigningKeys");
        var table = new SqlTable(source, "SigningKeys");
        var store = new SqlSigningKeyStore(table, EnvPartitioner.Live, null, new FakeCipher());

        await store.StoreAsync(Key());

        // If any read path forgot to decrypt, the server would sign with a base64 blob and every
        // token would fail verification everywhere — so all three are asserted.
        Assert.Equal(PrivateJwk, (await store.GetActiveKeyAsync())!.KeyMaterialJson);
        Assert.Equal(PrivateJwk, Assert.Single(await store.GetAllAsync()).KeyMaterialJson);

        await store.DeactivateKeyAsync("k1");
        var deactivated = Assert.Single(await store.GetAllAsync());
        Assert.False(deactivated.IsActive);
        Assert.Equal(PrivateJwk, deactivated.KeyMaterialJson);
    }

    [Fact]
    public async Task KeysWrittenBeforeTheCipher_StillLoad()
    {
        // The seam's contract is that an unrecognised value is legacy plaintext and passes through.
        // Without that, turning encryption on would brick an existing deployment's signing key.
        await using var source = SqlTestSource.Sqlite();
        await source.EnsureTableAsync("SigningKeys");
        var table = new SqlTable(source, "SigningKeys");

        await new SqlSigningKeyStore(table, EnvPartitioner.Live).StoreAsync(Key());

        var withCipher = new SqlSigningKeyStore(table, EnvPartitioner.Live, null, new FakeCipher());
        Assert.Equal(PrivateJwk, (await withCipher.GetActiveKeyAsync())!.KeyMaterialJson);
    }

    [Fact]
    public async Task WithNoCipher_BehaviourIsUnchanged()
    {
        await using var source = SqlTestSource.Sqlite();
        await source.EnsureTableAsync("SigningKeys");
        var table = new SqlTable(source, "SigningKeys");
        var store = new SqlSigningKeyStore(table, EnvPartitioner.Live);

        await store.StoreAsync(Key());

        Assert.Equal(PrivateJwk, (await store.GetActiveKeyAsync())!.KeyMaterialJson);
    }
}

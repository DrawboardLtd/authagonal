using System.Text.Json;
using Authagonal.Server.Services;

namespace Authagonal.Tests;

/// <summary>
/// Vault Transit's read-key response serializes each key version as an OBJECT for asymmetric keys
/// (ecdsa-p256 / rsa) but as a bare NUMBER (unix-seconds creation time) for symmetric keys
/// (aes256-gcm96). Before the converter, deserializing a symmetric key threw
/// "could not be converted to TransitKeyVersion. Path: $.data.keys.1", breaking ReadKeyAsync /
/// KeyExists / EnsureKeyType for the encryption keys backing secret-at-rest.
/// </summary>
public sealed class TransitKeyVersionConverterTests
{
    [Fact]
    public void ParsesSymmetricKeyVersion_FromBareNumber()
    {
        var v = JsonSerializer.Deserialize<TransitKeyVersion>("1699999999");

        Assert.NotNull(v);
        Assert.Null(v!.PublicKey);
        Assert.Equal("1699999999", v.CreationTime);
    }

    [Fact]
    public void ParsesAsymmetricKeyVersion_FromObject()
    {
        var json = """
            {"public_key":"-----BEGIN PUBLIC KEY-----\nABC\n-----END PUBLIC KEY-----","creation_time":"2026-07-02T00:00:00Z","name":"ecdsa-p256"}
            """;

        var v = JsonSerializer.Deserialize<TransitKeyVersion>(json);

        Assert.NotNull(v);
        Assert.Contains("BEGIN PUBLIC KEY", v!.PublicKey);
        Assert.Equal("2026-07-02T00:00:00Z", v.CreationTime);
    }

    [Fact]
    public void ParsesSymmetricKeysMap_MultipleVersions()
    {
        // The whole "keys" map Vault returns for an aes256-gcm96 key after a rotation.
        var map = JsonSerializer.Deserialize<Dictionary<string, TransitKeyVersion>>(
            """{"1":1699999999,"2":1700000000}""");

        Assert.NotNull(map);
        Assert.Equal(2, map!.Count);
        Assert.Equal("1699999999", map["1"].CreationTime);
        Assert.Equal("1700000000", map["2"].CreationTime);
        Assert.All(map.Values, v => Assert.Null(v.PublicKey));
    }
}

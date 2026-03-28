using Authagonal.Storage.Stores;

namespace Authagonal.Tests;

public class HashKeyTests
{
    [Fact]
    public void HashKey_SameInput_ProducesSameOutput()
    {
        var hash1 = TableGrantStore.HashKey("my-grant-key");
        var hash2 = TableGrantStore.HashKey("my-grant-key");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashKey_DifferentInputs_ProduceDifferentOutputs()
    {
        var hash1 = TableGrantStore.HashKey("key-one");
        var hash2 = TableGrantStore.HashKey("key-two");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void HashKey_Returns64CharLowercaseHex()
    {
        var hash = TableGrantStore.HashKey("test");
        Assert.Equal(64, hash.Length); // SHA256 = 32 bytes = 64 hex chars
        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.True(hash.All(c => "0123456789abcdef".Contains(c)));
    }

    [Fact]
    public void HashKey_MatchesKnownSha256()
    {
        // SHA256("hello") = 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824
        var hash = TableGrantStore.HashKey("hello");
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
    }
}

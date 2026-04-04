using Authagonal.Server.Services;

namespace Authagonal.Tests;

public class PlaintextSecretProviderTests
{
    private readonly PlaintextSecretProvider _provider = new();

    [Fact]
    public async Task ResolveAsync_ReturnsInputUnchanged()
    {
        var result = await _provider.ResolveAsync("my-secret-value");
        Assert.Equal("my-secret-value", result);
    }

    [Fact]
    public async Task ProtectAsync_ReturnsPlaintextUnchanged()
    {
        var result = await _provider.ProtectAsync("secret-name", "the-actual-secret");
        Assert.Equal("the-actual-secret", result);
    }

    [Fact]
    public async Task ResolveAsync_PreservesKvPrefix()
    {
        var result = await _provider.ResolveAsync("kv:some-secret");
        Assert.Equal("kv:some-secret", result);
    }

    [Fact]
    public async Task ResolveAsync_EmptyString_ReturnsEmpty()
    {
        var result = await _provider.ResolveAsync("");
        Assert.Equal("", result);
    }
}

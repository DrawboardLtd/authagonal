using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Verifies the server boots without errors.
/// Catches RDG/trimming issues (e.g. positional records without parameterless constructors,
/// required properties without setters) that only surface at startup during endpoint resolution.
/// </summary>
public sealed class ServerStartupTests : IAsyncLifetime
{
    private AuthagonalTestFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new AuthagonalTestFactory();
        await _factory.SeedTestDataAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Server_starts_and_health_endpoint_responds()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Discovery_endpoint_responds()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/.well-known/openid-configuration");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// SCIM bearer-token enforcement: a revoked or expired token must be rejected (401),
/// not silently accepted. Provisioning runs unattended from an IdP, so a stale token
/// that still works is a real exposure.
/// </summary>
public sealed class ScimTokenAuthTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    // Mint a SCIM token for the client with explicit expiry / revoked state.
    private async Task<string> SeedTokenAsync(string clientId, DateTimeOffset? expiresAt = null, bool revoked = false)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await _factory.ScimTokenStore.StoreAsync(new ScimToken
        {
            TokenId = Guid.NewGuid().ToString("N"),
            ClientId = clientId,
            TokenHash = Hash(raw),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            IsRevoked = revoked,
        });
        return raw;
    }

    private HttpClient ClientWith(string rawToken)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        return c;
    }

    [Fact]
    public async Task RevokedToken_Returns401()
    {
        await _factory.SeedTestDataAsync();
        var (clientId, _) = await _factory.SeedScimClientAsync();
        var raw = await SeedTokenAsync(clientId, revoked: true);

        var res = await ClientWith(raw).GetAsync("/scim/v2/Users");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_Returns401()
    {
        await _factory.SeedTestDataAsync();
        var (clientId, _) = await _factory.SeedScimClientAsync();
        var raw = await SeedTokenAsync(clientId, expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        var res = await ClientWith(raw).GetAsync("/scim/v2/Users");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ValidUnexpiredToken_IsAuthorized()
    {
        await _factory.SeedTestDataAsync();
        var (clientId, _) = await _factory.SeedScimClientAsync();
        var raw = await SeedTokenAsync(clientId, expiresAt: DateTimeOffset.UtcNow.AddDays(30));

        var res = await ClientWith(raw).GetAsync("/scim/v2/Users");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task UnknownToken_Returns401()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedScimClientAsync();

        var res = await ClientWith("not-a-real-token").GetAsync("/scim/v2/Users");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// F73 — group creation was unbounded, and no backend indexes group ownership.
/// </summary>
/// <remarks>
/// The rate limiter added earlier paces the writes but does not bound the table, and the table is what
/// matters: <c>GetGroupsByUserIdAsync</c> is an unindexed full scan of it and runs on EVERY token mint
/// and every /connect/userinfo call for the tenant once a group→role mapping exists. So one provisioning
/// token could make every login in the tenant scan an arbitrarily large table — the amplification the
/// review called the worst path. A per-client quota and a member cap are what bound it.
/// </remarks>
public sealed class ScimGroupAmplificationTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task GroupCreationStopsAtThePerClientQuota()
    {
        _factory.ConfigureAuthOptions = o => o.MaxScimGroupsPerClient = 2;
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        for (var i = 0; i < 2; i++)
        {
            var ok = await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = $"Group {i}" });
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        var refused = await client.PostAsJsonAsync("/scim/v2/Groups", new { displayName = "One too many" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task AnOversizedMembershipIsRefused()
    {
        // Membership is one uncapped list on the group row, and every id in it is re-verified against the
        // user store on write — an unbounded array is an unbounded row AND an unbounded number of point
        // reads inside a single request.
        _factory.ConfigureAuthOptions = o => o.MaxScimGroupMembers = 3;
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.PostAsJsonAsync("/scim/v2/Groups", new
        {
            displayName = "Everyone",
            members = Enumerable.Range(0, 4).Select(i => new { value = $"u{i}" }).ToArray(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The cap has to be what refused it, not the per-member ownership check further down — the whole
        // point is to reject the oversized list BEFORE spending a store read per member.
        Assert.Contains("at most 3 members", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.AzureProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// F26 cursor paging: ListPageAsync / ListByScimClientPageAsync resume from the SDK's opaque
/// continuation token, so page N costs one storage page instead of re-enumerating (and, with
/// encryption on, re-decrypting) every earlier row. Verified against real Azure Table paging
/// semantics (Azurite).
/// </summary>
[Collection("Azurite")]
public class UserListCursorPagingTests(AzuriteFixture azurite)
{
    private TableUserStore NewStore(string prefix)
    {
        var svc = new TableServiceClient(azurite.ConnectionString);
        TableClient T(string name)
        {
            var c = svc.GetTableClient($"{prefix}{name}");
            c.CreateIfNotExists();
            return c;
        }
        return new TableUserStore(T("Users"), T("Emails"), T("Logins"), T("ExtIds"), null, null, EnvPartitioner.Live);
    }

    private static AuthUser User(string id, string email, string? scimClientId = null, string? orgId = null) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        ScimProvisionedByClientId = scimClientId,
        OrganizationId = orgId,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ListPage_TokensWalkTheWholePopulation_WithoutOverlap()
    {
        var store = NewStore("CursorPage" + Guid.NewGuid().ToString("N")[..8]);
        for (var i = 0; i < 7; i++)
            await store.CreateAsync(User($"u{i}", $"user{i}@example.com"));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        var pages = 0;
        do
        {
            var page = await store.ListPageAsync(null, 3, token);
            foreach (var u in page.Users)
                Assert.True(seen.Add(u.Id), $"duplicate user {u.Id} across pages");
            token = page.ContinuationToken;
            pages++;
            Assert.True(pages <= 10, "runaway pagination");
        } while (token is not null);

        Assert.Equal(7, seen.Count);
        Assert.True(pages >= 3); // 7 users at page size 3 → at least 3 pages
    }

    [Fact]
    public async Task ListByScimClientPage_ScopesToClient_AndPages()
    {
        var store = NewStore("CursorScim" + Guid.NewGuid().ToString("N")[..8]);
        for (var i = 0; i < 5; i++)
            await store.CreateAsync(User($"s{i}", $"scim{i}@example.com", scimClientId: "client-a"));
        await store.CreateAsync(User("other", "other@example.com", scimClientId: "client-b"));
        await store.CreateAsync(User("manual", "manual@example.com"));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await store.ListByScimClientPageAsync("client-a", 2, token);
            foreach (var u in page.Users)
            {
                Assert.Equal("client-a", u.ScimProvisionedByClientId);
                Assert.True(seen.Add(u.Id));
            }
            token = page.ContinuationToken;
        } while (token is not null);

        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public async Task ListPage_OrganizationFilter_ReturnsOnlyMatches_AcrossPages()
    {
        var store = NewStore("CursorOrg" + Guid.NewGuid().ToString("N")[..8]);
        for (var i = 0; i < 6; i++)
            await store.CreateAsync(User($"o{i}", $"org{i}@example.com", orgId: i % 3 == 0 ? "acme" : "other"));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await store.ListPageAsync("acme", 2, token);
            foreach (var u in page.Users)
            {
                Assert.Equal("acme", u.OrganizationId);
                seen.Add(u.Id);
            }
            token = page.ContinuationToken;
        } while (token is not null);

        Assert.Equal(2, seen.Count); // o0 and o3
    }
}

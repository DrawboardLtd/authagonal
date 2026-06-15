using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Storage.Stores;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;

namespace Authagonal.Tests;

/// <summary>
/// Regression tests for SCIM user listing against real Azure Table semantics (Azurite).
/// The SCIM list endpoint calls ListByScimClientAsync with count = int.MaxValue to fetch
/// every user; the store must not let count + 1 overflow into a negative maxPerPage, which
/// Azure Table rejects with 400 InvalidInput (previously surfaced as a 500 on GET /scim/v2/Users).
/// </summary>
[Collection("Azurite")]
public class ScimUserListingTests(AzuriteFixture azurite)
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
        // Name-index tables are optional; pass null (not exercised here).
        return new TableUserStore(T("Users"), T("Emails"), T("Logins"), T("ExtIds"), null, null, EnvPartitioner.Live);
    }

    private static AuthUser ScimUser(string id, string email, string clientId) => new()
    {
        Id = id,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        ScimProvisionedByClientId = clientId,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ListByScimClient_WithMaxValueCount_ReturnsClientsUsers_WithoutOverflow()
    {
        var store = NewStore($"scimlist{Guid.NewGuid():N}");
        await store.CreateAsync(ScimUser("u1", "a@acme.test", "authagonal-scim"));
        await store.CreateAsync(ScimUser("u2", "b@acme.test", "authagonal-scim"));
        await store.CreateAsync(ScimUser("u3", "c@acme.test", "other-client"));

        // int.MaxValue is exactly what ScimUserEndpoints.ListUsersAsync passes.
        var (users, hasMore) = await store.ListByScimClientAsync("authagonal-scim", 0, int.MaxValue);

        Assert.False(hasMore);
        Assert.Equal(2, users.Count);
        Assert.All(users, u => Assert.Equal("authagonal-scim", u.ScimProvisionedByClientId));
        Assert.DoesNotContain(users, u => u.Id == "u3");
    }

    [Fact]
    public async Task ListByScimClient_PagesAndReportsHasMore_ForSmallCount()
    {
        var store = NewStore($"scimpage{Guid.NewGuid():N}");
        for (var i = 0; i < 5; i++)
            await store.CreateAsync(ScimUser($"u{i}", $"u{i}@acme.test", "authagonal-scim"));

        var (page, hasMore) = await store.ListByScimClientAsync("authagonal-scim", 0, 3);

        Assert.True(hasMore);
        Assert.Equal(3, page.Count);
    }
}

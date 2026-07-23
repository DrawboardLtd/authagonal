using System.Security.Claims;
using Authagonal.Core.Services;
using Authagonal.Server;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Authagonal.Tests;

/// <summary>M5: TableTicketStore lifecycle against Azurite — a double remove is a no-op (404-swallow),
/// ListAsync hides expired sessions, and establishing a new session lazily sweeps the user's expired ones.</summary>
[Collection("Azurite")]
public class TableTicketStoreTests(AzuriteFixture azurite)
{
    private TableTicketStore NewStore()
    {
        var svc = new TableServiceClient(azurite.ConnectionString);
        var sessions = svc.GetTableClient($"sess{Guid.NewGuid():N}");
        var byUser = svc.GetTableClient($"sessu{Guid.NewGuid():N}");
        sessions.CreateIfNotExists();
        byUser.CreateIfNotExists();
        return new TableTicketStore(sessions, byUser, EnvPartitioner.Live, new HttpContextAccessor());
    }

    private static AuthenticationTicket Ticket(string userId, DateTimeOffset expires)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], "cookie"));
        return new AuthenticationTicket(principal,
            new AuthenticationProperties { ExpiresUtc = expires, IssuedUtc = DateTimeOffset.UtcNow }, "cookie");
    }

    [Fact]
    public async Task DoubleRemove_isNoOp()
    {
        var store = NewStore();
        var key = await store.StoreAsync(Ticket("user1", DateTimeOffset.UtcNow.AddHours(1)));
        await store.RemoveAsync(key);
        await store.RemoveAsync(key); // must not throw — 404-swallowed
        Assert.Null(await store.RetrieveAsync(key));
    }

    [Fact]
    public async Task List_hidesExpiredSessions()
    {
        var store = NewStore();
        var key = await store.StoreAsync(Ticket("user2", DateTimeOffset.UtcNow.AddHours(1)));
        await store.RenewAsync(key, Ticket("user2", DateTimeOffset.UtcNow.AddMinutes(-5))); // now expired (renew doesn't sweep)
        Assert.Empty(await store.ListAsync("user2", null));
    }

    [Fact]
    public async Task NewSession_sweepsExpiredForUser()
    {
        var store = NewStore();
        var expiredKey = await store.StoreAsync(Ticket("user3", DateTimeOffset.UtcNow.AddHours(1)));
        await store.RenewAsync(expiredKey, Ticket("user3", DateTimeOffset.UtcNow.AddMinutes(-5))); // expired, lingering
        await store.StoreAsync(Ticket("user3", DateTimeOffset.UtcNow.AddHours(1))); // new session triggers the sweep
        Assert.Null(await store.RetrieveAsync(expiredKey));
        Assert.Single(await store.ListAsync("user3", null));
    }
}

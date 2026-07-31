using System.Security.Claims;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Microsoft.Extensions.DependencyInjection;
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
    private TableTicketStore NewStore(IUpstreamRefreshTokenStore? upstream = null)
    {
        var svc = new TableServiceClient(azurite.ConnectionString);
        var sessions = svc.GetTableClient($"sess{Guid.NewGuid():N}");
        var byUser = svc.GetTableClient($"sessu{Guid.NewGuid():N}");
        sessions.CreateIfNotExists();
        byUser.CreateIfNotExists();

        var services = new ServiceCollection();
        if (upstream is not null) services.AddSingleton(upstream);

        return new TableTicketStore(sessions, byUser, EnvPartitioner.Live, new HttpContextAccessor(),
            services.BuildServiceProvider());
    }

    /// <summary>A federated session: the claims the upstream-token key is built from.</summary>
    private static AuthenticationTicket FederatedTicket(string userId, string connectionId, string sid) =>
        new(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", userId),
                new Claim("upstream_connection_id", connectionId),
                new Claim("sid", sid),
            ], "cookie")),
            new AuthenticationProperties
            {
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
                IssuedUtc = DateTimeOffset.UtcNow,
            }, "cookie");

    /// <summary>Records what was removed, which is the whole assertion.</summary>
    private sealed class RecordingUpstreamStore : IUpstreamRefreshTokenStore
    {
        public List<(string UserId, string ConnectionId, string SessionId)> Removed { get; } = [];

        public Task SetAsync(string userId, string connectionId, string sessionId, string refreshToken,
            DateTimeOffset expiresAt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetAsync(string userId, string connectionId, string sessionId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task RemoveAsync(string userId, string connectionId, string sessionId, CancellationToken ct = default)
        {
            Removed.Add((userId, connectionId, sessionId));
            return Task.CompletedTask;
        }
    }

    // -----------------------------------------------------------------------
    // F335 (remainder) — every way a session ends drops the upstream credential
    // -----------------------------------------------------------------------

    /// <remarks>
    /// The sign-out paths read the (userId, connectionId, sid) key off the CURRENT principal, which
    /// left every other way a session ends — revoking one from the account page, "sign out
    /// everywhere", the expiry sweep — leaving a live bearer credential for ANOTHER identity provider
    /// behind for up to its own seven-day bound. That was written off as needing a wider
    /// IUserSessionRegistry, since a SessionDescriptor carries an opaque id and no principal. It does
    /// not: the ticket is stored here, so the principal is available at the exact moment the session
    /// is destroyed, whoever destroyed it.
    /// </remarks>
    [Fact]
    public async Task RevokingAnotherSession_DropsItsUpstreamRefreshToken()
    {
        var upstream = new RecordingUpstreamStore();
        var store = NewStore(upstream);

        var key = await store.StoreAsync(FederatedTicket("user-f1", "conn-1", "sid-1"));
        Assert.True(await store.RevokeAsync("user-f1", key));

        Assert.Equal(("user-f1", "conn-1", "sid-1"), Assert.Single(upstream.Removed));
    }

    [Fact]
    public async Task SignOutEverywhere_DropsEveryUpstreamRefreshToken()
    {
        var upstream = new RecordingUpstreamStore();
        var store = NewStore(upstream);

        await store.StoreAsync(FederatedTicket("user-f2", "conn-1", "sid-a"));
        await store.StoreAsync(FederatedTicket("user-f2", "conn-2", "sid-b"));
        var current = await store.StoreAsync(FederatedTicket("user-f2", "conn-3", "sid-current"));

        Assert.Equal(2, await store.RevokeOthersAsync("user-f2", current));

        Assert.Equal(2, upstream.Removed.Count);
        Assert.Contains(("user-f2", "conn-1", "sid-a"), upstream.Removed);
        Assert.Contains(("user-f2", "conn-2", "sid-b"), upstream.Removed);
        // The session the user kept keeps its credential.
        Assert.DoesNotContain(("user-f2", "conn-3", "sid-current"), upstream.Removed);
    }

    [Fact]
    public async Task ANonFederatedSession_RemovesNothing()
    {
        // A session with no upstream connection was not federated with revalidation on. Nothing to
        // remove, and no key to build one from.
        var upstream = new RecordingUpstreamStore();
        var store = NewStore(upstream);

        var key = await store.StoreAsync(Ticket("user-f3", DateTimeOffset.UtcNow.AddHours(1)));
        await store.RemoveAsync(key);

        Assert.Empty(upstream.Removed);
    }

    [Fact]
    public async Task WithNoUpstreamStoreRegistered_RevocationStillWorks()
    {
        var store = NewStore();
        var key = await store.StoreAsync(FederatedTicket("user-f4", "conn-1", "sid-1"));

        await store.RemoveAsync(key);

        Assert.Null(await store.RetrieveAsync(key));
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

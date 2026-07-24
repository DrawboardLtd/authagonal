using Authagonal.Core.Authority;
using Authagonal.Core.Services;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Capability tickets: short-lived single-use handles over the grant store. The property
/// under test is atomic single-use — the double-redeem that a plain cache's get-then-remove
/// leaves open must fail here.
/// </summary>
public sealed class CapabilityTicketTests
{
    private readonly InMemoryGrantStore _grants = new();
    private readonly GrantStoreCapabilityTicketService _service;

    public CapabilityTicketTests()
    {
        _service = new GrantStoreCapabilityTicketService(_grants, [new RecordingHook()]);
    }

    private sealed class RecordingHook : IAuthHook
    {
        public List<string> Redeemed { get; } = [];

        public Task OnCapabilityTicketRedeemedAsync(string ticketId, string? subjectId, string clientId, CancellationToken ct = default)
        {
            Redeemed.Add(ticketId);
            return Task.CompletedTask;
        }

        public Task OnUserAuthenticatedAsync(string userId, string email, string method, string? clientId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnUserCreatedAsync(string userId, string email, string createdVia, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Core.Models.MfaPolicy> ResolveMfaPolicyAsync(string userId, string email, Core.Models.MfaPolicy clientPolicy, string clientId, CancellationToken ct = default) => Task.FromResult(clientPolicy);
        public Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnUserUpdatedAsync(string userId, string email, string updatedVia, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnUserDeletedAsync(string userId, string email, string deletedVia, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Mint_Redeem_ReturnsBoundTokenOnce()
    {
        var handle = await _service.MintAsync("the-bound-token", "agent-client", "user-1");

        var first = await _service.TryRedeemAsync(handle);
        Assert.NotNull(first);
        Assert.Equal("the-bound-token", first.BoundToken);
        Assert.Equal("agent-client", first.ClientId);
        Assert.Equal("user-1", first.SubjectId);

        // Atomic single-use: the second redemption must lose.
        Assert.Null(await _service.TryRedeemAsync(handle));
    }

    [Fact]
    public async Task Redeem_ExpiredTicket_IsNull()
    {
        var handle = await _service.MintAsync("t", "c", ttl: TimeSpan.FromMilliseconds(-1));
        Assert.Null(await _service.TryRedeemAsync(handle));
    }

    [Fact]
    public async Task Redeem_UnknownHandle_IsNull()
    {
        Assert.Null(await _service.TryRedeemAsync("no-such-handle"));
        Assert.Null(await _service.TryRedeemAsync(""));
    }

    [Fact]
    public async Task Narrowing_RoundTrips()
    {
        var narrowing = AuthoritySet.Of(new AuthorityGrant { Type = "email", Actions = ["read"] });
        var handle = await _service.MintAsync("t", "c", narrowing: narrowing);

        var redeemed = await _service.TryRedeemAsync(handle);
        Assert.NotNull(redeemed?.Narrowing);
        Assert.Equal(AuthorityJson.Serialize(narrowing), AuthorityJson.Serialize(redeemed.Narrowing));
    }

    [Fact]
    public async Task ConcurrentRedemption_ExactlyOneWins()
    {
        var handle = await _service.MintAsync("t", "c");

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => _service.TryRedeemAsync(handle))));

        Assert.Equal(1, results.Count(r => r is not null));
    }
}

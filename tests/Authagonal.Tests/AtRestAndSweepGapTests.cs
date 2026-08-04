using System.Security.Claims;
using Authagonal.Core.Clustering;
using Authagonal.Core.Services;
using Authagonal.Server.Services;
using Authagonal.Server;
using Authagonal.Tests.Infrastructure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Two retention gaps and one encryption gap, all on the default backend.
/// </summary>
[Collection("Azurite")]
public class AtRestAndSweepGapTests(AzuriteFixture azurite)
{
    private readonly TableServiceClient _svc = new(azurite.ConnectionString);

    private TableClient NewTable(string prefix)
    {
        var table = _svc.GetTableClient($"{prefix}{Guid.NewGuid():N}"[..24]);
        table.CreateIfNotExists();
        return table;
    }

    private sealed class AlwaysLeader : ILeaderElection
    {
        public bool IsLeader => true;
        public string NodeId => "test";
        public string? LeaderId => "test";
    }

    // ── #53: three tables nothing collected on Azure ─────────────────────────

    /// <summary>
    /// Azure Table has no TTL, and nothing swept <c>MfaChallenges</c>, <c>UpstreamRefreshTokens</c> or
    /// <c>RevokedTokens</c> — while DynamoDB expires all three natively and <c>SqlExpiryReaper</c>'s table list
    /// covers all three.
    /// </summary>
    /// <remarks>
    /// Azure Table is the DEFAULT and the documented quick start, so the one backend with no collector was the
    /// one most deployments use. All three accumulate on ordinary traffic: an abandoned second factor leaves a
    /// permanent <c>MfaChallenges</c> row; every federated session leaves an <c>UpstreamRefreshTokens</c> row
    /// holding a live credential for another provider; and <c>RevokedTokens</c>' own comment says entries live
    /// "exactly as long as the token it kills and the stores' existing expiry reapers keep the list bounded" —
    /// a reaper this backend did not have.
    /// </remarks>
    [Theory]
    [InlineData("ExpiresAt")]
    [InlineData("ExpiresUtc")]
    public async Task TheSweepRemovesExpiredRowsAndKeepsLiveOnes(string expiryProperty)
    {
        var table = NewTable("swp");
        var now = DateTimeOffset.UtcNow;

        await table.AddEntityAsync(new TableEntity("p", "dead") { [expiryProperty] = now.AddMinutes(-5) });
        await table.AddEntityAsync(new TableEntity("p", "live") { [expiryProperty] = now.AddMinutes(30) });

        var (removed, failures) = await NewSweep(table, expiryProperty).SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Equal(0, failures);
        Assert.NotNull(await table.GetEntityAsync<TableEntity>("p", "live"));
        await Assert.ThrowsAsync<Azure.RequestFailedException>(
            () => table.GetEntityAsync<TableEntity>("p", "dead"));
    }

    /// <summary>
    /// A row with no expiry at all is never swept.
    /// </summary>
    /// <remarks>
    /// <c>UpstreamRefreshTokenEntity.ExpiresUtc</c> is nullable, and null means "no expiry known" — for a row
    /// holding an upstream credential. A sweep must not invent one. The OData <c>le</c> predicate does not match
    /// null, which is what makes this true, and it is worth pinning because the alternative (a client-side
    /// filter) would have swept them.
    /// </remarks>
    [Fact]
    public async Task ARowWithNoStatedExpiryIsNeverSwept()
    {
        var table = NewTable("swpn");

        await table.AddEntityAsync(new TableEntity("p", "no-expiry") { ["Token"] = "upstream-secret" });
        await table.AddEntityAsync(new TableEntity("p", "dead")
        {
            ["ExpiresUtc"] = DateTimeOffset.UtcNow.AddMinutes(-5),
        });

        var (removed, _) = await NewSweep(table, "ExpiresUtc").SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.NotNull(await table.GetEntityAsync<TableEntity>("p", "no-expiry"));
    }

    /// <summary>
    /// The filter must be one Table Storage can actually parse.
    /// </summary>
    /// <remarks>
    /// The reason this runs against Azurite rather than a fake: the predicate is server-side, and a filter the
    /// SDK cannot render throws at query time — on the leader, inside a catch that logs and waits for the next
    /// tick, which is to say invisibly. Nothing short of a real query proves it parses. Its sibling
    /// <c>RateLimitCounterSweepService</c> records the same lesson.
    /// </remarks>
    [Fact]
    public async Task TheSweepFilterParsesServerSide()
    {
        var table = NewTable("swpf");
        var (removed, failures) = await NewSweep(table, "ExpiresAt").SweepOnceAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.Equal(0, failures);
    }

    private static TableExpirySweepService NewSweep(TableClient table, string expiryProperty) =>
        new(table, expiryProperty, "test row", new AlwaysLeader(),
            NullLogger<TableExpirySweepService>.Instance);

    // ── #18: the session row is the whole ticket ─────────────────────────────

    /// <summary>
    /// Server-side session rows persisted the full authentication ticket in cleartext.
    /// </summary>
    /// <remarks>
    /// The <c>Data</c> column is base64 of everything on the principal: the user's email, name and phone, every
    /// <c>federated:*</c> claim, <c>saml_name_id</c>, and — with <c>RevalidateOnRefresh</c> — the upstream IdP's
    /// refresh token, a live credential for another provider. It had no <c>IFieldCipher</c>, so in the very
    /// deployment where the operator is told PII and upstream tokens are encrypted at rest, this store was the
    /// one that opted out. The adversary is the one <c>IFieldCipher</c> names: "a leaked read-only role, a
    /// copied backup, a snapshot, a support export".
    /// </remarks>
    [Fact]
    public async Task TheSessionTicketIsEncryptedAtRestWhenACipherIsRegistered()
    {
        var sessions = NewTable("sess");
        var byUser = NewTable("sessu");

        var services = new ServiceCollection();
        services.AddSingleton<IFieldCipher>(new ReversingCipher());
        var store = new TableTicketStore(
            sessions, byUser, EnvPartitioner.Live, new HttpContextAccessor(), services.BuildServiceProvider());

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "u1"), new Claim("upstream_refresh_token", "UPSTREAM-SECRET")], "cookie")),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
            "cookie");

        var key = await store.StoreAsync(ticket);

        // The stored column does not contain the plaintext the ticket serializes to.
        var row = (await sessions.GetEntityAsync<TableEntity>(
            TableTicketStore.Partition, key)).Value;
        var stored = row.GetString("Data");
        Assert.StartsWith(ReversingCipher.Marker, stored, StringComparison.Ordinal);

        // And it still round-trips, so the encryption is not write-only.
        var read = await store.RetrieveAsync(key);
        Assert.NotNull(read);
        Assert.Equal("UPSTREAM-SECRET", read!.Principal.FindFirst("upstream_refresh_token")?.Value);
    }

    /// <summary>
    /// With no cipher registered the column is unchanged, so an existing deployment reads its own rows.
    /// </summary>
    [Fact]
    public async Task WithNoCipherTheSessionTicketRoundTripsUnchanged()
    {
        var sessions = NewTable("sessp");
        var byUser = NewTable("sesspu");

        var store = new TableTicketStore(
            sessions, byUser, EnvPartitioner.Live, new HttpContextAccessor(),
            new ServiceCollection().BuildServiceProvider());

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1")], "cookie")),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
            "cookie");

        var key = await store.StoreAsync(ticket);

        var read = await store.RetrieveAsync(key);
        Assert.NotNull(read);
        Assert.Equal("u1", read!.Principal.FindFirst("sub")?.Value);
    }

    /// <summary>
    /// A visibly-transformed stand-in, so "was it protected" is decidable without a real KMS.
    /// </summary>
    private sealed class ReversingCipher : IFieldCipher
    {
        internal const string Marker = "enc:";

        public Task<string> ProtectAsync(string plaintext, CancellationToken ct = default)
            => Task.FromResult(Marker + new string(plaintext.Reverse().ToArray()));

        public Task<string> ResolveAsync(string stored, CancellationToken ct = default)
            => Task.FromResult(stored.StartsWith(Marker, StringComparison.Ordinal)
                ? new string(stored[Marker.Length..].Reverse().ToArray())
                : stored);
    }
}

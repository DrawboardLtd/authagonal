using System.Net;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Server.Services.Cluster;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

/// <summary>
/// Integration tests for POST /_internal/backchannel-logout (OIDC Back-Channel Logout 1.0):
/// the InternalEndpointGuard gate, grant revocation, and the logout_token fan-out — observed
/// with a real loopback HTTP listener, since the endpoint's "BackChannelLogout" named
/// HttpClient is unconfigured in the test host and therefore does real socket I/O.
/// NOTE: the fan-out itself is currently broken by a token-serialization bug (see the
/// KnownBug test below), so the listener asserts the absence of the call for now.
/// </summary>
public sealed class BackChannelLogoutTests : IAsyncLifetime
{
    private const string ClusterSecret = "test-cluster-secret-789";
    private const string BclClientId = "bcl-client";

    private readonly AuthagonalTestFactory _factory = new();
    private readonly BackChannelLogoutRecorder _fanOut = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Set before the host starts: the sender now runs every registered URI through the outbound SSRF
        // guard, so the loopback listener this suite used to observe the fan-out with is refused before a
        // socket is opened. The stub takes its place and additionally records what was NOT sent.
        _factory.BackChannelLogoutHttpHandler = _fanOut;
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();

        // The test factory never binds Cluster:* configuration onto ClusterOptions, so configure the
        // shared secret directly on the cached IOptions instance the endpoint resolves per request.
        // Without a secret the guard falls back to source-IP checks, and TestServer requests carry a
        // null RemoteIpAddress — which would make every request fail regardless of intent.
        _factory.Services.GetRequiredService<IOptions<ClusterOptions>>().Value.Secret = ClusterSecret;
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // Guard behaviour on the endpoint
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MissingSecretHeader_Returns404_AndRevokesNothing()
    {
        var subjectId = await SeedGrantAsync(BclClientId);

        var response = await _client.PostAsJsonAsync(
            "/_internal/backchannel-logout", new { subjectId });

        // The guard rejects with 404 (endpoint hides itself rather than advertising a 401).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var remaining = await _factory.GrantStore.GetBySubjectAsync(subjectId);
        Assert.Single(remaining);
    }

    [Fact]
    public async Task WrongSecretHeader_Returns404_AndRevokesNothing()
    {
        var subjectId = await SeedGrantAsync(BclClientId);

        var request = BuildLogoutRequest(subjectId, secret: "not-the-secret");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var remaining = await _factory.GrantStore.GetBySubjectAsync(subjectId);
        Assert.Single(remaining);
    }

    [Fact]
    public async Task WithSecret_MissingSubjectId_Returns400()
    {
        var request = BuildLogoutRequest(subjectId: "", secret: ClusterSecret);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("subject_id_required", json.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------
    // Revocation + logout_token fan-out
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WithSecret_ClientWithBackChannelUri_RevokesGrants_AndNotifies()
    {
        // Regression guard: the events member's value used to be an anonymous object (`new { }`),
        // which JsonWebTokenHandler.CreateToken cannot serialize (IDX11025) — the per-client
        // try/catch swallowed it, so every RP silently counted as `failed` and no HTTP request
        // was ever sent. The mint now uses a serializable empty dictionary; this test observes the
        // fan-out through the named client's handler and validates the logout_token shape.
        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = BclClientId,
            ClientName = "Back-Channel Client",
            RequireClientSecret = false,
            AllowedGrantTypes = ["authorization_code"],
            RedirectUris = ["https://bcl.test/callback"],
            AllowedScopes = ["openid"],
            BackChannelLogoutUri = "https://rp.example/logout",
        });

        var subjectId = Guid.NewGuid().ToString("N");
        await SeedGrantAsync(BclClientId, subjectId, type: "refresh_token");
        await SeedGrantAsync(BclClientId, subjectId, type: "consent");

        var request = BuildLogoutRequest(subjectId, ClusterSecret);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("notified").GetInt32());
        Assert.Equal(0, json.GetProperty("failed").GetInt32());
        Assert.Equal(2, json.GetProperty("grantsRevoked").GetInt32());

        // The handler saw the POST: application/x-www-form-urlencoded logout_token=...
        var (uri, body) = Assert.Single(_fanOut.Requests);
        Assert.Equal("https://rp.example/logout", uri);
        var logoutToken = System.Net.WebUtility.UrlDecode(
            Assert.Single(body.Split('&'), p => p.StartsWith("logout_token=", StringComparison.Ordinal))
                ["logout_token=".Length..]);

        // Validate the logout-token payload per the Back-Channel Logout spec.
        var parts = logoutToken.Split('.');
        Assert.Equal(3, parts.Length);
        var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(
            parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '=')));
        var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
        Assert.Equal(BclClientId, payload.GetProperty("aud").GetString());
        Assert.Equal(subjectId, payload.GetProperty("sub").GetString());
        Assert.True(payload.GetProperty("events").TryGetProperty(
            "http://schemas.openid.net/event/backchannel-logout", out _));
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("jti").GetString()));
        Assert.False(payload.TryGetProperty("nonce", out _)); // logout tokens MUST NOT contain a nonce

        var remaining = await _factory.GrantStore.GetBySubjectAsync(subjectId);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task WithSecret_ClientWithoutBackChannelUri_RevokesGrantsWithoutNotifying()
    {
        // The seeded TestClientId has no BackChannelLogoutUri
        var subjectId = await SeedGrantAsync(AuthagonalTestFactory.TestClientId);

        var request = BuildLogoutRequest(subjectId, ClusterSecret);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("notified").GetInt32());
        Assert.Equal(0, json.GetProperty("failed").GetInt32());
        Assert.Equal(1, json.GetProperty("grantsRevoked").GetInt32());

        var remaining = await _factory.GrantStore.GetBySubjectAsync(subjectId);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task WithSecret_UnreachableBackChannelUri_CountsAsFailed()
    {
        const string clientId = "bcl-unreachable";
        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = clientId,
            ClientName = "Unreachable Back-Channel Client",
            RequireClientSecret = false,
            AllowedGrantTypes = ["authorization_code"],
            RedirectUris = ["https://bcl.test/callback"],
            AllowedScopes = ["openid"],
            // Transport failure, not a mint failure and not a refusal — the handler refuses to connect.
            BackChannelLogoutUri = "https://unreachable.example/logout",
        });
        var subjectId = await SeedGrantAsync(clientId);

        var response = await _client.SendAsync(BuildLogoutRequest(subjectId, ClusterSecret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("notified").GetInt32());
        Assert.Equal(1, json.GetProperty("failed").GetInt32());
        Assert.Empty(await _factory.GrantStore.GetBySubjectAsync(subjectId)); // revocation unaffected
    }

    /// <summary>
    /// A logout URI pointing into the deployment's own network is not POSTed at all.
    /// </summary>
    /// <remarks>
    /// Dynamic registration validates this URI where it is written, which does nothing for the URIs that
    /// never went through it: seeded clients, the Duende migration, admin writes, and anything registered
    /// before that check existed. The sink is a server-initiated POST to a stored, caller-chosen target
    /// whose response never comes back to the caller — a blind SSRF primitive aimed at the cloud metadata
    /// service or an unauthenticated internal admin API. The assertion that matters is
    /// <c>Assert.Empty(_fanOut.Requests)</c>: nothing left the process.
    /// </remarks>
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:9200/_shutdown")]
    [InlineData("http://10.0.0.7/internal/logout")]
    [InlineData("http://es.internal:9200/_shutdown")]
    public async Task WithSecret_InternalBackChannelUri_IsRefusedAtSendTime(string uri)
    {
        var clientId = $"bcl-internal-{Guid.NewGuid():N}";
        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = clientId,
            ClientName = "Internally-Pointed Client",
            RequireClientSecret = false,
            AllowedGrantTypes = ["authorization_code"],
            RedirectUris = ["https://bcl.test/callback"],
            AllowedScopes = ["openid"],
            BackChannelLogoutUri = uri,
        });
        var subjectId = await SeedGrantAsync(clientId);

        var response = await _client.SendAsync(BuildLogoutRequest(subjectId, ClusterSecret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("notified").GetInt32());
        Assert.Equal(1, json.GetProperty("failed").GetInt32());
        Assert.Empty(_fanOut.Requests);
        // The user is still logged out — the refusal is about where the notification goes, not whether
        // the session ends.
        Assert.Empty(await _factory.GrantStore.GetBySubjectAsync(subjectId));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<string> SeedGrantAsync(
        string clientId, string? subjectId = null, string type = "refresh_token")
    {
        subjectId ??= Guid.NewGuid().ToString("N");
        await _factory.GrantStore.StoreAsync(new PersistedGrant
        {
            Key = Guid.NewGuid().ToString("N"),
            Type = type,
            SubjectId = subjectId,
            ClientId = clientId,
            Data = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        return subjectId;
    }

    private static HttpRequestMessage BuildLogoutRequest(string subjectId, string? secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/_internal/backchannel-logout")
        {
            Content = JsonContent.Create(new { subjectId }),
        };
        if (secret is not null)
            request.Headers.Add(InternalEndpointGuard.SecretHeader, secret);
        return request;
    }
}

/// <summary>
/// Direct unit tests for InternalEndpointGuard: shared-secret comparison when configured,
/// and the loopback/private-address fallback when no secret is set.
/// </summary>
/// <summary>
/// <c>/_internal/backchannel-logout</c> revokes every grant for an arbitrary subject, so its guard is
/// load-bearing. It used to fall back to "the source address looks private" whenever
/// <c>Cluster:Secret</c> was unset, reading <c>Connection.RemoteIpAddress</c> — which
/// <c>UseForwardedHeaders</c> has already OVERWRITTEN from the client-supplied <c>X-Forwarded-For</c>.
/// With the trust set defaulting to empty (meaning every caller is a trusted proxy), any internet client
/// could send <c>X-Forwarded-For: 10.0.0.1</c> and pass: remote unauthenticated mass session destruction.
/// Four independent review findings landed here.
///
/// Two behaviour changes are pinned below. The guard now reads the RAW peer address captured before
/// forwarded headers are applied, and a private-range peer is no longer sufficient on its own — in a
/// shared cluster network that trusts every neighbouring workload, which is exactly what the forged
/// header impersonated. Loopback remains allowed for single-node development.
/// </summary>
public sealed class InternalEndpointGuardTests
{
    private const string Secret = "guard-secret-123";

    private static DefaultHttpContext Context(
        string? rawPeer = null,
        string? effectivePeer = null,
        string? forwardedFor = null,
        string? headerValue = null,
        bool stashRawPeer = true)
    {
        var ctx = new DefaultHttpContext();
        // effectivePeer models what UseForwardedHeaders leaves in RemoteIpAddress; rawPeer is the socket.
        var effective = effectivePeer ?? rawPeer;
        if (effective is not null)
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(effective);
        if (stashRawPeer && rawPeer is not null)
            ctx.Items[InternalEndpointGuard.RawPeerAddressItem] = IPAddress.Parse(rawPeer);
        if (forwardedFor is not null)
            ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (headerValue is not null)
            ctx.Request.Headers[InternalEndpointGuard.SecretHeader] = headerValue;
        return ctx;
    }

    [Fact]
    public void SecretConfigured_MatchingHeader_Authorized()
        => Assert.True(InternalEndpointGuard.IsAuthorized(Context(headerValue: Secret), Secret));

    [Fact]
    public void SecretConfigured_WrongHeader_Rejected()
        => Assert.False(InternalEndpointGuard.IsAuthorized(Context(headerValue: "wrong"), Secret));

    [Fact]
    public void SecretConfigured_MissingHeader_Rejected()
        => Assert.False(InternalEndpointGuard.IsAuthorized(Context(), Secret));

    [Fact]
    public void SecretConfigured_LoopbackWithoutHeader_Rejected()
    {
        // Once a secret is configured, the IP fallback no longer applies — even loopback
        // callers must present the header.
        Assert.False(InternalEndpointGuard.IsAuthorized(Context(rawPeer: "127.0.0.1"), Secret));
    }

    /// <summary>A correct secret is a credential and works from anywhere, including a public peer.</summary>
    [Fact]
    public void SecretConfigured_PublicPeerWithCorrectHeader_Authorized()
        => Assert.True(InternalEndpointGuard.IsAuthorized(
            Context(rawPeer: "203.0.113.7", headerValue: Secret), Secret));

    [Theory]
    [InlineData("127.0.0.1")] // IPv4 loopback
    [InlineData("::1")]       // IPv6 loopback
    public void NoSecret_Loopback_Authorized(string ip)
        => Assert.True(InternalEndpointGuard.IsAuthorized(Context(rawPeer: ip), secret: null));

    /// <summary>
    /// CHANGED BEHAVIOUR: a private-range peer no longer authorizes on its own. Previously every address
    /// below was accepted, which is what made the forged-header bypass effective — the attacker only had
    /// to name one of them.
    /// </summary>
    [Theory]
    [InlineData("10.1.2.3")]        // RFC1918 10/8
    [InlineData("172.16.0.1")]      // RFC1918 172.16/12 lower bound
    [InlineData("172.31.255.254")]  // RFC1918 172.16/12 upper bound
    [InlineData("192.168.1.1")]     // RFC1918 192.168/16
    [InlineData("169.254.10.10")]   // IPv4 link-local (also the cloud metadata range)
    [InlineData("fd00::1")]         // IPv6 unique local fc00::/7
    [InlineData("fe80::1")]         // IPv6 link-local
    [InlineData("::ffff:10.0.0.1")] // IPv4-mapped IPv6, private
    public void NoSecret_PrivateAddress_Rejected(string ip)
        => Assert.False(InternalEndpointGuard.IsAuthorized(Context(rawPeer: ip), secret: null));

    [Theory]
    [InlineData("8.8.8.8")]              // public IPv4
    [InlineData("172.32.0.1")]           // just outside 172.16/12
    [InlineData("2001:4860:4860::8888")] // public IPv6
    [InlineData("::ffff:8.8.8.8")]       // IPv4-mapped IPv6, public
    public void NoSecret_PublicAddress_Rejected(string ip)
        => Assert.False(InternalEndpointGuard.IsAuthorized(Context(rawPeer: ip), secret: null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoSecret_NullRemoteIp_Rejected(string? secret)
        => Assert.False(InternalEndpointGuard.IsAuthorized(Context(), secret));

    /// <summary>
    /// The original bypass, end to end: a public peer claims a private address, the forwarded middleware
    /// rewrites RemoteIpAddress to it, and the guard used to trust the result.
    /// </summary>
    [Fact]
    public void NoSecret_SpoofedForwardedFor_Rejected()
        => Assert.False(InternalEndpointGuard.IsAuthorized(
            Context(rawPeer: "203.0.113.7", effectivePeer: "10.0.0.1", forwardedFor: "10.0.0.1"),
            secret: null));

    /// <summary>Claiming loopback via a header from a non-loopback peer must not pass either.</summary>
    [Fact]
    public void NoSecret_SpoofedLoopbackHeader_Rejected()
        => Assert.False(InternalEndpointGuard.IsAuthorized(
            Context(rawPeer: "203.0.113.7", effectivePeer: "127.0.0.1", forwardedFor: "127.0.0.1"),
            secret: null));

    /// <summary>
    /// If the capture middleware did not run and a forwarded header is present, the guard cannot
    /// distinguish a genuine peer from a rewritten one, so it refuses rather than guessing.
    /// </summary>
    [Fact]
    public void NoSecret_MissingRawPeerWithForwardedHeader_Rejected()
        => Assert.False(InternalEndpointGuard.IsAuthorized(
            Context(effectivePeer: "127.0.0.1", forwardedFor: "127.0.0.1", stashRawPeer: false),
            secret: null));

    /// <summary>Without any forwarded header the live connection address is still trustworthy.</summary>
    [Fact]
    public void NoSecret_MissingRawPeerNoForwardedHeader_FallsBackToConnection()
        => Assert.True(InternalEndpointGuard.IsAuthorized(
            Context(effectivePeer: "127.0.0.1", stashRawPeer: false), secret: null));
}

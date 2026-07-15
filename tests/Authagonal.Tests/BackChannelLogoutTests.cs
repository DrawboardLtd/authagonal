using System.Net;
using System.Net.Sockets;
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
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
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
        // was ever sent. The mint now uses a serializable empty dictionary; this test observes
        // the real fan-out via a loopback listener and validates the logout_token shape.
        await using var capture = new CaptureServer();

        await _factory.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = BclClientId,
            ClientName = "Back-Channel Client",
            RequireClientSecret = false,
            AllowedGrantTypes = ["authorization_code"],
            RedirectUris = ["https://bcl.test/callback"],
            AllowedScopes = ["openid"],
            BackChannelLogoutUri = $"http://127.0.0.1:{capture.Port}/logout",
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

        // The listener saw the real POST: application/x-www-form-urlencoded logout_token=...
        var body = await capture.Body.WaitAsync(TimeSpan.FromSeconds(10));
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
            // A loopback port nothing listens on — connection refused, not a mint failure.
            BackChannelLogoutUri = "http://127.0.0.1:1/logout",
        });
        var subjectId = await SeedGrantAsync(clientId);

        var response = await _client.SendAsync(BuildLogoutRequest(subjectId, ClusterSecret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("notified").GetInt32());
        Assert.Equal(1, json.GetProperty("failed").GetInt32());
        Assert.Empty(await _factory.GrantStore.GetBySubjectAsync(subjectId)); // revocation unaffected
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

    /// <summary>
    /// Minimal single-request loopback HTTP server: captures the request body of one POST
    /// and answers 200. Used to observe the endpoint's real logout_token fan-out, because
    /// the "BackChannelLogout" named client has no test-host handler substitution seam.
    /// </summary>
    private sealed class CaptureServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serve;
        private readonly TaskCompletionSource<string> _body =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Port { get; }
        public Task<string> Body => _body.Task;

        public CaptureServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = ServeOneAsync();
        }

        private async Task ServeOneAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();

                var buffer = new byte[64 * 1024];
                var total = 0;
                int headerEnd;
                while ((headerEnd = FindHeaderEnd(buffer, total)) < 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total));
                    if (read == 0) throw new IOException("Connection closed before headers completed");
                    total += read;
                }

                var headers = Encoding.ASCII.GetString(buffer, 0, headerEnd);
                var contentLength = 0;
                foreach (var line in headers.Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                }

                var bodyStart = headerEnd + 4;
                while (total - bodyStart < contentLength)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total));
                    if (read == 0) break;
                    total += read;
                }

                var body = Encoding.UTF8.GetString(
                    buffer, bodyStart, Math.Min(contentLength, total - bodyStart));

                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response);

                _body.TrySetResult(body);
            }
            catch (Exception ex)
            {
                _body.TrySetException(ex);
            }
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (var i = 3; i < length; i++)
            {
                if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' &&
                    buffer[i - 1] == '\r' && buffer[i] == '\n')
                    return i - 3;
            }
            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _serve; } catch { /* listener torn down */ }
        }
    }
}

/// <summary>
/// Direct unit tests for InternalEndpointGuard: shared-secret comparison when configured,
/// and the loopback/private-address fallback when no secret is set.
/// </summary>
public sealed class InternalEndpointGuardTests
{
    private const string Secret = "guard-secret-123";

    private static DefaultHttpContext Context(string? remoteIp = null, string? headerValue = null)
    {
        var ctx = new DefaultHttpContext();
        if (remoteIp is not null)
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
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
        Assert.False(InternalEndpointGuard.IsAuthorized(Context(remoteIp: "127.0.0.1"), Secret));
    }

    [Theory]
    [InlineData("127.0.0.1")]      // IPv4 loopback
    [InlineData("::1")]            // IPv6 loopback
    [InlineData("10.1.2.3")]       // RFC1918 10/8
    [InlineData("172.16.0.1")]     // RFC1918 172.16/12 lower bound
    [InlineData("172.31.255.254")] // RFC1918 172.16/12 upper bound
    [InlineData("192.168.1.1")]    // RFC1918 192.168/16
    [InlineData("169.254.10.10")]  // IPv4 link-local
    [InlineData("fd00::1")]        // IPv6 unique local fc00::/7
    [InlineData("fe80::1")]        // IPv6 link-local
    [InlineData("::ffff:10.0.0.1")] // IPv4-mapped IPv6, private
    public void NoSecret_InternalAddress_Authorized(string ip)
        => Assert.True(InternalEndpointGuard.IsAuthorized(Context(remoteIp: ip), secret: null));

    [Theory]
    [InlineData("8.8.8.8")]              // public IPv4
    [InlineData("172.32.0.1")]           // just outside 172.16/12
    [InlineData("2001:4860:4860::8888")] // public IPv6
    [InlineData("::ffff:8.8.8.8")]       // IPv4-mapped IPv6, public
    public void NoSecret_PublicAddress_Rejected(string ip)
        => Assert.False(InternalEndpointGuard.IsAuthorized(Context(remoteIp: ip), secret: null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoSecret_NullRemoteIp_Rejected(string? secret)
        => Assert.False(InternalEndpointGuard.IsAuthorized(Context(), secret));
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Four token-path gaps, each a control applied on a sibling path and dropped on this one.
/// </summary>
public sealed class ProtocolLowFindingTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // The grace window is off by default (RefreshTokenReuseGraceSeconds = 0), so a retry never enters the
        // path under test — the same thing that made an earlier grace-window fix look covered when it was not.
        // Set before the host is built, since options are bound once.
        _factory.ConfigureAuthOptions = o => o.RefreshTokenReuseGraceSeconds = 30;
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();

        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        client!.AllowedScopes = ["openid", "profile", "email", "roles", "groups", "offline_access"];
        client.AllowOfflineAccess = true;
        await _factory.ClientStore.UpsertAsync(client);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // ── #6: roles and groups are scope-gated on the access token ─────────────

    /// <summary>
    /// <c>roles</c> and <c>groups</c> were written to the access token unconditionally.
    /// </summary>
    /// <remarks>
    /// Immediately above the scope-gated block that carries the OIDC §5.4 claims, and while the id_token gated
    /// the same two and both userinfo endpoints gate them at read time. So <c>scope=openid profile</c> released
    /// the subject's full role and group membership to whatever resource server received the token: a client
    /// whose AllowedScopes cannot include <c>roles</c> could not ask for them, the consent screen never
    /// mentioned them, and the token carried them anyway.
    /// </remarks>
    [Fact]
    public async Task RolesAndGroupsAreAbsentFromTheAccessTokenWithoutTheirScopes()
    {
        await SeedUserWithRoleAsync();

        var payload = PayloadOf((await GetTokensAsync("openid profile")).GetProperty("access_token").GetString()!);

        Assert.False(payload.TryGetProperty("roles", out _), "roles rode an access token that never asked");
        Assert.False(payload.TryGetProperty("groups", out _), "groups rode an access token that never asked");
    }

    [Fact]
    public async Task RolesArePresentOnTheAccessTokenWhenTheScopeIsGranted()
    {
        await SeedUserWithRoleAsync();

        var payload = PayloadOf((await GetTokensAsync("openid profile roles")).GetProperty("access_token").GetString()!);

        Assert.True(payload.TryGetProperty("roles", out var roles));
        Assert.Contains("auditor", roles.EnumerateArray().Select(r => r.GetString()));
    }

    /// <summary>The id_token gated them all along, so the two must now agree.</summary>
    [Fact]
    public async Task TheAccessTokenAndIdTokenAgreeOnWhetherRolesWereReleased()
    {
        await SeedUserWithRoleAsync();
        var tokens = await GetTokensAsync("openid profile");

        var access = PayloadOf(tokens.GetProperty("access_token").GetString()!);
        var id = PayloadOf(tokens.GetProperty("id_token").GetString()!);

        Assert.Equal(access.TryGetProperty("roles", out _), id.TryGetProperty("roles", out _));
    }

    // ── #7: the grace-window retry re-derives the subject ────────────────────

    /// <summary>
    /// The grace-window retry minted from the snapshot frozen at the last rotation.
    /// </summary>
    /// <remarks>
    /// The normal rotation path re-reads the user store on every refresh — that is where deactivating an
    /// account, revoking a role or an ended upstream session actually takes effect. The grace path, taken when
    /// an already-consumed refresh token is presented within <c>RefreshTokenReuseGraceSeconds</c>, minted
    /// directly from <c>data.Subject</c> and did neither. It still replays the same successor grant, which is
    /// what makes the retry idempotent; only the authorization facts are refreshed.
    /// </remarks>
    [Fact]
    public async Task TheGraceWindowRetryRefusesADeactivatedSubject()
    {
        await SeedUserWithRoleAsync();
        var tokens = await GetTokensAsync("openid profile offline_access");
        var refresh = tokens.GetProperty("refresh_token").GetString()!;

        // Rotate once, so presenting the original again lands in the grace window.
        var rotated = await RefreshAsync(refresh);
        rotated.EnsureSuccessStatusCode();

        var user = Assert.Single(await _factory.UserStore.SearchAsync("test@example.com"));
        user.IsActive = false;
        await _factory.UserStore.UpdateAsync(user);

        // The retry is inside the grace window, so it would previously have been served from the snapshot.
        var retry = await RefreshAsync(refresh);
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
    }

    /// <summary>The control: an unchanged subject is still served the idempotent retry.</summary>
    [Fact]
    public async Task TheGraceWindowRetryStillServesAnUnchangedSubject()
    {
        await SeedUserWithRoleAsync();
        var tokens = await GetTokensAsync("openid profile offline_access");
        var refresh = tokens.GetProperty("refresh_token").GetString()!;

        var first = await RefreshAsync(refresh);
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();

        var retry = await RefreshAsync(refresh);
        retry.EnsureSuccessStatusCode();
        var retryBody = await retry.Content.ReadFromJsonAsync<JsonElement>();

        // Same successor handle — the retry replays rather than rotating again.
        Assert.Equal(
            firstBody.GetProperty("refresh_token").GetString(),
            retryBody.GetProperty("refresh_token").GetString());
    }

    // ── #33: an unbounded day count became a 500 ─────────────────────────────

    /// <summary>
    /// <c>expiresInDays</c> was unbounded, so a large value threw out of <c>DateTimeOffset.AddDays</c>.
    /// </summary>
    /// <remarks>
    /// The throw became a 500 with no indication of which field was wrong and no audit row — the audit call is
    /// after the store write. Both sibling admin endpoints taking a day count bound it.
    /// </remarks>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(4000)]
    [InlineData(-1)]
    public async Task AnOutOfRangeScimTokenLifetimeIsRefusedNotThrown(int days)
    {
        var admin = await _factory.GetAdminTokenAsync(_client);
        await _factory.ClientStore.UpsertAsync(new Authagonal.Core.Models.OAuthClient
        {
            ClientId = "hr-sync", ClientName = "HR", RequireClientSecret = false,
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scim/tokens")
        {
            Content = JsonContent.Create(new { clientId = "hr-sync", expiresInDays = days }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("expiresInDays", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReasonableScimTokenLifetimeIsAccepted()
    {
        var admin = await _factory.GetAdminTokenAsync(_client);
        await _factory.ClientStore.UpsertAsync(new Authagonal.Core.Models.OAuthClient
        {
            ClientId = "hr-sync", ClientName = "HR", RequireClientSecret = false,
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scim/tokens")
        {
            Content = JsonContent.Create(new { clientId = "hr-sync", expiresInDays = 365 }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);

        Assert.True((await _client.SendAsync(request)).IsSuccessStatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task SeedUserWithRoleAsync()
    {
        var user = await _factory.SeedTestUserAsync();
        user.Roles = ["auditor"];
        await _factory.UserStore.UpdateAsync(user);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
        }));

    private async Task<JsonElement> GetTokensAsync(string scope)
    {
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var authorize = await _client.GetAsync(
            $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            $"&response_type=code&scope={Uri.EscapeDataString(scope)}" +
            $"&state=test&code_challenge={challenge}&code_challenge_method=S256");
        var code = HttpUtility.ParseQueryString(authorize.Headers.Location!.Query)["code"]!;

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "https://app.test/callback",
                ["code_verifier"] = verifier,
                ["client_id"] = AuthagonalTestFactory.TestClientId,
            }));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement PayloadOf(string jwt)
    {
        var part = jwt.Split('.')[1];
        var padded = part.Replace('-', '+').Replace('_', '/')
            .PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement.Clone();
    }
}

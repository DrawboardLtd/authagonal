using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Admin-surface responses an automated caller has to be able to act on, and the bounds on what one
/// request may ask for.
/// </summary>
public sealed class AdminSurfaceHardeningTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _adminToken = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // F347 — one request must not be able to ask for the whole directory
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UserSearch_ClampsMaxResults()
    {
        for (var i = 0; i < 8; i++)
            await _factory.SeedTestUserAsync($"clamped{i}@example.com", "Test1234!");

        // maxResults reached the store unbounded, so a single admin-scoped request could pull the
        // entire user table — a memory and egress amplifier over every user's PII, and the first
        // request a compromised admin token makes.
        var response = await Send(HttpMethod.Get, "/api/v1/profile/search?q=clamped&maxResults=100000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("users").GetArrayLength() <= 200);
    }

    // -----------------------------------------------------------------------
    // F334 — an authorization failure is not a login challenge
    // -----------------------------------------------------------------------
    //
    // Not asserted here: the shipped IClientScopeGuard grants every scope to any authenticated admin
    // (it is the single-role default), so there is no request that reaches the denial branch in this
    // harness. The fix — a JSON 403 naming the scope, instead of Results.Forbid() running the cookie
    // scheme's forbid handler and answering a 302 to /login — is exercised only by a host that
    // registers a real scope hierarchy. Recorded rather than covered by a test that would have to
    // stub the guard and would then be testing the stub.

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return _client.SendAsync(request);
    }
}

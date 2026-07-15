using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

// ---------------------------------------------------------------------------------------------
// Scope admin CRUD (ScopeEndpoints) — runs on the stock AuthagonalTestFactory with a real admin
// bearer token, exactly like AdminRoleEndpointTests.
// ---------------------------------------------------------------------------------------------
public sealed class AdminScopeAndProvisioningEndpointTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _adminToken = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/scopes — ListScopes
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListScopes_Empty_ReturnsEmptyList()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Get, "/api/v1/scopes/"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("scopes").GetArrayLength());
    }

    [Fact]
    public async Task ListScopes_ReturnsCreatedScopes()
    {
        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scopes/", new { name = "api.read" }));
        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scopes/", new { name = "api.write" }));

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Get, "/api/v1/scopes/"));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var names = json.GetProperty("scopes").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Contains("api.read", names);
        Assert.Contains("api.write", names);
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/scopes — CreateScope
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CreateScope_Valid_Returns201WithDefaults()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scopes/", new
        {
            name = "api.full",
            displayName = "Full API",
            description = "Everything",
            userClaims = new[] { "email" },
        }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/scopes/api.full", response.Headers.Location?.ToString());

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("api.full", json.GetProperty("name").GetString());
        Assert.Equal("Full API", json.GetProperty("displayName").GetString());
        Assert.False(json.GetProperty("emphasize").GetBoolean());       // default
        Assert.False(json.GetProperty("required").GetBoolean());        // default
        Assert.True(json.GetProperty("showInDiscoveryDocument").GetBoolean()); // default true
        Assert.Equal("email", json.GetProperty("userClaims")[0].GetString());
    }

    [Fact]
    public async Task CreateScope_MissingName_Returns400()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scopes/", new
        {
            displayName = "Nameless",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreateScope_Duplicate_Returns409()
    {
        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scopes/", new { name = "dupe.scope" }));
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scopes/", new { name = "dupe.scope" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("scope_exists", json.GetProperty("error").GetString());
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/scopes/{name} — GetScope
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetScope_Existing_ReturnsScope()
    {
        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scopes/", new { name = "gettable" }));

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Get, "/api/v1/scopes/gettable"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gettable", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetScope_Unknown_Returns404()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Get, "/api/v1/scopes/never-created"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // PUT /api/v1/scopes/{name} — UpdateScope
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UpdateScope_PartialUpdate_OnlyChangesSuppliedFields()
    {
        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scopes/", new
        {
            name = "partial",
            displayName = "Before",
            description = "Original description",
        }));

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Put, "/api/v1/scopes/partial", new
        {
            displayName = "After",
            emphasize = true,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("After", json.GetProperty("displayName").GetString());
        Assert.True(json.GetProperty("emphasize").GetBoolean());
        Assert.Equal("Original description", json.GetProperty("description").GetString()); // untouched
        Assert.NotEqual(JsonValueKind.Null, json.GetProperty("updatedAt").ValueKind);      // stamped
    }

    [Fact]
    public async Task UpdateScope_Unknown_Returns404()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Put, "/api/v1/scopes/ghost", new
        {
            displayName = "Ghost",
        }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // DELETE /api/v1/scopes/{name} — DeleteScope
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeleteScope_Existing_Returns204AndRemoves()
    {
        await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scopes/", new { name = "temp" }));

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Delete, "/api/v1/scopes/temp"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var get = await _client.SendAsync(AdminRequest(HttpMethod.Get, "/api/v1/scopes/temp"));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task DeleteScope_Unknown_Returns404()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Delete, "/api/v1/scopes/ghost"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Unauthenticated callers rejected — real JWT-bearer pipeline (authorization
    // short-circuits before parameter binding, so this also covers the
    // provisioning routes whose handler deps aren't registered in this factory).
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/v1/scopes/")]
    [InlineData("GET", "/api/v1/scopes/openid")]
    [InlineData("POST", "/api/v1/scopes/")]
    [InlineData("PUT", "/api/v1/scopes/openid")]
    [InlineData("DELETE", "/api/v1/scopes/openid")]
    [InlineData("GET", "/api/v1/provisioning/apps/")]
    [InlineData("POST", "/api/v1/provisioning/apps/")]
    [InlineData("PUT", "/api/v1/provisioning/apps/some-app")]
    [InlineData("DELETE", "/api/v1/provisioning/apps/some-app")]
    [InlineData("POST", "/api/v1/provisioning/apps/some-app/test")]
    public async Task ScopeAndProvisioningEndpoints_NoToken_Returns401(string method, string url)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

// ---------------------------------------------------------------------------------------------
// Provisioning app admin CRUD + /test (ProvisioningEndpoints) — runs on the bespoke
// AdminSurfaceHost (see the note on it in AdminClientEndpointTests.cs: the stock factory has no
// IProvisioningAppStore/IProvisioningAppQuota/IAuditLogger, so these routes can't bind there).
// ---------------------------------------------------------------------------------------------
public sealed class AdminScopeAndProvisioningEndpointTests_Provisioning : IAsyncLifetime
{
    private readonly AdminSurfaceHost _host = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _host.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static StringContent Json(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task<string> CreateAppAsync(object? body = null)
    {
        var response = await _client.PostAsync("/api/v1/provisioning/apps/",
            Json(body ?? new { name = "App", callbackUrl = "https://hooks.example.com/prov" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("appId").GetString()!;
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/provisioning/apps — ListApps
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListApps_Empty_ReturnsEmptyListAndNullLimit()
    {
        var response = await _client.GetAsync("/api/v1/provisioning/apps/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("apps").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("limit").ValueKind);
    }

    [Fact]
    public async Task ListApps_ReturnsViewsWithoutApiKeyValue()
    {
        await CreateAppAsync(new { name = "Keyed", callbackUrl = "https://hooks.example.com/a", apiKey = "super-secret" });

        var response = await _client.GetAsync("/api/v1/provisioning/apps/");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var app = json.GetProperty("apps")[0];

        Assert.Equal("Keyed", app.GetProperty("name").GetString());
        Assert.True(app.GetProperty("hasApiKey").GetBoolean());
        Assert.DoesNotContain("super-secret", await response.Content.ReadAsStringAsync()); // key never echoed
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/provisioning/apps — CreateApp
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CreateApp_Valid_ReturnsView()
    {
        var response = await _client.PostAsync("/api/v1/provisioning/apps/", Json(new
        {
            name = "  My App  ",
            callbackUrl = " https://hooks.example.com/prov ",
            apiKey = "key-1",
            tryTimeoutSeconds = 30,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("My App", json.GetProperty("name").GetString());                       // trimmed
        Assert.Equal("https://hooks.example.com/prov", json.GetProperty("callbackUrl").GetString());
        Assert.True(json.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(30, json.GetProperty("tryTimeoutSeconds").GetInt32());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("appId").GetString()));
    }

    [Theory]
    [InlineData(2, 5)]     // below the 5s floor → clamped up
    [InlineData(999, 300)] // above the 300s ceiling → clamped down
    public async Task CreateApp_TimeoutClampedToUsefulRange(int requested, int expected)
    {
        var response = await _client.PostAsync("/api/v1/provisioning/apps/", Json(new
        {
            name = "Clamp",
            callbackUrl = "https://hooks.example.com/c",
            tryTimeoutSeconds = requested,
        }));

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, json.GetProperty("tryTimeoutSeconds").GetInt32());
    }

    [Theory]
    [InlineData(null, "https://hooks.example.com/x")]         // missing name
    [InlineData("App", null)]                                  // missing callbackUrl
    [InlineData("App", "not-a-url")]                           // not absolute
    [InlineData("App", "ftp://hooks.example.com/x")]           // wrong scheme
    [InlineData("App", "https://localhost/x")]                 // internal host (SSRF guard)
    [InlineData("App", "http://169.254.169.254/latest")]       // metadata IP (SSRF guard)
    public async Task CreateApp_InvalidRequest_Returns400(string? name, string? callbackUrl)
    {
        var response = await _client.PostAsync("/api/v1/provisioning/apps/",
            Json(new { name, callbackUrl }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreateApp_OverQuota_Returns400ProvisioningAppLimit()
    {
        await using var limited = new AdminSurfaceHost { MaxProvisioningApps = 1 };
        var client = limited.CreateClient();

        var first = await client.PostAsync("/api/v1/provisioning/apps/",
            Json(new { name = "One", callbackUrl = "https://hooks.example.com/1" }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsync("/api/v1/provisioning/apps/",
            Json(new { name = "Two", callbackUrl = "https://hooks.example.com/2" }));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var json = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("provisioning_app_limit", json.GetProperty("error").GetString());
    }

    // -------------------------------------------------------------------------
    // PUT /api/v1/provisioning/apps/{appId} — UpdateApp
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UpdateApp_Unknown_Returns404AppNotFound()
    {
        var response = await _client.PutAsync("/api/v1/provisioning/apps/ghost",
            Json(new { name = "Ghost", callbackUrl = "https://hooks.example.com/g" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("app_not_found", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UpdateApp_ApiKeySemantics_NullKeepsEmptyClearsValueReplaces()
    {
        var appId = await CreateAppAsync(new { name = "K", callbackUrl = "https://hooks.example.com/k", apiKey = "k1" });

        // null (omitted) → unchanged
        var r1 = await _client.PutAsync($"/api/v1/provisioning/apps/{appId}",
            Json(new { name = "K", callbackUrl = "https://hooks.example.com/k" }));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal("k1", (await _host.ProvisioningAppStore.GetAsync(appId))!.ApiKey);

        // value → replaced
        var r2 = await _client.PutAsync($"/api/v1/provisioning/apps/{appId}",
            Json(new { name = "K", callbackUrl = "https://hooks.example.com/k", apiKey = "k2" }));
        var v2 = await r2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(v2.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal("k2", (await _host.ProvisioningAppStore.GetAsync(appId))!.ApiKey);

        // empty string → cleared
        var r3 = await _client.PutAsync($"/api/v1/provisioning/apps/{appId}",
            Json(new { name = "K", callbackUrl = "https://hooks.example.com/k", apiKey = "" }));
        var v3 = await r3.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(v3.GetProperty("hasApiKey").GetBoolean());
        Assert.Null((await _host.ProvisioningAppStore.GetAsync(appId))!.ApiKey);
    }

    [Fact]
    public async Task UpdateApp_InvalidCallbackUrl_Returns400()
    {
        var appId = await CreateAppAsync();

        var response = await _client.PutAsync($"/api/v1/provisioning/apps/{appId}",
            Json(new { name = "App", callbackUrl = "https://localhost/internal" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // DELETE /api/v1/provisioning/apps/{appId} — DeleteApp
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeleteApp_Existing_ReturnsRemovedTrue()
    {
        var appId = await CreateAppAsync();

        var response = await _client.DeleteAsync($"/api/v1/provisioning/apps/{appId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("removed").GetBoolean());
        Assert.Null(await _host.ProvisioningAppStore.GetAsync(appId));
    }

    [Fact]
    public async Task DeleteApp_Unknown_Returns404AppNotFound()
    {
        var response = await _client.DeleteAsync("/api/v1/provisioning/apps/ghost");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("app_not_found", json.GetProperty("error").GetString());
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/provisioning/apps/{appId}/test — TestApp
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TestApp_Unknown_Returns404AppNotFound()
    {
        var response = await _client.PostAsync("/api/v1/provisioning/apps/ghost/test", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("app_not_found", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TestApp_PostsTestPayloadToCallbackTryUrl_WithBearerKey()
    {
        var handler = new RecordingProvisioningHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("callback says hi") },
        };
        await using var host = new AdminSurfaceHost { ProvisioningHttpHandler = handler };
        var client = host.CreateClient();

        await host.ProvisioningAppStore.UpsertAsync(new ProvisioningAppConfig
        {
            AppId = "app-t",
            Name = "Testable",
            CallbackUrl = "https://hooks.example.com/prov/",
            ApiKey = "bearer-key-1",
        });

        var response = await client.PostAsync("/api/v1/provisioning/apps/app-t/test", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(200, json.GetProperty("statusCode").GetInt32());
        Assert.Equal("callback says hi", json.GetProperty("body").GetString());

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://hooks.example.com/prov/try", sent.Url); // trailing slash trimmed, /try appended
        Assert.Equal("Bearer bearer-key-1", sent.Authorization);
        Assert.Contains("test-user", sent.Body);
        Assert.Contains("test@example.com", sent.Body);
    }

    [Fact]
    public async Task TestApp_CallbackReturnsError_ReportsFailureWithStatus()
    {
        var handler = new RecordingProvisioningHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") },
        };
        await using var host = new AdminSurfaceHost { ProvisioningHttpHandler = handler };
        var client = host.CreateClient();

        await host.ProvisioningAppStore.UpsertAsync(new ProvisioningAppConfig
        {
            AppId = "app-e",
            Name = "Erroring",
            CallbackUrl = "https://hooks.example.com/prov",
        });

        var response = await client.PostAsync("/api/v1/provisioning/apps/app-e/test", null);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal(500, json.GetProperty("statusCode").GetInt32());
        Assert.Equal("boom", json.GetProperty("body").GetString());
    }

    [Fact]
    public async Task TestApp_ConnectionFailure_ReportsFailureCleanly()
    {
        var handler = new RecordingProvisioningHandler { Throws = new HttpRequestException("connection refused") };
        await using var host = new AdminSurfaceHost { ProvisioningHttpHandler = handler };
        var client = host.CreateClient();

        await host.ProvisioningAppStore.UpsertAsync(new ProvisioningAppConfig
        {
            AppId = "app-x",
            Name = "Unreachable",
            CallbackUrl = "https://hooks.example.com/prov",
        });

        var response = await client.PostAsync("/api/v1/provisioning/apps/app-x/test", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal(0, json.GetProperty("statusCode").GetInt32());
        Assert.Equal("connection refused", json.GetProperty("body").GetString());
    }

    [Fact]
    public async Task TestApp_StoredUnsafeCallbackUrl_RefusesWithoutCalling()
    {
        var handler = new RecordingProvisioningHandler();
        await using var host = new AdminSurfaceHost { ProvisioningHttpHandler = handler };
        var client = host.CreateClient();

        // Simulate a legacy/directly-seeded row the create-time validation never saw.
        await host.ProvisioningAppStore.UpsertAsync(new ProvisioningAppConfig
        {
            AppId = "app-bad",
            Name = "Internal",
            CallbackUrl = "http://localhost:8080/hook",
        });

        var response = await client.PostAsync("/api/v1/provisioning/apps/app-bad/test", null);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Contains("not an allowed external host", json.GetProperty("body").GetString());
        Assert.Empty(handler.Requests); // SSRF guard fired before any outbound call
    }

    // -------------------------------------------------------------------------
    // Unauthenticated callers rejected (bespoke host scheme)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/v1/provisioning/apps/")]
    [InlineData("POST", "/api/v1/provisioning/apps/")]
    [InlineData("PUT", "/api/v1/provisioning/apps/some-app")]
    [InlineData("DELETE", "/api/v1/provisioning/apps/some-app")]
    [InlineData("POST", "/api/v1/provisioning/apps/some-app/test")]
    public async Task ProvisioningEndpoints_NoAuth_Returns401(string method, string url)
    {
        var anonymous = _host.CreateClient(authenticated: false);
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT")
            request.Content = Json(new { name = "X", callbackUrl = "https://hooks.example.com/x" });

        var response = await anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

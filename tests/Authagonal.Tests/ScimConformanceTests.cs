using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Conformance details a provisioning client actually depends on, and the two places where a
/// malformed request was resolved in the destructive direction.
/// </summary>
public sealed class ScimConformanceTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _token = null!;
    private string _scimClientId = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        (_scimClientId, _token) = await _factory.SeedScimClientAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // F203 — the error `status` member is a JSON string
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ErrorBody_StatusIsAJsonString()
    {
        var response = await SendAsync(HttpMethod.Get, "/scim/v2/Users/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // RFC 7644 §3.12 types it as a string. Emitted as a number, a client deserializing the
        // schema's own type failed on every error response.
        Assert.Contains("\"status\":\"404\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // F206 — the ListResponse member is `Resources`, capital R
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("/scim/v2/Schemas")]
    [InlineData("/scim/v2/ResourceTypes")]
    public async Task Discovery_UsesTheSpecifiedResourcesCasing(string path)
    {
        var body = await (await SendAsync(HttpMethod.Get, path)).Content.ReadAsStringAsync();

        // One of the few SCIM members that is not lowerCamelCase; the default naming policy
        // camelCased it, so a conforming client found no resource list at all.
        Assert.Contains("\"Resources\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"resources\"", body, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // F96 / F327 — count semantics
    // -----------------------------------------------------------------------

    [Fact]
    public async Task List_NegativeCount_IsRefused()
    {
        var response = await SendAsync(HttpMethod.Get, "/scim/v2/Users?count=-1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_ZeroCount_ReturnsNoResourcesRatherThanReachingTheStore()
    {
        await CreateUserAsync("counted@example.com");

        var response = await SendAsync(HttpMethod.Get, "/scim/v2/Users?count=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // §3.4.2.4 gives count=0 a defined meaning: the total, without the resources. It used to be
        // passed straight through as the store page size, where non-positive is meaningless — the
        // Azure provider answers it with a 500.
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("Resources").EnumerateArray());
    }

    // -----------------------------------------------------------------------
    // F175 / F319 — Location on create, WWW-Authenticate on 401
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Create_CarriesALocationHeader()
    {
        var response = await CreateUserAsync("located@example.com");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The helper accepted a location argument and discarded it, so every create answered without
        // the header RFC 7644 §3.3 requires — the documented way to address the new resource.
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/scim/v2/Users/", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthenticated_CarriesABearerChallenge()
    {
        var response = await _client.GetAsync("/scim/v2/Users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h =>
            string.Equals(h.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // F345 — a disabled client's SCIM token must stop working
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DisabledClient_CannotUseItsScimToken()
    {
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/scim/v2/Users")).StatusCode);

        var client = await _factory.ClientStore.GetAsync(_scimClientId);
        client!.Enabled = false;
        await _factory.ClientStore.UpsertAsync(client);

        // Disabling stopped the client's OAuth flows while leaving its SCIM token able to read and
        // write the whole directory — the wider capability of the two.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/scim/v2/Users")).StatusCode);
    }

    // -----------------------------------------------------------------------
    // F193 — an unparseable `active` must not deprovision
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Patch_UnparseableActive_IsRefusedRatherThanTreatedAsFalse()
    {
        var created = await (await CreateUserAsync("patched@example.com")).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;

        var response = await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[] { new { op = "replace", path = "active", value = "maybe" } },
        });

        // It used to evaluate to false, so any typo silently DEPROVISIONED the user — and answered
        // 200, so both sides recorded a success.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await _factory.UserStore.GetAsync(id))!.IsActive);
    }

    [Fact]
    public async Task Patch_ProperActive_StillWorks()
    {
        var created = await (await CreateUserAsync("deprovisioned@example.com")).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;

        var response = await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[] { new { op = "replace", path = "active", value = false } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await _factory.UserStore.GetAsync(id))!.IsActive);
    }

    // -----------------------------------------------------------------------
    // F113 — externalId uniqueness on update, not only on create
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Patch_CannotStealAnotherUsersExternalId()
    {
        var owner = await (await CreateUserAsync("owner@example.com", externalId: "ext-1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var thief = await (await CreateUserAsync("thief@example.com", externalId: "ext-2"))
            .Content.ReadFromJsonAsync<JsonElement>();

        // POST checks this and 409s; PUT and PATCH did not, so an update could repoint the
        // (clientId, externalId) index at this record while the other user still believed it owned
        // the mapping — after which a deprovision aimed at one user hit the other.
        var response = await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{thief.GetProperty("id").GetString()}", new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[] { new { op = "replace", path = "externalId", value = "ext-1" } },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // And the owner still holds it.
        Assert.Equal(owner.GetProperty("id").GetString(),
            (await _factory.UserStore.FindByExternalIdAsync(_scimClientId, "ext-1"))?.Id);
    }

    // -----------------------------------------------------------------------
    // F191 — a non-primary address must not become the login identity
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Patch_Emails_PicksThePrimaryNotTheFirst()
    {
        var created = await (await CreateUserAsync("real@example.com")).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;

        // The alias is listed FIRST; the order is the IdP's, not the spec's. Taking element [0]
        // rewrote the account's userName and login identity to an address its owner may not control,
        // and the real one stopped resolving.
        var response = await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[]
            {
                new
                {
                    op = "replace",
                    path = "emails",
                    value = new object[]
                    {
                        new { value = "alias@example.com", primary = false },
                        new { value = "primary@example.com", primary = true },
                    },
                },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("primary@example.com", (await _factory.UserStore.GetAsync(id))!.Email);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private Task<HttpResponseMessage> CreateUserAsync(string userName, string? externalId = null) =>
        SendAsync(HttpMethod.Post, "/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName,
            externalId,
            name = new { givenName = "Test", familyName = "User" },
            emails = new[] { new { value = userName, primary = true } },
            active = true,
        });

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/scim+json");
        }
        return _client.SendAsync(request);
    }
}

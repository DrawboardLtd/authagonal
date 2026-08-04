using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Four gaps on the SCIM write and read surface, each present on a sibling path and absent here.
/// </summary>
public sealed class ScimWriteSurfaceTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    private async Task<HttpClient> ScimClientAsync()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        return client;
    }

    private static Task<HttpResponseMessage> CreateGroupAsync(
        HttpClient client, string displayName, string? externalId = null) =>
        client.PostAsJsonAsync("/scim/v2/Groups", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
            displayName,
            externalId,
        });

    // ── #24: externalId uniqueness, using the index every provider maintains ─

    /// <summary>
    /// <c>IScimGroupStore.FindByExternalIdAsync</c> was implemented, indexed and parity-tested by all four
    /// providers, and called by no production code — so the rule it exists to serve was never enforced.
    /// </summary>
    /// <remarks>
    /// No attacker needed. Entra POSTs a group, the response is lost to a gateway timeout after the row is
    /// written, Entra retries, and a second group with the same externalId is created. Later PATCHes resolve
    /// the group through an <c>externalId eq</c> filter, which returns whichever row the scan reaches first —
    /// so if the administrator attached a group-to-role mapping to the other one, the members it grants roles
    /// to silently stop matching the members the connector is maintaining.
    /// </remarks>
    [Fact]
    public async Task ADuplicateGroupExternalIdIsRefused()
    {
        var client = await ScimClientAsync();

        Assert.Equal(HttpStatusCode.Created, (await CreateGroupAsync(client, "Engineering", "G-1")).StatusCode);

        var duplicate = await CreateGroupAsync(client, "Engineering (retry)", "G-1");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Exactly one group exists, so a role mapping cannot bind to the wrong row.
        var list = await (await client.GetAsync("/scim/v2/Groups")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, list.GetProperty("totalResults").GetInt32());
    }

    [Fact]
    public async Task AReplaceCannotStealAnotherGroupsExternalId()
    {
        var client = await ScimClientAsync();
        await CreateGroupAsync(client, "Engineering", "G-1");
        var second = await CreateGroupAsync(client, "Sales", "G-2");
        var id = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var response = await client.PutAsJsonAsync($"/scim/v2/Groups/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
            displayName = "Sales",
            externalId = "G-1",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task APatchCannotStealAnotherGroupsExternalId()
    {
        var client = await ScimClientAsync();
        await CreateGroupAsync(client, "Engineering", "G-1");
        var second = await CreateGroupAsync(client, "Sales", "G-2");
        var id = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var patch = new StringContent("""
            {
              "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "Operations": [{ "op": "replace", "path": "externalId", "value": "G-1" }]
            }
            """, Encoding.UTF8, "application/scim+json");

        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsync($"/scim/v2/Groups/{id}", patch)).StatusCode);
    }

    /// <summary>Re-declaring a group's OWN externalId is not a conflict.</summary>
    [Fact]
    public async Task AGroupMayKeepItsOwnExternalId()
    {
        var client = await ScimClientAsync();
        var created = await CreateGroupAsync(client, "Engineering", "G-1");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var response = await client.PutAsJsonAsync($"/scim/v2/Groups/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
            displayName = "Engineering renamed",
            externalId = "G-1",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── #25: the single-group GET applies its projection ─────────────────────

    /// <summary>
    /// <c>GET /scim/v2/Groups/{id}</c> validated <c>attributes</c>/<c>excludedAttributes</c> and then returned
    /// the full resource anyway.
    /// </summary>
    /// <remarks>
    /// RFC 7644 §3.9 is honoured on the single-user GET, the user listing and the group listing. This was the
    /// fourth read path and the only one of the four with no test. On a role-mapped group the membership is the
    /// largest field in the response, and excluding it is the documented reason a connector sends the parameter.
    /// </remarks>
    [Fact]
    public async Task GettingOneGroup_HonoursExcludedAttributes()
    {
        var client = await ScimClientAsync();
        var created = await CreateGroupAsync(client, "Engineering", "G-1");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var json = await (await client.GetAsync($"/scim/v2/Groups/{id}?excludedAttributes=members"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(json.TryGetProperty("members", out _), "members was returned despite being excluded");
        Assert.Equal("Engineering", json.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task GettingOneGroup_HonoursAttributes()
    {
        var client = await ScimClientAsync();
        var created = await CreateGroupAsync(client, "Engineering", "G-1");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var json = await (await client.GetAsync($"/scim/v2/Groups/{id}?attributes=displayName"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Engineering", json.GetProperty("displayName").GetString());
        Assert.False(json.TryGetProperty("externalId", out _), "externalId was returned outside the projection");
        Assert.False(json.TryGetProperty("members", out _), "members was returned outside the projection");
    }

    // ── #62: the write surface fires hooks and writes audit rows ─────────────

    /// <summary>
    /// The entire SCIM write surface fired no <c>IAuthHook</c> and wrote no audit row.
    /// </summary>
    /// <remarks>
    /// <c>IAuthHook</c>'s own parameter docs name <c>"scim"</c> as an origin value it receives, and
    /// <c>docs/extensibility.md</c> uses an audit logger as its worked example — so an operator who registered
    /// one to mirror identities downstream and feed a SIEM got nothing. 500 accounts provisioned by Entra never
    /// reached the downstream directory, and a deactivation, which revokes every grant, produced no event.
    /// "Who deactivated this user and when" had nowhere to look, while the same trail faithfully recorded an
    /// administrator renaming a client.
    /// </remarks>
    [Fact]
    public async Task ProvisioningAUser_FiresTheHookAndWritesAnAuditRow()
    {
        var client = await ScimClientAsync();

        var created = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "alice@acme.example",
            active = true,
        });
        created.EnsureSuccessStatusCode();

        Assert.Contains(_factory.AuthHook.UserCreations, e => e.Email == "alice@acme.example" && e.CreatedVia == "scim");

        var entry = Assert.Single(_factory.AuditLog.Entries, e => e.Action == "scim.user_created");
        Assert.Equal("user", entry.EntityType);
        // Attributed to the provisioning CLIENT and its token, not to a person — see ScimActor.
        Assert.StartsWith("scim:", entry.Actor, StringComparison.Ordinal);
        Assert.Contains("scim-client", entry.Actor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeactivatingAUserThroughScim_IsAudited()
    {
        var client = await ScimClientAsync();

        var created = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "bob@acme.example",
            active = true,
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var patch = new StringContent("""
            {
              "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "Operations": [{ "op": "replace", "path": "active", "value": false }]
            }
            """, Encoding.UTF8, "application/scim+json");
        (await client.PatchAsync($"/scim/v2/Users/{id}", patch)).EnsureSuccessStatusCode();

        var entry = Assert.Single(_factory.AuditLog.Entries, e => e.Action == "scim.user_patched");
        Assert.Equal("deactivated", entry.Detail);
        Assert.Contains(_factory.AuthHook.UserUpdates, e => e.UpdatedVia == "scim");
    }

    [Fact]
    public async Task GroupWritesAreAudited()
    {
        var client = await ScimClientAsync();

        var created = await CreateGroupAsync(client, "Engineering", "G-1");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        (await client.DeleteAsync($"/scim/v2/Groups/{id}")).EnsureSuccessStatusCode();

        Assert.Contains(_factory.AuditLog.Entries, e => e.Action == "scim.group_created");
        Assert.Contains(_factory.AuditLog.Entries, e => e.Action == "scim.group_deleted");
    }

    // ── #29: the admin creation path validates the address ───────────────────

    /// <summary>
    /// <c>POST /api/v1/profile</c> validated only that the address was non-empty.
    /// </summary>
    /// <remarks>
    /// The normalized address is the email index's PartitionKey under the default configuration, and the
    /// profile row is written first — so a key the storage service rejects fails AFTER the account is durably
    /// created, leaving a record <c>FindByEmailAsync</c> cannot reach: the holder cannot log in, cannot reset
    /// their password, and the address cannot be reused because the profile row still holds it. Both sibling
    /// creation paths refuse the same values.
    /// </remarks>
    [Theory]
    [InlineData("a/b@x.example")]
    [InlineData("a\\b@x.example")]
    [InlineData("a#b@x.example")]
    [InlineData("a?b@x.example")]
    [InlineData("no-at-sign")]
    [InlineData("two@@x.example")]
    [InlineData("trailing@")]
    [InlineData("no-dot@localhost")]
    [InlineData("has space@x.example")]
    public async Task AdminUserCreation_RefusesAnUnstorableOrImplausibleAddress(string email)
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient();
        var adminToken = await _factory.GetAdminTokenAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profile")
        {
            Content = JsonContent.Create(new { email, password = "Test1234!" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("valid email", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminUserCreation_StillAcceptsAnOrdinaryAddress()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient();
        var adminToken = await _factory.GetAdminTokenAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profile")
        {
            Content = JsonContent.Create(new { email = "new.user+tag@acme.example", password = "Test1234!" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

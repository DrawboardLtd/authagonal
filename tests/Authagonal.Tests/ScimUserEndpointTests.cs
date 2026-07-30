using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class ScimUserEndpointTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    [Fact]
    public async Task CreateUser_WithValidToken_Returns201()
    {
        await _factory.SeedTestDataAsync();
        var (clientId, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "scim-user@example.com",
            name = new { givenName = "SCIM", familyName = "User" },
            active = true,
            externalId = "ext-001",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/scim+json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("scim-user@example.com", json.GetProperty("userName").GetString());
        Assert.Equal("ext-001", json.GetProperty("externalId").GetString());
        Assert.True(json.GetProperty("active").GetBoolean());
        Assert.NotEmpty(json.GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task CreateUser_WithoutToken_Returns401()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            userName = "test@example.com",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_DuplicateEmail_Returns409()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            userName = "duplicate@example.com",
            name = new { givenName = "First" },
        });

        var response = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            userName = "duplicate@example.com",
            name = new { givenName = "Second" },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetUser_ReturnsScimResource()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var createResponse = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            userName = "get-test@example.com",
            name = new { givenName = "Get", familyName = "Test" },
        });
        var createJson = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = createJson.GetProperty("id").GetString();

        var response = await client.GetAsync($"/scim/v2/Users/{userId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("get-test@example.com", json.GetProperty("userName").GetString());
    }

    [Fact]
    public async Task GetUser_NotFound_Returns404()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.GetAsync("/scim/v2/Users/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListUsers_ReturnsPaginatedList()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        // Create a few users
        for (int i = 0; i < 3; i++)
        {
            await client.PostAsJsonAsync("/scim/v2/Users", new
            {
                userName = $"list-user-{i}@example.com",
                name = new { givenName = $"User{i}" },
            });
        }

        var response = await client.GetAsync("/scim/v2/Users?startIndex=1&count=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("totalResults").GetInt32() >= 3);
        Assert.True(json.GetProperty("Resources").GetArrayLength() >= 3);
    }

    [Fact]
    public async Task ListUsers_WithFilter_ReturnsFiltered()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        await client.PostAsJsonAsync("/scim/v2/Users", new { userName = "filter-target@example.com" });
        await client.PostAsJsonAsync("/scim/v2/Users", new { userName = "filter-other@example.com" });

        var response = await client.GetAsync("/scim/v2/Users?filter=userName eq \"filter-target@example.com\"");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("totalResults").GetInt32());
        Assert.Equal("filter-target@example.com",
            json.GetProperty("Resources")[0].GetProperty("userName").GetString());
    }

    /// Compound filters and the operators beyond eq/co are part of the grammar ServiceProviderConfig
    /// advertises, so they must actually filter — not 400, and not quietly return everything.
    [Fact]
    public async Task ListUsers_CompoundFilter_IsHonoured()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        await client.PostAsJsonAsync("/scim/v2/Users", new { userName = "compound-wanted@example.com", active = true });
        await client.PostAsJsonAsync("/scim/v2/Users", new { userName = "compound-other@example.com", active = true });

        var filter = Uri.EscapeDataString("userName eq \"compound-wanted@example.com\" and active eq true");
        var response = await client.GetAsync($"/scim/v2/Users?filter={filter}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("totalResults").GetInt32());
        Assert.Equal("compound-wanted@example.com",
            json.GetProperty("Resources")[0].GetProperty("userName").GetString());
    }

    [Fact]
    public async Task ListUsers_StartsWithFilter_IsHonoured()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        await client.PostAsJsonAsync("/scim/v2/Users", new { userName = "swtarget-one@example.com" });
        await client.PostAsJsonAsync("/scim/v2/Users", new { userName = "unrelated-two@example.com" });

        var response = await client.GetAsync($"/scim/v2/Users?filter={Uri.EscapeDataString("userName sw \"swtarget\"")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("totalResults").GetInt32());
        Assert.Equal("swtarget-one@example.com",
            json.GetProperty("Resources")[0].GetProperty("userName").GetString());
    }

    /// Only genuinely malformed input is a 400 now — the grammar is supported, so a rejection means the
    /// caller wrote something that is not a SCIM filter at all.
    [Theory]
    [InlineData("userName eq")]              // no value
    [InlineData("(userName eq \"a\"")]       // unbalanced
    [InlineData("userName xx \"a\"")]        // not an operator
    public async Task ListUsers_MalformedFilter_Returns400InvalidFilter(string filter)
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.GetAsync($"/scim/v2/Users?filter={Uri.EscapeDataString(filter)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalidFilter", json.GetProperty("scimType").GetString());
    }

    [Fact]
    public async Task ListUsers_NoFilter_StillLists()
    {
        // The counterpart to the test above: absent must stay "list everything", not get swept into the
        // refusal by an over-eager unsupported check.
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        await client.PostAsJsonAsync("/scim/v2/Users", new { userName = "no-filter@example.com" });

        var response = await client.GetAsync("/scim/v2/Users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("totalResults").GetInt32() >= 1);
    }

    [Fact]
    public async Task PatchUser_Deactivate_SetsActiveToFalse()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var createResponse = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            userName = "patch-test@example.com",
            active = true,
        });
        var createJson = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = createJson.GetProperty("id").GetString();

        var patchContent = new StringContent(
            JsonSerializer.Serialize(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                Operations = new[]
                {
                    new { op = "replace", path = "active", value = (object)false }
                }
            }), Encoding.UTF8, "application/scim+json");

        var response = await client.PatchAsync($"/scim/v2/Users/{userId}", patchContent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task PatchUser_ChangeEmailToExistingUser_Returns409()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        await client.PostAsJsonAsync("/scim/v2/Users", new { userName = "victim2@example.com", name = new { givenName = "Victim" } });
        var attackerResp = await client.PostAsJsonAsync("/scim/v2/Users", new { userName = "attacker2@example.com", name = new { givenName = "Attacker" } });
        var attackerId = (await attackerResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // Repointing one record's email onto another existing user's email must be rejected, not
        // allowed to clobber the global email index.
        var patchContent = new StringContent(
            JsonSerializer.Serialize(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                Operations = new[]
                {
                    new { op = "replace", path = "userName", value = "victim2@example.com" }
                }
            }), Encoding.UTF8, "application/scim+json");

        var response = await client.PatchAsync($"/scim/v2/Users/{attackerId}", patchContent);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PutUser_ReplacesUser()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var createResponse = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            userName = "put-test@example.com",
            name = new { givenName = "Old", familyName = "Name" },
        });
        var createJson = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = createJson.GetProperty("id").GetString();

        var response = await client.PutAsJsonAsync($"/scim/v2/Users/{userId}", new
        {
            userName = "put-test@example.com",
            name = new { givenName = "New", familyName = "Name" },
            active = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New", json.GetProperty("name").GetProperty("givenName").GetString());
    }

    [Fact]
    public async Task DeleteUser_SoftDeactivates()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var createResponse = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            userName = "delete-test@example.com",
        });
        var createJson = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userId = createJson.GetProperty("id").GetString();

        var response = await client.DeleteAsync($"/scim/v2/Users/{userId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify user is deactivated (not actually deleted)
        var user = await _factory.UserStore.GetAsync(userId!);
        Assert.NotNull(user);
        Assert.False(user.IsActive);
    }

    [Fact]
    public async Task DeactivatedUser_CannotLogin()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync("disabled@example.com", "Test1234!");

        // Deactivate the user
        user.IsActive = false;
        await _factory.UserStore.UpdateAsync(user);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "disabled@example.com",
            password = "Test1234!",
        });

        Assert.Equal(HttpStatusCode.Forbidden, loginResponse.StatusCode);
        var json = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("account_disabled", json.GetProperty("error").GetString());
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    // -----------------------------------------------------------------------
    // Deprovisioning must stick (#69)
    // -----------------------------------------------------------------------

    /// <summary>
    /// <c>ScimCreateUserRequest.Active</c> was a non-nullable bool defaulting to TRUE, and PUT assigned it
    /// straight onto <c>IsActive</c> — so a replace that simply did not mention <c>active</c> reactivated a
    /// deprovisioned user. An offboarded account came back on the next directory sync, silently.
    /// </summary>
    [Fact]
    public async Task ReplaceUser_OmittingActive_DoesNotReactivateADeprovisionedUser()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var created = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "offboard@example.com",
            active = true,
        });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        // Deprovision.
        var deactivate = await client.PutAsJsonAsync($"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "offboard@example.com",
            active = false,
        });
        deactivate.EnsureSuccessStatusCode();
        Assert.False((await _factory.UserStore.GetAsync(id))!.IsActive);

        // A later sync that does NOT mention `active` must leave them deprovisioned.
        var resync = await client.PutAsJsonAsync($"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "offboard@example.com",
            name = new { givenName = "Off", familyName = "Board" },
        });
        resync.EnsureSuccessStatusCode();

        Assert.False((await _factory.UserStore.GetAsync(id))!.IsActive,
            "a PUT omitting `active` reactivated a deprovisioned user");
    }

    /// <summary>An explicit active=true must still reactivate — the fix preserves, it does not freeze.</summary>
    [Fact]
    public async Task ReplaceUser_ExplicitActiveTrue_StillReactivates()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var created = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "rehire@example.com",
            active = false,
        });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var reactivate = await client.PutAsJsonAsync($"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "rehire@example.com",
            active = true,
        });
        reactivate.EnsureSuccessStatusCode();

        Assert.True((await _factory.UserStore.GetAsync(id))!.IsActive);
    }

    /// <summary>
    /// Deactivating via PUT must revoke grants, not merely set a flag. PATCH and DELETE already did; PUT
    /// left every refresh token and stored consent live, so the offboarded user kept working.
    /// </summary>
    [Fact]
    public async Task ReplaceUser_Deactivating_RevokesGrants()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var created = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "revoke-me@example.com",
            active = true,
        });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        await _factory.GrantStore.StoreAsync(new Authagonal.Core.Models.PersistedGrant
        {
            Key = $"rt:{Guid.NewGuid():N}",
            Type = "refresh_token",
            SubjectId = id,
            ClientId = AuthagonalTestFactory.TestClientId,
            Data = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        Assert.NotEmpty(await _factory.GrantStore.GetBySubjectAsync(id));

        var deactivate = await client.PutAsJsonAsync($"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "revoke-me@example.com",
            active = false,
        });
        deactivate.EnsureSuccessStatusCode();

        Assert.Empty(await _factory.GrantStore.GetBySubjectAsync(id));
    }
}

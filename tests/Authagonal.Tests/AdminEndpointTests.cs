using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class AdminEndpointTests : IAsyncLifetime
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

    private void SetAdminAuth() =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);

    // -----------------------------------------------------------------------
    // POST /api/v1/profile — RegisterUser
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RegisterUser_ValidRequest_Returns201()
    {
        SetAdminAuth();

        var response = await _client.PostAsJsonAsync("/api/v1/profile/", new
        {
            email = "newuser@example.com",
            password = "Str0ng!Pass",
            firstName = "Jane",
            lastName = "Doe"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("newuser@example.com", json.GetProperty("email").GetString());
        Assert.Equal("Jane", json.GetProperty("firstName").GetString());
        Assert.False(json.GetProperty("emailConfirmed").GetBoolean());
    }

    [Fact]
    public async Task RegisterUser_SendsVerificationEmail()
    {
        SetAdminAuth();

        await _client.PostAsJsonAsync("/api/v1/profile/", new
        {
            email = "verify@example.com",
            password = "Str0ng!Pass",
            firstName = "V",
            lastName = "User"
        });

        Assert.Contains(_factory.EmailService.SentEmails, e => e.Email == "verify@example.com" && e.Type == "verification");
    }

    [Fact]
    public async Task RegisterUser_RunsProvisioningByDefault()
    {
        SetAdminAuth();

        var response = await _client.PostAsJsonAsync("/api/v1/profile/", new
        {
            email = "provisioned@example.com",
            password = "Str0ng!Pass"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Contains(id, _factory.Provisioning.Provisioned);
    }

    /// <summary>
    /// The caller is itself the provisioning target and is already part-way through setting this user
    /// up — provisioning it here would call that app back about a user it is in the middle of
    /// creating, carrying only the attributes that survived the round trip.
    /// </summary>
    [Fact]
    public async Task RegisterUser_SkipProvisioning_CreatesTheUserWithoutProvisioningIt()
    {
        SetAdminAuth();

        var response = await _client.PostAsJsonAsync("/api/v1/profile/", new
        {
            email = "self-provisioned@example.com",
            password = "Str0ng!Pass",
            skipProvisioning = true
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // The identity exists...
        Assert.NotNull(await _factory.UserStore.GetAsync(id!));
        // ...and nothing was asked to provision it.
        Assert.DoesNotContain(id, _factory.Provisioning.Provisioned);
    }

    [Fact]
    public async Task RegisterUser_DuplicateEmail_Returns409()
    {
        SetAdminAuth();
        await _factory.SeedTestUserAsync(email: "dupe@example.com");

        var response = await _client.PostAsJsonAsync("/api/v1/profile/", new
        {
            email = "dupe@example.com",
            password = "Str0ng!Pass"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RegisterUser_WeakPassword_Returns400()
    {
        SetAdminAuth();

        var response = await _client.PostAsJsonAsync("/api/v1/profile/", new
        {
            email = "weak@example.com",
            password = "short"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("weak_password", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RegisterUser_NoAuth_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/profile/", new
        {
            email = "noauth@example.com",
            password = "Str0ng!Pass"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegisterUser_FiresAuthHook()
    {
        SetAdminAuth();

        await _client.PostAsJsonAsync("/api/v1/profile/", new
        {
            email = "hooked@example.com",
            password = "Str0ng!Pass"
        });

        Assert.Contains(_factory.AuthHook.UserCreations, c => c.Email == "hooked@example.com" && c.CreatedVia == "admin");
    }

    // -----------------------------------------------------------------------
    // GET /api/v1/profile/{userId}
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetUser_ExistingUser_ReturnsProfile()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "getme@example.com");

        var response = await _client.GetAsync($"/api/v1/profile/{user.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("getme@example.com", json.GetProperty("email").GetString());
        Assert.Equal("Test", json.GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task GetUser_NonexistentUser_Returns404()
    {
        SetAdminAuth();

        var response = await _client.GetAsync("/api/v1/profile/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // PUT /api/v1/profile — UpdateUser
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpdateUser_ValidRequest_UpdatesFields()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "update@example.com");

        var response = await _client.PutAsJsonAsync("/api/v1/profile/", new
        {
            userId = user.Id,
            firstName = "Updated",
            lastName = "Name",
            companyName = "Acme Inc",
            phone = "+1234567890"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Updated", json.GetProperty("firstName").GetString());
        Assert.Equal("Acme Inc", json.GetProperty("companyName").GetString());
    }

    [Fact]
    public async Task UpdateUser_OrgChange_InvalidatesTokens()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "orgchange@example.com");

        // Store a grant for this user
        await _factory.GrantStore.StoreAsync(new Core.Models.PersistedGrant
        {
            Key = "test-grant",
            Type = "refresh_token",
            SubjectId = user.Id,
            ClientId = "test-client",
            Data = "data",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });

        await _client.PutAsJsonAsync("/api/v1/profile/", new
        {
            userId = user.Id,
            organizationId = "new-org"
        });

        // All grants for this user should be removed
        var grants = await _factory.GrantStore.GetBySubjectAsync(user.Id);
        Assert.Empty(grants);
    }

    // -----------------------------------------------------------------------
    // DELETE /api/v1/profile/{userId}
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeleteUser_ExistingUser_Returns204()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "delete@example.com");

        var response = await _client.DeleteAsync($"/api/v1/profile/{user.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify user is gone
        var getResponse = await _client.GetAsync($"/api/v1/profile/{user.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_NonexistentUser_Returns404()
    {
        SetAdminAuth();

        var response = await _client.DeleteAsync("/api/v1/profile/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // POST /api/v1/profile/{userId}/identities — LinkExternalIdentity
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LinkExternalIdentity_Returns201()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "link@example.com");

        var response = await _client.PostAsJsonAsync($"/api/v1/profile/{user.Id}/identities", new
        {
            provider = "google",
            providerKey = "google-123",
            displayName = "test@gmail.com"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("google", json.GetProperty("provider").GetString());
    }

    // -----------------------------------------------------------------------
    // DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnlinkExternalIdentity_Returns204()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "unlink@example.com");
        await _factory.UserStore.AddLoginAsync(new Core.Models.ExternalLoginInfo
        {
            UserId = user.Id,
            Provider = "google",
            ProviderKey = "g-456"
        });

        var response = await _client.DeleteAsync($"/api/v1/profile/{user.Id}/identities/google/g-456");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    // -----------------------------------------------------------------------
    // Directory + support routes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SearchUsers_FindsByEmailPrefix()
    {
        SetAdminAuth();
        await _factory.SeedTestUserAsync(email: "findme@example.com");

        var response = await _client.GetAsync("/api/v1/profile/search?q=findme");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(json.GetProperty("users").EnumerateArray(),
            u => u.GetProperty("email").GetString() == "findme@example.com");
    }

    /// <summary>
    /// Exact, unlike search — a caller resolving "this address" to "this account" wants one answer or
    /// none, not a prefix match that happens to include somebody else.
    /// </summary>
    [Fact]
    public async Task GetUserByEmail_ReturnsTheOneAccount()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "exact@example.com");

        var response = await _client.GetAsync("/api/v1/profile/by-email?email=exact@example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.Id, json.GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetUserByEmail_UnknownAddress_Returns404()
    {
        SetAdminAuth();

        var response = await _client.GetAsync("/api/v1/profile/by-email?email=nobody@example.com");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UsersExist_ReturnsOnlyTheOnesThatDo()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "real@example.com");

        var response = await _client.PostAsJsonAsync("/api/v1/profile/exists",
            new { userIds = new[] { user.Id, "does-not-exist" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = json.GetProperty("userIds").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal([user.Id], ids);
        Assert.False(json.GetProperty("truncated").GetBoolean());
    }

    /// <summary>
    /// Callers have been sending isActive and emailVerified on update all along; the request model
    /// had neither, so both were silently dropped and a "blocked" user stayed enabled.
    /// </summary>
    [Fact]
    public async Task UpdateUser_HonoursIsActiveAndEmailVerified()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "toggle@example.com", emailConfirmed: false);

        var response = await _client.PutAsJsonAsync("/api/v1/profile",
            new { userId = user.Id, isActive = false, emailVerified = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await _factory.UserStore.GetAsync(user.Id);
        Assert.False(stored!.IsActive);
        Assert.True(stored.EmailConfirmed);
    }

    [Fact]
    public async Task SetPassword_ReplacesTheCredential()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "reset@example.com");
        var before = (await _factory.UserStore.GetAsync(user.Id))!.PasswordHash;

        var response = await _client.PostAsJsonAsync($"/api/v1/profile/{user.Id}/set-password",
            new { password = "An0ther!Pass" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = (await _factory.UserStore.GetAsync(user.Id))!;
        Assert.NotEqual(before, after.PasswordHash);
    }

    [Fact]
    public async Task SetPassword_WeakPassword_IsRefused()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "weak@example.com");

        var response = await _client.PostAsJsonAsync($"/api/v1/profile/{user.Id}/set-password",
            new { password = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnlockUser_ClearsTheLockoutAndTheFailureCount()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "locked@example.com");
        user.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
        user.AccessFailedCount = 5;
        await _factory.UserStore.UpdateAsync(user);

        var response = await _client.PostAsync($"/api/v1/profile/{user.Id}/unlock", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await _factory.UserStore.GetAsync(user.Id);
        Assert.Null(stored!.LockoutEnd);
        Assert.Equal(0, stored.AccessFailedCount);
    }

    /// <summary>
    /// A support console needs to tell "forgotten their password" from "never had one, they use
    /// SSO" — opposite advice for someone who cannot get in. Presence only; the hash never leaves.
    /// </summary>
    [Fact]
    public async Task GetUser_ReportsWhetherThereIsAPasswordWithoutReturningIt()
    {
        SetAdminAuth();
        var user = await _factory.SeedTestUserAsync(email: "haspw@example.com");

        var response = await _client.GetAsync($"/api/v1/profile/{user.Id}");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("hasPassword").GetBoolean());
        Assert.True(json.GetProperty("isActive").GetBoolean());
        Assert.Equal(0, json.GetProperty("accessFailedCount").GetInt32());
        Assert.DoesNotContain("passwordHash", json.ToString(), StringComparison.OrdinalIgnoreCase);
    }

}

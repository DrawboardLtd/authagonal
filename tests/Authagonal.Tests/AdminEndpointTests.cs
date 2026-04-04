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
}

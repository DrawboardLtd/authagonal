using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class DeviceAuthorizationTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();

        // Add device_code grant type to the admin client (confidential)
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.AdminClientId);
        client!.AllowedGrantTypes = ["client_credentials", GrantTypes.DeviceCode];
        client.AllowedScopes = ["openid", "profile", "email", "offline_access", AuthagonalTestFactory.AdminScope];
        client.AllowOfflineAccess = true;
        await _factory.ClientStore.UpsertAsync(client);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // POST /connect/deviceauthorization
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeviceAuthorization_ValidClient_ReturnsCodePair()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
            ["scope"] = "openid profile email"
        });

        var response = await _client.PostAsync("/connect/deviceauthorization", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.NotNull(json.GetProperty("device_code").GetString());
        Assert.NotNull(json.GetProperty("user_code").GetString());
        Assert.NotNull(json.GetProperty("verification_uri").GetString());
        Assert.NotNull(json.GetProperty("verification_uri_complete").GetString());
        Assert.True(json.GetProperty("expires_in").GetInt32() > 0);
        Assert.True(json.GetProperty("interval").GetInt32() > 0);

        // User code format: XXXX-XXXX
        var userCode = json.GetProperty("user_code").GetString()!;
        Assert.Matches(@"^[A-Z0-9]{4}-[A-Z0-9]{4}$", userCode);
    }

    [Fact]
    public async Task DeviceAuthorization_UnknownClient_Returns401()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "nonexistent",
            ["scope"] = "openid"
        });

        var response = await _client.PostAsync("/connect/deviceauthorization", form);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeviceAuthorization_ClientWithoutDeviceGrant_Returns400()
    {
        // test-client only has authorization_code + refresh_token
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = AuthagonalTestFactory.TestClientId,
            ["scope"] = "openid"
        });

        var response = await _client.PostAsync("/connect/deviceauthorization", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // POST /connect/token — device_code grant (polling)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeviceCodeGrant_BeforeApproval_ReturnsAuthorizationPending()
    {
        var deviceCodes = await RequestDeviceCodes();

        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.DeviceCode,
            ["device_code"] = deviceCodes.DeviceCode,
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret
        });

        var response = await _client.PostAsync("/connect/token", tokenForm);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization_pending", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeviceCodeGrant_AfterApproval_ReturnsTokens()
    {
        var user = await _factory.SeedTestUserAsync();
        var deviceCodes = await RequestDeviceCodes();

        // Login as the user
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        // Approve the device code
        var approveForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user_code"] = deviceCodes.UserCode
        });
        var approveResponse = await _client.PostAsync("/api/auth/device/approve", approveForm);
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);

        // Now poll for tokens
        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.DeviceCode,
            ["device_code"] = deviceCodes.DeviceCode,
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret
        });

        var tokenResponse = await _client.PostAsync("/connect/token", tokenForm);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);

        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(tokens.GetProperty("access_token").GetString());
    }

    [Fact]
    public async Task DeviceCodeGrant_InvalidDeviceCode_Returns400()
    {
        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.DeviceCode,
            ["device_code"] = "totally-invalid-code",
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret
        });

        var response = await _client.PostAsync("/connect/token", tokenForm);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeviceCodeGrant_WrongClient_Returns400()
    {
        var deviceCodes = await RequestDeviceCodes();

        // Try to exchange with test-client instead of admin-client
        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.DeviceCode,
            ["device_code"] = deviceCodes.DeviceCode,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
        });

        var response = await _client.PostAsync("/connect/token", tokenForm);
        // Should fail — either unsupported_grant_type or invalid_grant
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeviceApproval_NotAuthenticated_Returns401()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user_code"] = "ABCD-1234"
        });

        var response = await _client.PostAsync("/api/auth/device/approve", form);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeviceApproval_InvalidCode_Returns400()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user_code"] = "ZZZZ-9999"
        });

        var response = await _client.PostAsync("/api/auth/device/approve", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<DeviceCodes> RequestDeviceCodes()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
            ["scope"] = "openid profile email"
        });

        var response = await _client.PostAsync("/connect/deviceauthorization", form);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        return new DeviceCodes(
            json.GetProperty("device_code").GetString()!,
            json.GetProperty("user_code").GetString()!);
    }

    private sealed record DeviceCodes(string DeviceCode, string UserCode);
}

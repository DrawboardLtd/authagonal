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
    public async Task DeviceCodeGrant_PollingTooFast_ReturnsSlowDown()
    {
        // RFC 8628 §3.5 (F45): a second poll within the advertised interval must earn slow_down.
        var deviceCodes = await RequestDeviceCodes();

        FormUrlEncodedContent Poll() => new(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.DeviceCode,
            ["device_code"] = deviceCodes.DeviceCode,
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret
        });

        var first = await _client.PostAsync("/connect/token", Poll());
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization_pending", firstJson.GetProperty("error").GetString());

        // Immediately poll again — inside the 5s interval.
        var second = await _client.PostAsync("/connect/token", Poll());
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var secondJson = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("slow_down", secondJson.GetProperty("error").GetString());
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

    /// <summary>
    /// The approval screen showed nothing about what was being approved, and
    /// <c>verification_uri_complete</c> pre-fills the code — so approval was one click on an opaque prompt.
    /// That is RFC 8628 §5.4's remote-phishing / illicit-consent-grant shape: an attacker starts a device
    /// flow, sends the victim the complete URI, and the victim authorises the ATTACKER's device against
    /// their own account. This endpoint is what makes informed consent possible.
    /// </summary>
    [Fact]
    public async Task DeviceInfo_DescribesTheRequestingClientAndScopes()
    {
        await _factory.SeedTestUserAsync();
        var deviceCodes = await RequestDeviceCodes();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var response = await _client.GetAsync(
            $"/api/auth/device/info?user_code={Uri.EscapeDataString(deviceCodes.UserCode)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Which application is asking — the client the DEVICE request was made for, not the browser's.
        Assert.Equal(AuthagonalTestFactory.AdminClientId, body.GetProperty("clientId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("clientName").GetString()));
        // ...and for what.
        Assert.True(body.GetProperty("scopes").GetArrayLength() > 0);
    }

    /// <summary>Anonymous callers must not be able to probe codes through the info endpoint.</summary>
    [Fact]
    public async Task DeviceInfo_NotAuthenticated_Returns401()
    {
        var deviceCodes = await RequestDeviceCodes();

        var response = await _client.GetAsync(
            $"/api/auth/device/info?user_code={Uri.EscapeDataString(deviceCodes.UserCode)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeviceInfo_InvalidCode_Returns400()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var response = await _client.GetAsync("/api/auth/device/info?user_code=ZZZZ-9999");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // F256 — a poll must not be able to roll back a consume
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ApprovingAnAlreadyRedeemedCode_IsRefused()
    {
        await _factory.SeedTestUserAsync();
        var deviceCodes = await RequestDeviceCodes();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var approveForm = new Dictionary<string, string> { ["user_code"] = deviceCodes.UserCode };
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsync("/api/auth/device/approve", new FormUrlEncodedContent(approveForm))).StatusCode);

        var tokenForm = new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.DeviceCode,
            ["device_code"] = deviceCodes.DeviceCode,
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
        };
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsync("/connect/token", new FormUrlEncodedContent(tokenForm))).StatusCode);

        // The approval write is an unconditional full-row upsert and the row it writes carries
        // ConsumedAt = null, so an approval landing after a consume erases the marker the atomic
        // consume depends on and one approval yields a second token set.
        //
        // Through the API this is already blocked one step earlier — the user_code grant is marked
        // consumed on approval, so a second approval never reaches the device grant. The guard added
        // on the device grant itself is the belt to that brace, for any path that reaches the
        // approval write with a device code rather than a user code. This test pins the outcome both
        // of them exist to produce.
        var reapprove = await _client.PostAsync("/api/auth/device/approve", new FormUrlEncodedContent(approveForm));
        Assert.Equal(HttpStatusCode.BadRequest, reapprove.StatusCode);

        var secondToken = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(tokenForm));
        Assert.Equal(HttpStatusCode.BadRequest, secondToken.StatusCode);
    }

    [Fact]
    public async Task PendingPoll_DoesNotRewriteTheGrant()
    {
        await _factory.SeedTestUserAsync();
        var deviceCodes = await RequestDeviceCodes();

        var before = await _factory.GrantStore.GetAsync($"device:{deviceCodes.DeviceCode}");
        Assert.NotNull(before);

        await _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.DeviceCode,
            ["device_code"] = deviceCodes.DeviceCode,
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
        }));

        // The poll persisted a LastPolledAt marker through an unconditional upsert, which is what
        // could land on top of a concurrent consume. The §3.5 interval now rides the rate limiter, so
        // a pending poll leaves the grant untouched — nothing to roll anything back with.
        var after = await _factory.GrantStore.GetAsync($"device:{deviceCodes.DeviceCode}");
        Assert.NotNull(after);
        Assert.Equal(before!.Data, after!.Data);
    }

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

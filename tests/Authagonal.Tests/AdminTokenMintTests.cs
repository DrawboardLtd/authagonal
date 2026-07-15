using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

// ---------------------------------------------------------------------------------------------
// POST /api/v1/token (admin token mint / impersonation, TokenEndpoints) plus the previously
// untested MfaAdminEndpoints paths (GET MFA status, DELETE a specific credential).
// Stock AuthagonalTestFactory with a real admin bearer token throughout.
// ---------------------------------------------------------------------------------------------
public sealed class AdminTokenMintTests : IAsyncLifetime
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

    private HttpRequestMessage AdminRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        return request;
    }

    private async Task<HttpResponseMessage> MintAsync(string clientId, string userId, string? scopes = null)
    {
        var url = $"/api/v1/token?clientId={Uri.EscapeDataString(clientId)}&userId={Uri.EscapeDataString(userId)}";
        if (scopes is not null)
            url += $"&scopes={Uri.EscapeDataString(scopes)}";
        return await _client.SendAsync(AdminRequest(HttpMethod.Post, url));
    }

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(payload));
    }

    // -------------------------------------------------------------------------
    // Happy path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Mint_OpenidScope_ReturnsAccessRefreshAndIdTokensForTargetUser()
    {
        var user = await _factory.SeedTestUserAsync(email: "target@example.com");

        var response = await MintAsync(AuthagonalTestFactory.TestClientId, user.Id, "openid profile offline_access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var accessToken = json.GetProperty("access_token").GetString()!;
        Assert.False(string.IsNullOrEmpty(accessToken));
        // offline_access requested + client allows it → refresh token issued
        Assert.False(string.IsNullOrEmpty(json.GetProperty("refresh_token").GetString()));
        Assert.False(string.IsNullOrEmpty(json.GetProperty("id_token").GetString()));
        Assert.Equal("openid profile offline_access", json.GetProperty("scope").GetString());
        Assert.Equal(3600, json.GetProperty("expires_in").GetInt32()); // client's AccessTokenLifetimeSeconds

        // Access token is minted FOR the target user THROUGH the requested client
        var payload = DecodeJwtPayload(accessToken);
        Assert.Equal(user.Id, payload.GetProperty("sub").GetString());
        Assert.Equal(AuthagonalTestFactory.TestClientId, payload.GetProperty("client_id").GetString());
        Assert.Equal("openid profile offline_access", payload.GetProperty("scope").GetString());

        // Id token subject matches too
        var idPayload = DecodeJwtPayload(json.GetProperty("id_token").GetString()!);
        Assert.Equal(user.Id, idPayload.GetProperty("sub").GetString());
        Assert.Equal("target@example.com", idPayload.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Mint_NoScopesParam_DefaultsToClientAllowedScopes()
    {
        var user = await _factory.SeedTestUserAsync(email: "defaults@example.com");

        var response = await MintAsync(AuthagonalTestFactory.TestClientId, user.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Test client's registered scopes, in registration order
        Assert.Equal("openid profile email offline_access", json.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Mint_WithoutOpenidScope_OmitsIdToken()
    {
        var user = await _factory.SeedTestUserAsync(email: "noid@example.com");

        var response = await MintAsync(AuthagonalTestFactory.TestClientId, user.Id, "profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.TryGetProperty("id_token", out _));
        // No offline_access requested → no refresh token (an unconditional mint used to hand every
        // impersonation call a long-lived credential).
        Assert.False(json.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrEmpty(rt.GetString()));
    }

    // -------------------------------------------------------------------------
    // Scope constraints
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Mint_ScopeOutsideClientAllowedScopes_Returns400InvalidScope()
    {
        var user = await _factory.SeedTestUserAsync(email: "scoped@example.com");

        var response = await MintAsync(AuthagonalTestFactory.TestClientId, user.Id, "openid payments:write");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_scope", json.GetProperty("error").GetString());
        Assert.Contains("payments:write", json.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task Mint_AdminScope_Returns403ForbiddenScope()
    {
        var user = await _factory.SeedTestUserAsync(email: "admin-mint@example.com");

        // The seeded admin client legitimately HOLDS the admin scope, so this passes the
        // subset check and must be stopped by the dedicated forbidden_scope guard.
        var response = await MintAsync(AuthagonalTestFactory.AdminClientId, user.Id, AuthagonalTestFactory.AdminScope);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden_scope", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mint_AdminScope_CaseInsensitive_Returns403()
    {
        var user = await _factory.SeedTestUserAsync(email: "admin-caps@example.com");

        var response = await MintAsync(AuthagonalTestFactory.AdminClientId, user.Id, "AUTHAGONAL-ADMIN");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Validation / lookups
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Mint_MissingClientId_Returns400()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/token?userId=someone"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mint_MissingUserId_Returns400()
    {
        var response = await _client.SendAsync(
            AdminRequest(HttpMethod.Post, $"/api/v1/token?clientId={AuthagonalTestFactory.TestClientId}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mint_UnknownClient_Returns404()
    {
        var user = await _factory.SeedTestUserAsync(email: "noclient@example.com");
        var response = await MintAsync("no-such-client", user.Id, "openid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("client_not_found", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mint_UnknownUser_Returns404()
    {
        var response = await MintAsync(AuthagonalTestFactory.TestClientId, "no-such-user", "openid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("user_not_found", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mint_NoAuth_Returns401()
    {
        var user = await _factory.SeedTestUserAsync(email: "unauth@example.com");
        var response = await _client.PostAsync(
            $"/api/v1/token?clientId={AuthagonalTestFactory.TestClientId}&userId={user.Id}", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // MfaAdminEndpoints — GET /api/v1/profile/{userId}/mfa (status)
    // -------------------------------------------------------------------------

    private async Task<AuthUser> SeedMfaUserAsync(string email)
    {
        var user = await _factory.SeedTestUserAsync(email: email);
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);

        await _factory.MfaStore.CreateCredentialAsync(new MfaCredential
        {
            Id = "totp-1",
            UserId = user.Id,
            Type = MfaCredentialType.Totp,
            Name = "Authenticator app",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
            LastUsedAt = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await _factory.MfaStore.CreateCredentialAsync(new MfaCredential
        {
            Id = "rec-1",
            UserId = user.Id,
            Type = MfaCredentialType.RecoveryCode,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
            IsConsumed = true,
        });
        return user;
    }

    [Fact]
    public async Task MfaStatus_ReturnsEnabledFlagAndMethods()
    {
        var user = await SeedMfaUserAsync("mfa-status@example.com");

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Get, $"/api/v1/profile/{user.Id}/mfa/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("enabled").GetBoolean());

        var methods = json.GetProperty("methods").EnumerateArray().ToList();
        Assert.Equal(2, methods.Count);

        var totp = methods.Single(m => m.GetProperty("type").GetString() == "totp");
        Assert.Equal("totp-1", totp.GetProperty("id").GetString());
        Assert.Equal("Authenticator app", totp.GetProperty("name").GetString());
        Assert.False(totp.TryGetProperty("isConsumed", out _)); // only reported for recovery codes

        var recovery = methods.Single(m => m.GetProperty("type").GetString() == "recoverycode");
        Assert.True(recovery.GetProperty("isConsumed").GetBoolean());
    }

    [Fact]
    public async Task MfaStatus_UserWithoutMfa_ReturnsDisabledEmptyMethods()
    {
        var user = await _factory.SeedTestUserAsync(email: "no-mfa@example.com");

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Get, $"/api/v1/profile/{user.Id}/mfa/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, json.GetProperty("methods").GetArrayLength());
    }

    [Fact]
    public async Task MfaStatus_UnknownUser_Returns404()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Get, "/api/v1/profile/no-such-user/mfa/"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("user_not_found", json.GetProperty("error").GetString());
    }

    // -------------------------------------------------------------------------
    // MfaAdminEndpoints — DELETE /api/v1/profile/{userId}/mfa/{credentialId}
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeleteMfaCredential_LastRealFactor_RemovesAndDisablesMfa()
    {
        var user = await SeedMfaUserAsync("mfa-del@example.com");

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Delete, $"/api/v1/profile/{user.Id}/mfa/totp-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());

        Assert.Null(await _factory.MfaStore.GetCredentialAsync(user.Id, "totp-1"));
        // Only the recovery code remains → MFA flips off (recovery codes aren't a real factor)
        Assert.False((await _factory.UserStore.GetAsync(user.Id))!.MfaEnabled);
    }

    [Fact]
    public async Task DeleteMfaCredential_OtherRealFactorRemains_KeepsMfaEnabled()
    {
        var user = await SeedMfaUserAsync("mfa-keep@example.com");
        await _factory.MfaStore.CreateCredentialAsync(new MfaCredential
        {
            Id = "totp-2",
            UserId = user.Id,
            Type = MfaCredentialType.Totp,
            Name = "Backup authenticator",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Delete, $"/api/v1/profile/{user.Id}/mfa/totp-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await _factory.MfaStore.GetCredentialAsync(user.Id, "totp-1"));
        Assert.NotNull(await _factory.MfaStore.GetCredentialAsync(user.Id, "totp-2"));
        Assert.True((await _factory.UserStore.GetAsync(user.Id))!.MfaEnabled);
    }

    [Fact]
    public async Task DeleteMfaCredential_UnknownCredential_Returns404()
    {
        var user = await SeedMfaUserAsync("mfa-nocred@example.com");

        var response = await _client.SendAsync(AdminRequest(HttpMethod.Delete, $"/api/v1/profile/{user.Id}/mfa/nope"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("credential_not_found", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeleteMfaCredential_UnknownUser_Returns404()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Delete, "/api/v1/profile/no-such-user/mfa/totp-1"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("user_not_found", json.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("GET", "/api/v1/profile/someone/mfa/")]
    [InlineData("DELETE", "/api/v1/profile/someone/mfa/totp-1")]
    public async Task MfaAdminEndpoints_NoToken_Returns401(string method, string url)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

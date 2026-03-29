using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

public class MfaEndpointTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Login_MfaDisabled_SignsCookieDirectly()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);

        // Enroll TOTP for user
        await EnrollTotpForUser(user.Id);

        // Client has MfaPolicy=Disabled (default)
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("userId", out _));
        Assert.False(json.TryGetProperty("mfaRequired", out _));
    }

    [Fact]
    public async Task Login_MfaEnabled_UserEnrolled_ReturnsMfaRequired()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);

        // Set client MfaPolicy=Enabled
        var testClient = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        testClient!.MfaPolicy = MfaPolicy.Enabled;
        await _factory.ClientStore.UpsertAsync(testClient);

        // Enroll TOTP for user
        await EnrollTotpForUser(user.Id);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync(
            $"/api/auth/login?returnUrl=/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}",
            new { email = "test@example.com", password = "Test1234!" });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("mfaRequired").GetBoolean());
        Assert.True(json.TryGetProperty("challengeId", out _));
        Assert.True(json.TryGetProperty("methods", out var methods));
        Assert.Contains("totp", methods.EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task Login_MfaEnabled_UserNotEnrolled_SignsCookieDirectly()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();

        // Set client MfaPolicy=Enabled
        var testClient = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        testClient!.MfaPolicy = MfaPolicy.Enabled;
        await _factory.ClientStore.UpsertAsync(testClient);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync(
            $"/api/auth/login?returnUrl=/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}",
            new { email = "test@example.com", password = "Test1234!" });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("userId", out _));
        Assert.False(json.TryGetProperty("mfaRequired", out _));
    }

    [Fact]
    public async Task Login_MfaRequired_UserNotEnrolled_ReturnsMfaSetupRequired()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();

        // Set client MfaPolicy=Required
        var testClient = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        testClient!.MfaPolicy = MfaPolicy.Required;
        await _factory.ClientStore.UpsertAsync(testClient);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync(
            $"/api/auth/login?returnUrl=/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}",
            new { email = "test@example.com", password = "Test1234!" });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("mfaSetupRequired").GetBoolean());
    }

    [Fact]
    public async Task MfaVerify_ValidTotp_SignsCookie()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);

        // Set client MfaPolicy=Enabled
        var testClient = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        testClient!.MfaPolicy = MfaPolicy.Enabled;
        await _factory.ClientStore.UpsertAsync(testClient);

        // Enroll TOTP
        var secret = await EnrollTotpForUser(user.Id);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Login to get MFA challenge
        var loginResponse = await client.PostAsJsonAsync(
            $"/api/auth/login?returnUrl=/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}",
            new { email = "test@example.com", password = "Test1234!" });
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var challengeId = loginJson.GetProperty("challengeId").GetString();

        // Generate valid TOTP code
        var totpService = _factory.Services.GetRequiredService<TotpService>();
        var code = totpService.GenerateCode(secret);

        // Verify MFA
        var verifyResponse = await client.PostAsJsonAsync("/api/auth/mfa/verify", new
        {
            challengeId,
            method = "totp",
            code
        });
        verifyResponse.EnsureSuccessStatusCode();

        var verifyJson = await verifyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.Id, verifyJson.GetProperty("userId").GetString());
        Assert.Equal("test@example.com", verifyJson.GetProperty("email").GetString());
    }

    [Fact]
    public async Task MfaVerify_WrongTotp_Returns401()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);

        var testClient = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        testClient!.MfaPolicy = MfaPolicy.Enabled;
        await _factory.ClientStore.UpsertAsync(testClient);

        await EnrollTotpForUser(user.Id);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var loginResponse = await client.PostAsJsonAsync(
            $"/api/auth/login?returnUrl=/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}",
            new { email = "test@example.com", password = "Test1234!" });
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var challengeId = loginJson.GetProperty("challengeId").GetString();

        var verifyResponse = await client.PostAsJsonAsync("/api/auth/mfa/verify", new
        {
            challengeId,
            method = "totp",
            code = "000000"
        });

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, verifyResponse.StatusCode);
    }

    [Fact]
    public async Task MfaVerify_RecoveryCode_WorksOnce_ThenFails()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);

        var testClient = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        testClient!.MfaPolicy = MfaPolicy.Enabled;
        await _factory.ClientStore.UpsertAsync(testClient);

        await EnrollTotpForUser(user.Id);

        // Generate recovery codes
        var recoveryService = _factory.Services.GetRequiredService<RecoveryCodeService>();
        var (codes, creds) = recoveryService.Generate(user.Id);
        foreach (var cred in creds)
            await _factory.MfaStore.CreateCredentialAsync(cred);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // First use: should succeed
        var loginResponse = await client.PostAsJsonAsync(
            $"/api/auth/login?returnUrl=/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}",
            new { email = "test@example.com", password = "Test1234!" });
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var challengeId = loginJson.GetProperty("challengeId").GetString();

        var verifyResponse = await client.PostAsJsonAsync("/api/auth/mfa/verify", new
        {
            challengeId,
            method = "recovery",
            code = codes[0]
        });
        verifyResponse.EnsureSuccessStatusCode();

        // Second use of same code: should fail
        var loginResponse2 = await client.PostAsJsonAsync(
            $"/api/auth/login?returnUrl=/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}",
            new { email = "test@example.com", password = "Test1234!" });
        var loginJson2 = await loginResponse2.Content.ReadFromJsonAsync<JsonElement>();
        var challengeId2 = loginJson2.GetProperty("challengeId").GetString();

        var verifyResponse2 = await client.PostAsJsonAsync("/api/auth/mfa/verify", new
        {
            challengeId = challengeId2,
            method = "recovery",
            code = codes[0]
        });
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, verifyResponse2.StatusCode);
    }

    [Fact]
    public async Task MfaVerify_InvalidChallenge_Returns400()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsJsonAsync("/api/auth/mfa/verify", new
        {
            challengeId = "nonexistent",
            method = "totp",
            code = "123456"
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HookOverrides_ClientPolicy()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();

        // Client has Enabled, but hook overrides to Required
        var testClient = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        testClient!.MfaPolicy = MfaPolicy.Enabled;
        await _factory.ClientStore.UpsertAsync(testClient);

        _factory.AuthHook.MfaPolicyOverride = (_, _, _, _) => MfaPolicy.Required;

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync(
            $"/api/auth/login?returnUrl=/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}",
            new { email = "test@example.com", password = "Test1234!" });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // User is not enrolled, Required policy → mfaSetupRequired
        Assert.True(json.GetProperty("mfaSetupRequired").GetBoolean());
    }

    [Fact]
    public async Task AdminResetMfa_UserCanLoginWithoutMfa()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);

        var testClient = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        testClient!.MfaPolicy = MfaPolicy.Enabled;
        await _factory.ClientStore.UpsertAsync(testClient);

        await EnrollTotpForUser(user.Id);

        var adminClient = _factory.CreateClient();
        var adminToken = await _factory.GetAdminTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        // Admin resets MFA
        var resetResponse = await adminClient.DeleteAsync($"/api/v1/profile/{user.Id}/mfa");
        resetResponse.EnsureSuccessStatusCode();

        // Now login should succeed without MFA challenge
        var loginClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var loginResponse = await loginClient.PostAsJsonAsync(
            $"/api/auth/login?returnUrl=/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}",
            new { email = "test@example.com", password = "Test1234!" });
        loginResponse.EnsureSuccessStatusCode();

        var json = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("userId", out _));
        Assert.False(json.TryGetProperty("mfaRequired", out _));
    }

    private async Task<byte[]> EnrollTotpForUser(string userId)
    {
        var totpService = _factory.Services.GetRequiredService<TotpService>();
        var secret = totpService.GenerateSecret();
        var secretBase64 = Convert.ToBase64String(secret);

        var cred = new MfaCredential
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Type = MfaCredentialType.Totp,
            Name = "Authenticator app",
            SecretProtected = secretBase64, // PlaintextSecretProvider in tests
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _factory.MfaStore.CreateCredentialAsync(cred);
        return secret;
    }
}

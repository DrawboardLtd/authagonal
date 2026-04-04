using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

public class MfaSetupEndpointTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task MfaStatus_NoCredentials_ReturnsDisabled()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();

        var client = await LoginAndGetAuthenticatedClient();
        var response = await client.GetAsync("/api/auth/mfa/status");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("enabled").GetBoolean());
        Assert.Empty(json.GetProperty("methods").EnumerateArray().ToList());
    }

    [Fact]
    public async Task TotpSetup_ReturnsQrCodeAndSetupToken()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();

        var client = await LoginAndGetAuthenticatedClient();
        var response = await client.PostAsync("/api/auth/mfa/totp/setup", null);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("setupToken", out _));
        Assert.True(json.TryGetProperty("qrCodeDataUri", out var qr));
        Assert.StartsWith("data:image/png;base64,", qr.GetString());
    }

    [Fact]
    public async Task TotpConfirm_ValidCode_Succeeds()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();

        var client = await LoginAndGetAuthenticatedClient();

        // Setup TOTP
        var setupResponse = await client.PostAsync("/api/auth/mfa/totp/setup", null);
        var setupJson = await setupResponse.Content.ReadFromJsonAsync<JsonElement>();
        var setupToken = setupJson.GetProperty("setupToken").GetString();

        // Get the secret from the credential
        var cred = await _factory.MfaStore.GetCredentialAsync(user.Id, setupToken!);
        Assert.NotNull(cred);
        var secretBase64 = cred!.SecretProtected!; // PlaintextSecretProvider
        var secret = Convert.FromBase64String(secretBase64);

        // Generate valid code
        var totpService = _factory.Services.GetRequiredService<TotpService>();
        var code = totpService.GenerateCode(secret);

        // Confirm
        var confirmResponse = await client.PostAsJsonAsync("/api/auth/mfa/totp/confirm", new
        {
            setupToken,
            code
        });
        confirmResponse.EnsureSuccessStatusCode();

        // Verify MFA is now enabled
        var updatedUser = await _factory.UserStore.GetAsync(user.Id);
        Assert.True(updatedUser!.MfaEnabled);
    }

    [Fact]
    public async Task TotpConfirm_InvalidCode_Returns400()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();

        var client = await LoginAndGetAuthenticatedClient();

        // Setup TOTP
        var setupResponse = await client.PostAsync("/api/auth/mfa/totp/setup", null);
        var setupJson = await setupResponse.Content.ReadFromJsonAsync<JsonElement>();
        var setupToken = setupJson.GetProperty("setupToken").GetString();

        // Confirm with wrong code
        var confirmResponse = await client.PostAsJsonAsync("/api/auth/mfa/totp/confirm", new
        {
            setupToken,
            code = "000000"
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, confirmResponse.StatusCode);
    }

    [Fact]
    public async Task RecoveryGenerate_RequiresPrimaryMethod()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();

        var client = await LoginAndGetAuthenticatedClient();

        // Try to generate recovery codes without TOTP enrolled
        var response = await client.PostAsync("/api/auth/mfa/recovery/generate", null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RecoveryGenerate_WithTotp_ReturnsCodes()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();

        // Enroll TOTP directly
        var totpService = _factory.Services.GetRequiredService<TotpService>();
        var secret = totpService.GenerateSecret();
        await _factory.MfaStore.CreateCredentialAsync(new MfaCredential
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            Type = MfaCredentialType.Totp,
            Name = "Authenticator app",
            SecretProtected = Convert.ToBase64String(secret),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var client = await LoginAndGetAuthenticatedClient();
        var response = await client.PostAsync("/api/auth/mfa/recovery/generate", null);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var codes = json.GetProperty("codes").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Equal(10, codes.Count);
    }

    [Fact]
    public async Task DeleteCredential_RemovesAndUpdatesUser()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);

        // Enroll TOTP
        var credId = Guid.NewGuid().ToString("N");
        await _factory.MfaStore.CreateCredentialAsync(new MfaCredential
        {
            Id = credId,
            UserId = user.Id,
            Type = MfaCredentialType.Totp,
            Name = "Authenticator app",
            SecretProtected = Convert.ToBase64String(new byte[20]),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var client = await LoginAndGetAuthenticatedClient();
        var response = await client.DeleteAsync($"/api/auth/mfa/credentials/{credId}");
        response.EnsureSuccessStatusCode();

        // MfaEnabled should be false now
        var updatedUser = await _factory.UserStore.GetAsync(user.Id);
        Assert.False(updatedUser!.MfaEnabled);
    }

    private async Task<HttpClient> LoginAndGetAuthenticatedClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });
        return client;
    }
}

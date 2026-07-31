using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// A client configured <see cref="RefreshTokenUsage.ReUse"/> must be able to refresh twice.
/// </summary>
/// <remarks>
/// The setting was persisted, seeded, migrated and documented but read nowhere, so every client got
/// OneTime. For a ReUse client that is worse than a preference being ignored: its second, entirely
/// ordinary refresh presents the same token again, which strict rotation reads as REPLAY and answers
/// by revoking the user's whole grant family. The operator's explicit configuration produced a
/// sign-out.
/// </remarks>
public sealed class RefreshTokenUsageTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task ReUseClient_CanRefreshTwiceWithTheSameToken()
    {
        await SetUsageAsync(RefreshTokenUsage.ReUse);
        var tokens = await GetTokensViaPkce();
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var first = await RefreshAsync(refreshToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The same token again. Under rotation this is replay; under ReUse it is the point.
        var second = await RefreshAsync(refreshToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // And the handle is unchanged, which is what "reuse" means.
        var returned = (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("refresh_token").GetString();
        Assert.Equal(refreshToken, returned);
    }

    [Fact]
    public async Task OneTimeClient_StillRotatesAndStillDetectsReplay()
    {
        await SetUsageAsync(RefreshTokenUsage.OneTime);
        var tokens = await GetTokensViaPkce();
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var rotated = await RefreshAsync(refreshToken);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.NotEqual(refreshToken,
            (await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await RefreshAsync(refreshToken)).StatusCode);
    }

    private async Task SetUsageAsync(RefreshTokenUsage usage)
    {
        var client = await _factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId);
        client!.RefreshTokenUsage = usage;
        await _factory.ClientStore.UpsertAsync(client);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = AuthagonalTestFactory.TestClientId,
        }));

    private async Task<JsonElement> GetTokensViaPkce()
    {
        await _factory.SeedTestUserAsync();
        await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var authResponse = await _client.GetAsync(
            $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            "&response_type=code&scope=openid+offline_access&state=t" +
            $"&code_challenge={challenge}&code_challenge_method=S256");

        var code = HttpUtility.ParseQueryString(authResponse.Headers.Location!.Query)["code"]!;

        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "https://app.test/callback",
                ["code_verifier"] = verifier,
                ["client_id"] = AuthagonalTestFactory.TestClientId,
            }));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}

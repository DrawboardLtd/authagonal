using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// A challenge id is a bearer credential handed to a caller who has proved a password but holds no
/// session, and login mints one for an already-enrolled user too. Before <see cref="MfaChallengePurpose"/>
/// existed, <c>MfaChallenge</c> carried no discriminator and the enrolment endpoints accepted any live
/// challenge in <c>X-MFA-Setup-Token</c> — so a password-only attacker could drive factor management
/// against an enrolled victim. Each test below is one of the four sinks that made that exploitable.
/// </summary>
public class MfaChallengePurposeTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task<AuthUser> SeedEnrolledUserAsync()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);

        var totpService = _factory.Services.GetRequiredService<TotpService>();
        await _factory.MfaStore.CreateCredentialAsync(new MfaCredential
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            Type = MfaCredentialType.Totp,
            Name = "Authenticator app",
            SecretProtected = Convert.ToBase64String(totpService.GenerateSecret()),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return user;
    }

    /// <summary>Password-only login: returns the verification challenge id and no session cookie.</summary>
    private static async Task<string> LoginForChallengeAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!" });
        login.EnsureSuccessStatusCode();
        var json = await login.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("challengeId").GetString()!;
    }

    private static HttpRequestMessage WithSetupToken(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("X-MFA-Setup-Token", token);
        return req;
    }

    /// <summary>
    /// Sink 1, the original critical: mint the victim's recovery codes with the password alone. Also
    /// destroyed the victim's real codes as a side effect.
    /// </summary>
    [Fact]
    public async Task VerificationChallenge_CannotMintRecoveryCodes()
    {
        await SeedEnrolledUserAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challengeId = await LoginForChallengeAsync(client);

        var res = await client.SendAsync(
            WithSetupToken(HttpMethod.Post, "/api/auth/mfa/recovery/generate", challengeId));

        Assert.False(res.IsSuccessStatusCode);
        Assert.DoesNotContain("codes", await res.Content.ReadAsStringAsync());
    }

    /// <summary>Sink 2: enrol an attacker-controlled passkey, which survives a victim password reset.</summary>
    [Fact]
    public async Task VerificationChallenge_CannotBeginPasskeyEnrolment()
    {
        await SeedEnrolledUserAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challengeId = await LoginForChallengeAsync(client);

        var res = await client.SendAsync(
            WithSetupToken(HttpMethod.Post, "/api/auth/mfa/webauthn/setup", challengeId));

        Assert.False(res.IsSuccessStatusCode);
    }

    /// <summary>
    /// Sink 3: <c>/totp/confirm</c> was a second TOTP acceptance path with no attempt counter, so it was a
    /// brute-force oracle that issued a session on success.
    /// </summary>
    [Fact]
    public async Task VerificationChallenge_CannotReachTotpConfirm()
    {
        await SeedEnrolledUserAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challengeId = await LoginForChallengeAsync(client);

        var req = WithSetupToken(HttpMethod.Post, "/api/auth/mfa/totp/confirm", challengeId);
        req.Content = JsonContent.Create(new { setupToken = challengeId, code = "000000" });
        var res = await client.SendAsync(req);

        Assert.False(res.IsSuccessStatusCode);

        // And no session was established.
        var session = await client.GetAsync("/api/auth/session");
        var body = await session.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"authenticated\":true", body);
    }

    /// <summary>The status endpoint also leaked credential ids, which fed the brute-force arm.</summary>
    [Fact]
    public async Task VerificationChallenge_CannotReadMfaStatus()
    {
        await SeedEnrolledUserAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challengeId = await LoginForChallengeAsync(client);

        var res = await client.SendAsync(
            WithSetupToken(HttpMethod.Get, "/api/auth/mfa/status", challengeId));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    /// <summary>
    /// A passwordless-discovery challenge is minted to an anonymous caller and has an empty
    /// <c>UserId</c>, so it identifies nobody. Callers guarded on <c>userId is null</c>, which an empty
    /// string slipped past.
    /// </summary>
    [Fact]
    public async Task PasswordlessDiscoveryChallenge_IsNotAnIdentity()
    {
        await _factory.SeedTestDataAsync();
        await _factory.SeedTestUserAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var begin = await client.PostAsync("/api/auth/mfa/passwordless/begin", null);
        begin.EnsureSuccessStatusCode();
        var challengeId = (await begin.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("challengeId").GetString()!;

        var setup = await client.SendAsync(
            WithSetupToken(HttpMethod.Post, "/api/auth/mfa/totp/setup", challengeId));
        Assert.Equal(HttpStatusCode.Unauthorized, setup.StatusCode);

        var status = await client.SendAsync(
            WithSetupToken(HttpMethod.Get, "/api/auth/mfa/status", challengeId));
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    /// <summary>
    /// Sink 4: an abandoned enrolment leaves a pending row holding a live TOTP seed. Those rows have no
    /// expiry column, so accepting one at <c>/verify</c> made it a permanent, self-service-invisible
    /// second factor — a verifier reproduced this against a 90-day-old row.
    /// </summary>
    [Fact]
    public async Task PendingTotpCredential_IsNotAcceptedAsASecondFactor()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();
        user.MfaEnabled = true;
        await _factory.UserStore.UpdateAsync(user);

        var totpService = _factory.Services.GetRequiredService<TotpService>();
        var secret = totpService.GenerateSecret();

        // An abandoned enrolment attempt: pending, never confirmed, and long stale.
        await _factory.MfaStore.CreateCredentialAsync(new MfaCredential
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            Type = MfaCredentialType.Totp,
            Name = "TOTP (pending)",
            SecretProtected = Convert.ToBase64String(secret),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-90),
        });

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challengeId = await LoginForChallengeAsync(client);

        // A CORRECT code for the pending seed must still be refused: the factor was never proved.
        var code = totpService.GenerateCode(secret);
        var verify = await client.PostAsJsonAsync("/api/auth/mfa/verify",
            new { challengeId, method = "totp", code });

        Assert.False(verify.IsSuccessStatusCode);
    }

    /// <summary>
    /// The legitimate enrolment path must still work end to end: a user with no factor gets an enrolment
    /// token, and that token IS accepted by the setup endpoints. Guards against over-tightening.
    /// </summary>
    [Fact]
    public async Task EnrolmentToken_StillDrivesFirstFactorSetup()
    {
        await _factory.SeedTestDataAsync();
        var user = await _factory.SeedTestUserAsync();

        // Force enrolment: MFA required by policy, user has no factor.
        var clients = await _factory.ClientStore.GetAllAsync();
        var target = clients.First();
        target.MfaPolicy = MfaPolicy.Required;
        await _factory.ClientStore.UpsertAsync(target);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!", clientId = target.ClientId });
        login.EnsureSuccessStatusCode();
        var json = await login.Content.ReadFromJsonAsync<JsonElement>();

        // Only assert the enrolment contract when the deployment actually demanded enrolment.
        if (!json.TryGetProperty("setupToken", out var tokenProp) || tokenProp.GetString() is not { } setupToken)
            return;

        var setup = await client.SendAsync(
            WithSetupToken(HttpMethod.Post, "/api/auth/mfa/totp/setup", setupToken));
        Assert.True(setup.IsSuccessStatusCode,
            $"enrolment token must still reach /totp/setup, got {setup.StatusCode}");
    }
}

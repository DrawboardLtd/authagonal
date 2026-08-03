using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The MFA enrolment token is a credential, so it does not travel in a URL.
/// </summary>
/// <remarks>
/// <c>MfaSetupEndpoints.ResolveUserIdAsync</c> accepts an enrolment token as the sole identity for every
/// enrolment endpoint, and <c>/totp/confirm</c> signs a full session cookie for that user once an enrolment
/// it accepted completes. The holder needs nothing else from the victim: enrol your own authenticator, get
/// the cookie, and you are them.
/// <para>
/// The federated path put exactly that value in a redirect — <c>Results.Redirect(".../mfa-setup?setupToken=…")</c>
/// — so it appeared in a <c>Location</c> header, then in a real <c>GET</c> request line, and from there in
/// browser history, in the <c>Referer</c> of any cross-origin subresource the page loaded, and in every
/// access and proxy log on the way. It is an <c>HttpOnly</c> cookie now, which the SPA never reads.
/// </para>
/// </remarks>
public sealed class MfaSetupTokenTransportTests : IAsyncLifetime
{
    private const string SetupCookieName = "mfa_setup";

    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    /// <summary>
    /// The cookie alone reaches the enrolment endpoints, so nothing has to carry the token in JavaScript.
    /// </summary>
    /// <remarks>
    /// This is what lets the federated redirect drop the query parameter: the SPA sends no
    /// <c>X-MFA-Setup-Token</c> header at all and the server resolves identity from the cookie it set.
    /// </remarks>
    [Fact]
    public async Task TheEnrolmentCookieIsAcceptedAsIdentityWithNoHeader()
    {
        var user = await _factory.SeedTestUserAsync();
        var token = await StoreEnrolmentChallengeAsync(user.Id);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/mfa/totp/setup");
        request.Headers.Add("Cookie", $"{SetupCookieName}={token}");
        request.Headers.Add("Origin", AuthagonalTestFactory.TestIssuer);

        var response = await _client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, $"the enrolment cookie must reach /totp/setup, got {response.StatusCode}");
    }

    /// <summary>A cookie carrying a challenge that confers no enrolment authority is refused.</summary>
    /// <remarks>
    /// The cookie is a second doorway into the same gate, so it must be the same gate. A verification
    /// challenge belongs to a user who already HAS a factor; accepting one here would let a password-only
    /// caller drive enrolment against an enrolled victim.
    /// </remarks>
    [Fact]
    public async Task AVerificationChallengeInTheCookieIsRefused()
    {
        var user = await _factory.SeedTestUserAsync();
        var token = await StoreEnrolmentChallengeAsync(user.Id, MfaChallengePurpose.Verify);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/mfa/totp/setup");
        request.Headers.Add("Cookie", $"{SetupCookieName}={token}");
        request.Headers.Add("Origin", AuthagonalTestFactory.TestIssuer);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The status endpoint reports the forced enrolment, so the page does not need the token to know.
    /// </summary>
    /// <remarks>
    /// The setup page decided "is this a forced enrolment" from the presence of a <c>setupToken</c> query
    /// parameter, which is the reason the token was in the URL at all. With the token in an HttpOnly cookie
    /// the page cannot see it, so the server — which knows — says so instead. Without this the federated
    /// user would be shown a Skip link on an enrolment they are not allowed to skip.
    /// </remarks>
    [Fact]
    public async Task StatusReportsForcedForATokenCallerAndNotForASession()
    {
        var user = await _factory.SeedTestUserAsync();
        var token = await StoreEnrolmentChallengeAsync(user.Id);

        var byCookie = new HttpRequestMessage(HttpMethod.Get, "/api/auth/mfa/status");
        byCookie.Headers.Add("Cookie", $"{SetupCookieName}={token}");
        byCookie.Headers.Add("Origin", AuthagonalTestFactory.TestIssuer);
        var tokenStatus = await _client.SendAsync(byCookie);
        tokenStatus.EnsureSuccessStatusCode();
        Assert.True((await tokenStatus.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("forced").GetBoolean());

        // A signed-in caller is enrolling voluntarily and may leave.
        var session = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var login = await session.PostAsJsonAsync("/api/auth/login",
            new { email = user.Email, password = "Test1234!" });
        login.EnsureSuccessStatusCode();

        var sessionStatus = await session.GetAsync("/api/auth/mfa/status");
        sessionStatus.EnsureSuccessStatusCode();
        Assert.False((await sessionStatus.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("forced").GetBoolean());
    }

    private async Task<string> StoreEnrolmentChallengeAsync(
        string userId, MfaChallengePurpose purpose = MfaChallengePurpose.Enrol)
    {
        var challengeId = Guid.NewGuid().ToString("N");
        await _factory.MfaStore.StoreChallengeAsync(new MfaChallenge
        {
            ChallengeId = challengeId,
            UserId = userId,
            Purpose = purpose,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        });
        return challengeId;
    }
}

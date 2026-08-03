using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

public sealed class PushedAuthorizationTests : IAsyncLifetime
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
    public async Task Par_PublicClient_Returns201WithRequestUri()
    {
        var form = BuildPushedForm();
        var response = await _client.PostAsync("/connect/par", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var requestUri = json.GetProperty("request_uri").GetString();
        Assert.StartsWith("urn:ietf:params:oauth:request_uri:", requestUri);
        Assert.Equal(90, json.GetProperty("expires_in").GetInt32());
    }

    [Fact]
    public async Task Par_RequestUriInBody_Rejected()
    {
        var fields = BasePushedFields();
        fields["request_uri"] = "urn:ietf:params:oauth:request_uri:spoof";
        var response = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Par_UnknownClient_Returns401()
    {
        var fields = BasePushedFields();
        fields["client_id"] = "nonexistent-client";
        var response = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_client", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Par_ConfidentialClientMissingSecret_Returns401()
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = AuthagonalTestFactory.AdminClientId,
            ["response_type"] = "code",
            ["scope"] = "openid",
        };
        var response = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authorize_UnknownRequestUri_ReturnsInvalidRequest()
    {
        var url = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}&request_uri={Uri.EscapeDataString("urn:ietf:params:oauth:request_uri:bogus")}";

        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Authorize_PushedRequestForDifferentClient_Rejected()
    {
        // Push as TestClient.
        var parForm = BuildPushedForm();
        var parResponse = await _client.PostAsync("/connect/par", parForm);
        var parJson = await parResponse.Content.ReadFromJsonAsync<JsonElement>();
        var requestUri = parJson.GetProperty("request_uri").GetString()!;

        // Try to consume as AdminClient — should fail cleanly without leaking.
        var url = $"/connect/authorize?client_id={AuthagonalTestFactory.AdminClientId}&request_uri={Uri.EscapeDataString(requestUri)}";
        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Par_ThenAuthorize_Unauthenticated_RedirectsToLoginWithRequestUri()
    {
        var parResponse = await _client.PostAsync("/connect/par", BuildPushedForm());
        var parJson = await parResponse.Content.ReadFromJsonAsync<JsonElement>();
        var requestUri = parJson.GetProperty("request_uri").GetString()!;

        var url = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}&request_uri={Uri.EscapeDataString(requestUri)}";
        var response = await _client.GetAsync(url);

        // Should redirect to login — request_uri must NOT be consumed yet, so the user can
        // round-trip through login and retry.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.ToString());

        // Re-hitting the same /authorize should still succeed (load, not consume).
        var retry = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
    }

    [Fact]
    public async Task Par_PromptLogin_CompletesAfterFreshLogin_NoLoop()
    {
        // H1: a PAR carrying prompt=login must not loop forever. The prompt rides the pushed payload (not
        // the live query, so it can't be stripped), so after the user logs in — auth_time >= the request's
        // CreatedAt — the return trip to /authorize must PROCEED instead of bouncing back to /login again.
        await _factory.SeedTestUserAsync();

        var fields = BasePushedFields();
        fields["prompt"] = "login";
        var parResponse = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(fields));
        var requestUri = (await parResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("request_uri").GetString()!;
        var url = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}&request_uri={Uri.EscapeDataString(requestUri)}";

        // Unauthenticated → redirect to login (request_uri not consumed).
        var first = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Contains("/login", first.Headers.Location!.ToString());

        // Log in — sets a fresh auth_time on the cookie.
        var login = await _client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Return trip: prompt=login is now satisfied by the fresh session, so it must NOT bounce to /login.
        var second = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.DoesNotContain("/login", second.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Discovery_AdvertisesParEndpoint()
    {
        var response = await _client.GetAsync("/.well-known/openid-configuration");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var endpoint = json.GetProperty("pushed_authorization_request_endpoint").GetString();
        Assert.NotNull(endpoint);
        Assert.EndsWith("/connect/par", endpoint);
    }

    private static FormUrlEncodedContent BuildPushedForm() => new(BasePushedFields());

    // -----------------------------------------------------------------------
    // The record has to outlive the interactive leg it was designed around
    // -----------------------------------------------------------------------

    /// <summary>
    /// The pushed record survives longer than the advertised 90 seconds once /authorize has picked it up.
    /// </summary>
    /// <remarks>
    /// The record is deliberately NOT consumed until the authorization code is issued, so the user can round
    /// trip through login, and both hosts keep <c>request_uri</c> on the returnUrl they hand to the login app.
    /// Its lifetime was a hard-coded 90 seconds measured from the PAR POST, with no options hook — but
    /// everything the user must do sits between those two points: load the SPA, enter credentials, clear MFA
    /// (a TOTP code, or one emailed to them), possibly a step-up that signs the session out and starts over,
    /// then read and answer the consent screen. So an interactive PAR flow broke whenever a human took as
    /// long as a human takes, and the failure surfaced to the END USER mid-login as an invalid_request.
    /// </remarks>
    [Fact]
    public async Task Par_RecordIsExtendedOnFirstAuthorize_ToCoverTheInteractiveLeg()
    {
        var pushed = await _client.PostAsync("/connect/par", BuildPushedForm());
        var requestUri = (await pushed.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("request_uri").GetString()!;

        // As pushed: the advertised window, and no more.
        var atPush = await _factory.GrantStore.GetAsync(requestUri);
        Assert.NotNull(atPush);
        Assert.True(atPush.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(90),
            $"as pushed the record should expire within 90s, got {atPush.ExpiresAt}");

        // Unauthenticated /authorize — the first hop of a real interactive flow, which sends the user to
        // login carrying the request_uri.
        var authorize = await _client.GetAsync($"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&request_uri={Uri.EscapeDataString(requestUri)}");
        Assert.Equal(HttpStatusCode.Found, authorize.StatusCode);

        var afterPickup = await _factory.GrantStore.GetAsync(requestUri);
        Assert.NotNull(afterPickup);
        Assert.True(afterPickup.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10),
            $"after pickup the record must survive the interactive leg, got {afterPickup.ExpiresAt}");
    }

    /// <summary>The extension is a fixed deadline from the push, not a window that slides on every load.</summary>
    /// <remarks>
    /// A sliding window would let anyone holding the request_uri keep the row alive indefinitely by replaying
    /// /authorize. Computing an absolute deadline from <c>CreatedAt</c> makes the extension idempotent and
    /// keeps the whole flow bounded from the moment the client pushed it.
    /// </remarks>
    [Fact]
    public async Task Par_RepeatedAuthorizeDoesNotSlideTheDeadline()
    {
        var pushed = await _client.PostAsync("/connect/par", BuildPushedForm());
        var requestUri = (await pushed.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("request_uri").GetString()!;

        var url = $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&request_uri={Uri.EscapeDataString(requestUri)}";

        await _client.GetAsync(url);
        var first = (await _factory.GrantStore.GetAsync(requestUri))!.ExpiresAt;

        await _client.GetAsync(url);
        var second = (await _factory.GrantStore.GetAsync(requestUri))!.ExpiresAt;

        Assert.Equal(first, second);
    }

    // -----------------------------------------------------------------------
    // The request is bounded before it is stored
    // -----------------------------------------------------------------------

    /// <summary>
    /// An oversized pushed request is refused rather than serialised into a grant row.
    /// </summary>
    /// <remarks>
    /// Nothing bounded the body, the field count or the value lengths — no RequestSizeLimit, no FormOptions
    /// tuning and no MaxRequestBodySize override anywhere in src/ — so the ceiling was Kestrel's 30 MB
    /// default and ASP.NET's 1024 form values, and the whole form is serialised into one row. The only bound
    /// was a per-client throttle that counts REQUESTS, not bytes: 300/minute of 30 MB is 9 GB a minute of
    /// stored rows, from a public client that authenticates on a bare client_id readable from any SPA's
    /// network traffic.
    /// </remarks>
    [Fact]
    public async Task Par_OversizedRequest_IsRefused()
    {
        var fields = BasePushedFields();
        fields["state"] = new string('x', 200 * 1024);

        var response = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("invalid_request",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    /// <summary>A request with far more fields than an authorization request has is refused.</summary>
    /// <remarks>
    /// Separate from body size on purpose: a thousand short fields is a small body and a large dictionary,
    /// and it is the dictionary that gets serialised into the row.
    /// </remarks>
    [Fact]
    public async Task Par_TooManyFields_IsRefused()
    {
        var fields = BasePushedFields();
        for (var i = 0; i < 200; i++) fields[$"pad{i}"] = "x";

        var response = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>The control: an ordinary pushed request is unaffected by either bound.</summary>
    [Fact]
    public async Task Par_OrdinarySizedRequest_StillSucceeds()
    {
        // Generous next to a real authorization request, and well inside the bounds. Scopes stay registered
        // ones — an unregistered scope is refused on its own merits, which would make this control pass for
        // the wrong reason.
        var fields = BasePushedFields();
        fields["login_hint"] = new string('u', 2000);
        fields["nonce"] = new string('n', 512);
        for (var i = 0; i < 30; i++) fields[$"ext{i}"] = new string('v', 256);

        var response = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static Dictionary<string, string> BasePushedFields() => new()
    {
        ["client_id"] = AuthagonalTestFactory.TestClientId,
        ["response_type"] = "code",
        ["redirect_uri"] = "https://app.test/callback",
        ["scope"] = "openid profile",
        ["state"] = "xyz",
        ["code_challenge"] = GenerateCodeChallenge("verifier-of-sufficient-length-1234"),
        ["code_challenge_method"] = "S256",
    };

    private static string GenerateCodeChallenge(string verifier)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(verifier));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

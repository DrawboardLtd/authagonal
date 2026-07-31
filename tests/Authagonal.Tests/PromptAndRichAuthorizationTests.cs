using System.Net;
using System.Net.Http.Json;
using System.Web;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// F10 / F4 — the two authorize-endpoint parameters the OP answered by ignoring: a prompt demand it
/// resolved by rendering UI anyway, and a rich-authorization request it dropped on the floor.
/// </summary>
public sealed class PromptAndRichAuthorizationTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // F10 — prompt=none
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PromptNone_WithNoSession_ReturnsLoginRequiredToTheClient()
    {
        var response = await _client.GetAsync(AuthorizeUrl(prompt: "none"));

        // OIDC Core §3.1.2.1: "The Authorization Server MUST NOT display any authentication or
        // consent user interface pages." It answered with a 302 to the login SPA — a login form
        // rendered inside the hidden iframe a silent renewal runs in, which the user never sees and
        // the RP cannot distinguish from a hang.
        var location = AssertRedirectTo(response, "https://app.test/callback");
        Assert.Equal("login_required", location["error"]);
        Assert.Equal("xyz", location["state"]);
    }

    [Fact]
    public async Task PromptNone_WithASession_StillIssuesACode()
    {
        await SignInAsync();

        var response = await _client.GetAsync(AuthorizeUrl(prompt: "none"));

        // The point of prompt=none is that it succeeds silently when it can. Turning every
        // no-interaction request into an error would break renewal just as thoroughly.
        var location = AssertRedirectTo(response, "https://app.test/callback");
        Assert.False(location.AllKeys.Contains("error"), $"unexpected error: {location["error"]}");
        Assert.False(string.IsNullOrEmpty(location["code"]));
    }

    [Fact]
    public async Task PromptNone_CombinedWithAnotherValue_IsInvalidRequest()
    {
        // §3.1.2.1: "If this parameter contains none with any other value, an error is returned."
        // The combination is self-contradictory, so honouring either half picks one for the RP.
        var response = await _client.GetAsync(AuthorizeUrl(prompt: "none login"));

        var location = AssertRedirectTo(response, "https://app.test/callback");
        Assert.Equal("invalid_request", location["error"]);
    }

    [Fact]
    public async Task UnknownPromptValue_IsRefusedNotDropped()
    {
        var response = await _client.GetAsync(AuthorizeUrl(prompt: "invent_a_prompt"));

        var location = AssertRedirectTo(response, "https://app.test/callback");
        Assert.Equal("invalid_request", location["error"]);
    }

    [Fact]
    public async Task PromptSelectAccount_ForcesFreshAuthentication()
    {
        await SignInAsync();

        // A single-session OP offers account choice by returning the user to the login screen.
        // Ignoring the value entirely told the RP the user had chosen when they never saw a choice.
        var response = await _client.GetAsync(AuthorizeUrl(prompt: "select_account"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("/login", location, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // F4 — authorization_details at the authorization endpoint
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AuthorizationDetails_AtAuthorize_IsRefused()
    {
        await SignInAsync();

        var details = """[{"type":"payments","actions":["initiate"]}]""";
        var response = await _client.GetAsync(AuthorizeUrl() + "&authorization_details=" + Uri.EscapeDataString(details));

        // RFC 9396 §5: the AS "MUST abort processing and respond with an error
        // invalid_authorization_details". It was read by nothing, so the client got a code and an
        // access token with no authorization_details claim and no indication anything was dropped —
        // it believes it asked for a constrained grant and holds a broader one.
        var location = AssertRedirectTo(response, "https://app.test/callback");
        Assert.Equal("invalid_authorization_details", location["error"]);
        Assert.Null(location["code"]);
    }

    [Fact]
    public async Task AuthorizationDetails_AtPar_IsRefusedWithTheSameCode()
    {
        var response = await _client.PostAsync("/connect/par", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = AuthagonalTestFactory.TestClientId,
                ["response_type"] = "code",
                ["redirect_uri"] = "https://app.test/callback",
                ["scope"] = "openid",
                ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                ["code_challenge_method"] = "S256",
                ["authorization_details"] = """[{"type":"payments","actions":["initiate"]}]""",
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("invalid_authorization_details", body.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string AuthorizeUrl(string? prompt = null)
    {
        var url = "/connect/authorize"
            + $"?client_id={AuthagonalTestFactory.TestClientId}"
            + "&response_type=code"
            + "&redirect_uri=" + Uri.EscapeDataString("https://app.test/callback")
            + "&scope=openid"
            + "&state=xyz"
            + "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
            + "&code_challenge_method=S256";

        return prompt is null ? url : url + "&prompt=" + Uri.EscapeDataString(prompt);
    }

    private async Task SignInAsync()
    {
        await _factory.SeedTestUserAsync();
        var login = await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "test@example.com", password = "Test1234!" });
        Assert.True(login.IsSuccessStatusCode, $"login failed: {login.StatusCode}");
    }

    private static System.Collections.Specialized.NameValueCollection AssertRedirectTo(
        HttpResponseMessage response, string expectedPrefix)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(expectedPrefix, location, StringComparison.Ordinal);
        return HttpUtility.ParseQueryString(new Uri(location).Query);
    }
}

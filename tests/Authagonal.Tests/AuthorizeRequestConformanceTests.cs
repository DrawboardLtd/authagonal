using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Authagonal.Protocol.Endpoints;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// Authorization-endpoint conformance: F229 (redirect_uri components), F212 (repeated parameters),
// F219/F251 (metadata that states what is true, and JAR refused rather than ignored).
//
// The common thread is silent substitution. Each of these took a request that meant one thing and
// processed it as another without telling the client: a userinfo-bearing host that is not the host,
// a duplicate parameter where the first copy wins, a signed request object replaced by the unsigned
// query string it was meant to protect.
// -------------------------------------------------------------------------------------------------
public sealed class RedirectUriMatchingTests
{
    private static readonly string[] Registered = ["https://app.example.com/cb"];

    /// <summary>
    /// The comparison comprehends scheme, host, port, path and query — but not userinfo, so
    /// <c>https://evil.com@app.example.com/cb</c> matched, and the redirect was then rebuilt with the
    /// userinfo intact. The browser shows app.example.com; other readers of the same URL do not agree.
    /// </summary>
    [Theory]
    [InlineData("https://evil.com@app.example.com/cb")]
    [InlineData("https://user:pass@app.example.com/cb")]
    public void UserInfoComponent_IsRefused(string requested)
    {
        Assert.False(AuthorizeRequestSupport.IsRedirectUriRegistered(requested, Registered));
    }

    /// <summary>
    /// Uri silently trims trailing control characters, so the string compared here would not be the
    /// string a proxy or log downstream parses out of the same request.
    /// </summary>
    [Theory]
    [InlineData("https://app.example.com/cb\t")]
    [InlineData("https://app.example.com/cb\n")]
    [InlineData("https://app.example.com/cb ")]
    public void ControlCharactersAndWhitespace_AreRefusedOnTheRawString(string requested)
    {
        Assert.False(AuthorizeRequestSupport.IsRedirectUriRegistered(requested, Registered));
    }

    [Fact]
    public void ExactMatch_IsStillAccepted()
    {
        Assert.True(AuthorizeRequestSupport.IsRedirectUriRegistered("https://app.example.com/cb", Registered));
    }
}

public sealed class AuthorizeRequestConformanceTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    /// <summary>
    /// The OAuth error, wherever it landed. BuildErrorRedirect reflects to the redirect_uri when it
    /// has a validated one and answers a direct JSON body when it does not — and "does not" is itself
    /// the correct behaviour for several of these, so the assertion has to read both shapes.
    /// </summary>
    /// <summary>
    /// The error_description, read from wherever the error landed — the same two shapes
    /// <see cref="ErrorOf"/> handles, since a refusal with no validated redirect_uri is delivered as
    /// JSON rather than a redirect.
    /// </summary>
    private static async Task<string> DescriptionOf(HttpResponseMessage response)
    {
        if (response.Headers.Location is { } location)
        {
            var raw = location.ToString();
            var query = raw.Contains('?') ? raw[raw.IndexOf('?')..] : "";
            return HttpUtility.ParseQueryString(query)["error_description"] ?? "";
        }

        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.TryGetProperty("error_description", out var d)
            ? d.GetString() ?? ""
            : "";
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        if (response.Headers.Location is { } location)
        {
            var raw = location.ToString();
            var query = raw.Contains('?') ? raw[raw.IndexOf('?')..] : "";
            return HttpUtility.ParseQueryString(query)["error"] ?? "";
        }

        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var error)
            ? error.GetString() ?? ""
            : "";
    }

    // ---------------------------------------------------------------------------------------------
    // F212 — OIDC Core §3.1.2.1: "Request parameters ... MUST NOT be included more than once"
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every read took the first occurrence, so a repeated parameter resolved to one value with no
    /// rejection and no log line — leaving this server's reading of the request differing from what
    /// anything else in the path parsed.
    /// </summary>
    [Theory]
    [InlineData("state")]
    [InlineData("scope")]
    [InlineData("nonce")]
    [InlineData("code_challenge_method")]
    public async Task RepeatedSingleValuedParameter_IsRefused(string parameter)
    {
        var response = await _client.GetAsync(Authorize() + $"&{parameter}=duplicate-value");

        Assert.Equal("invalid_request", await ErrorOf(response));
    }

    /// <summary>
    /// A duplicated redirect_uri makes the reflection target itself ambiguous, so the error must be
    /// delivered directly rather than sent to whichever copy won.
    /// </summary>
    [Fact]
    public async Task RepeatedRedirectUri_IsRefusedWithoutReflectingToEither()
    {
        var response = await _client.GetAsync(
            Authorize() + $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}");

        Assert.DoesNotContain("app.test", response.Headers.Location?.ToString() ?? "");
    }

    /// <summary>
    /// The leg the scan structurally could not reach. Once a request_uri is present the parameter
    /// source becomes the PAR payload, so AuthorizeRequest.Read scanned that payload and the query
    /// string — the thing a proxy, WAF or log pipeline in front of this server actually parses — was
    /// never examined at all. A repeated request_uri resolved first-wins and was neither refused nor
    /// logged.
    /// <para>
    /// Both values are deliberately well-formed PAR URNs that were never issued: without the scan the
    /// first is looked up and the request fails as "unknown, expired, or already consumed", so
    /// asserting the error code alone would not distinguish the fix. The description is asserted too.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RepeatedRequestUri_IsRefusedBeforeTheParLookup()
    {
        const string prefix = "urn:ietf:params:oauth:request_uri:";
        var response = await _client.GetAsync(
            Authorize() + $"&request_uri={Uri.EscapeDataString(prefix + "aaa")}" +
                          $"&request_uri={Uri.EscapeDataString(prefix + "bbb")}");

        Assert.Equal("invalid_request", await ErrorOf(response));
        Assert.Contains("request_uri", await DescriptionOf(response));
        Assert.DoesNotContain("unknown, expired", await DescriptionOf(response));
    }

    /// <summary>resource is legitimately repeatable per RFC 8707 §2 and must stay exempt.</summary>
    [Fact]
    public async Task RepeatedResource_IsStillAccepted()
    {
        var response = await _client.GetAsync(
            Authorize() + "&resource=https://api.one.test&resource=https://api.two.test");

        Assert.NotEqual("invalid_request", await ErrorOf(response));
    }

    // ---------------------------------------------------------------------------------------------
    // F219 — OIDC Core §3.1.2.6: refuse an unsupported request object, do not ignore it
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A `request` parameter was dropped on the floor and the unsigned query string honoured in its
    /// place — the exact substitution a request object exists to prevent.
    /// </summary>
    [Fact]
    public async Task RequestObject_IsRefusedAsUnsupported()
    {
        var response = await _client.GetAsync(Authorize() + "&request=eyJhbGciOiJub25lIn0.e30.");

        Assert.Equal("request_not_supported", await ErrorOf(response));
    }

    /// <summary>
    /// A JAR-style request_uri pointing at a client-hosted document is unsupported; an opaque PAR URN
    /// that has expired is a different failure and must keep reporting as one.
    /// </summary>
    [Fact]
    public async Task JarStyleRequestUri_IsRefusedAsUnsupported()
    {
        var response = await _client.GetAsync(
            $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&request_uri={Uri.EscapeDataString("https://client.example/jar/abc")}");

        Assert.Equal("request_uri_not_supported", await ErrorOf(response));
    }

    [Fact]
    public async Task ExpiredParRequestUri_StillReportsAsInvalidRequest()
    {
        var response = await _client.GetAsync(
            $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&request_uri={Uri.EscapeDataString("urn:ietf:params:oauth:request_uri:gone")}");

        Assert.Equal("invalid_request", await ErrorOf(response));
    }

    // ---------------------------------------------------------------------------------------------
    // F219 / F251 — discovery must state what is true rather than inherit a default that is not
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Discovery §3 gives request_uri_parameter_supported a default of TRUE and response_modes a
    /// default of ["query","fragment"]. Omitting them advertised JAR by reference and a fragment
    /// response mode, neither of which this server has.
    /// </summary>
    [Fact]
    public async Task Discovery_StatesRequestObjectAndResponseModeSupportExplicitly()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");

        Assert.False(doc.GetProperty("request_parameter_supported").GetBoolean());
        Assert.False(doc.GetProperty("request_uri_parameter_supported").GetBoolean());
        Assert.Equal<string?[]>(["query"], [.. doc.GetProperty("response_modes_supported")
            .EnumerateArray().Select(e => e.GetString())]);
    }

    /// <summary>A well-formed request with no duplicates still reaches the login redirect.</summary>
    private static string Authorize() =>
        $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
        $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
        "&response_type=code&scope=openid&state=s1&nonce=n1" +
        "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256";
}

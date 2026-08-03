using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// A SAML assertion is only accepted in the browser that asked for it — and only if this server asked.
/// </summary>
/// <remarks>
/// The ACS matched a response's <c>InResponseTo</c> against ANY outstanding AuthnRequest row. The row is
/// global; nothing tied it to a user-agent. And a response carrying no <c>InResponseTo</c> at all was
/// treated as unsolicited and accepted unconditionally.
/// <para>
/// Both make the same attack work, and it needs nothing the victim has: the attacker starts a flow (or
/// doesn't), obtains a valid assertion for their OWN account at the same IdP, and delivers the ACS POST to
/// a victim's browser, which is then signed in as the attacker. Every other §4.1.4.3 rule is satisfied by
/// that legitimately-obtained assertion — Issuer matches the metadata entityID, Destination and Recipient
/// are this ACS, Audience is this SP, the signature verifies, the assertion id is a first sighting.
/// </para>
/// <para>
/// The OIDC federation host in the same server has carried the equivalent defence since F48d and names
/// this exact threat in its own comment; this path had nothing. That is the shape most of this review's
/// findings have: the check exists on the sibling.
/// </para>
/// </remarks>
public sealed class SamlLoginCsrfTests(AzuriteFixture azurite) : IAsyncLifetime, IClassFixture<AzuriteFixture>
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _adminToken = null!;

    public async Task InitializeAsync()
    {
        _factory.AzuriteConnectionString = azurite.ConnectionString;
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    /// <summary>
    /// The response is refused in a browser that did not start the flow, and the victim's pending request
    /// survives it.
    /// </summary>
    /// <remarks>
    /// The survival half matters as much as the refusal: the binding is checked BEFORE the request row is
    /// consumed, so a cross-browser POST cannot burn the row and strand the legitimate login. Checking it
    /// after would turn a login-CSRF attempt into a denial of service on the victim's sign-in.
    /// </remarks>
    [Fact]
    public async Task AResponseIsRefusedInABrowserThatDidNotRequestIt()
    {
        var connectionId = await CreateConnectionAsync();

        // The victim's browser starts the flow and holds the request cookie.
        var login = await _client.GetAsync($"/saml/{connectionId}/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var requestId = ExtractRequestId(login.Headers.Location!.ToString());

        var response = SignedResponse(connectionId, requestId);

        // A different browser — a fresh cookie jar against the same server — posts that response.
        var otherBrowser = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var injected = await otherBrowser.PostAsync($"/saml/{connectionId}/acs", Form(response));

        Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);
        Assert.Contains("saml_binding", await injected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // No session was established in the browser that received the POST.
        Assert.Null(injected.Headers.Location);

        // ...and the request row was not consumed, so the browser that started the flow still completes.
        var legitimate = await _client.PostAsync($"/saml/{connectionId}/acs", Form(response));
        Assert.Equal(HttpStatusCode.Redirect, legitimate.StatusCode);
        Assert.DoesNotContain("error=", legitimate.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The ordinary SP-initiated round trip still works in the browser that started it.</summary>
    /// <remarks>
    /// The control. Without it, a binding that refused everything — a cookie never set, a path scoped
    /// wrongly, a SameSite value the cross-site ACS POST cannot satisfy — would pass the test above.
    /// </remarks>
    [Fact]
    public async Task TheBrowserThatRequestedItSucceeds()
    {
        var connectionId = await CreateConnectionAsync();

        var login = await _client.GetAsync($"/saml/{connectionId}/login");
        var requestId = ExtractRequestId(login.Headers.Location!.ToString());

        var acs = await _client.PostAsync($"/saml/{connectionId}/acs",
            Form(SignedResponse(connectionId, requestId)));

        Assert.Equal(HttpStatusCode.Redirect, acs.StatusCode);
        Assert.DoesNotContain("error=", acs.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An unsolicited (IdP-initiated) response is refused unless the connection opts in.
    /// </summary>
    /// <remarks>
    /// Without this the browser binding above is decorative: the same assertion replays with
    /// <c>InResponseTo</c> simply removed, and the ACS accepted that unconditionally.
    /// </remarks>
    [Fact]
    public async Task AnUnsolicitedResponseIsRefusedByDefaultAndAcceptedOnlyWhenEnabled()
    {
        var connectionId = await CreateConnectionAsync();

        var unsolicited = Form(SignedResponse(connectionId, inResponseTo: null));
        var refused = await _client.PostAsync($"/saml/{connectionId}/acs", unsolicited);

        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Contains("error=saml_unsolicited", refused.Headers.Location!.ToString(), StringComparison.Ordinal);

        // The operator turns IdP-initiated sign-in on for this connection. The profile permits it; the
        // point is that it is a deliberate decision rather than the default.
        var enable = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/saml/connections/{connectionId}")
        {
            Content = new StringContent("""{"allowUnsolicitedResponses":true}""", Encoding.UTF8, "application/json"),
        };
        enable.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        var enabled = await _client.SendAsync(enable);
        Assert.True(enabled.IsSuccessStatusCode, $"enable failed: {enabled.StatusCode}");

        // A fresh assertion — the previous one's id has now been seen, and replay detection is separate.
        var accepted = await _client.PostAsync($"/saml/{connectionId}/acs",
            Form(SignedResponse(connectionId, inResponseTo: null, subject: "idp-initiated@example.com")));

        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.DoesNotContain("error=", accepted.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static FormUrlEncodedContent Form(string samlResponse)
        => new(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse });

    private string SignedResponse(string connectionId, string? inResponseTo, string subject = "csrf@example.com")
        => SamlTestHelper.BuildSignedResponse(
            $"{AuthagonalTestFactory.TestIssuer}/saml/{connectionId}/acs",
            "https://sp.test/csrf",
            subject,
            inResponseTo: inResponseTo,
            email: subject);

    private async Task<string> CreateConnectionAsync()
    {
        var body = new
        {
            connectionName = "CSRF IdP",
            entityId = "https://sp.test/csrf",
            metadataXml = SamlTestHelper.BuildIdpMetadata(),
            allowedDomains = new[] { "example.com" },
            jitProvisioningEnabled = true,
            // Left at its default on purpose: these tests are about what the default refuses.
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saml/connections")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);

        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"create failed: {response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("connectionId").GetString()!;
    }

    /// <summary>Pulls the AuthnRequest id out of the deflated SAMLRequest on the login redirect.</summary>
    private static string ExtractRequestId(string redirectUrl)
    {
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(redirectUrl).Query);
        var compressed = Convert.FromBase64String(query["SAMLRequest"]!);
        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        var doc = new XmlDocument();
        doc.LoadXml(reader.ReadToEnd());
        return doc.DocumentElement!.Attributes["ID"]!.Value;
    }
}

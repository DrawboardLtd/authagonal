using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml;
using Authagonal.Server.Services.Saml;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// F49–F52 + F56: vendor-quirk readiness. Friendly-name/multi-valued attribute mapping (Okta/Ping),
/// NameIDPolicy omission (ADFS), pasted metadata (Google Workspace), metadata refetch on cert
/// rollover (Entra), and server-side return URLs (RelayState 80-byte cap).
/// </summary>
public class SamlClaimMapperVendorTests
{
    private static Dictionary<string, string> Attrs(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void MapClaims_OktaFriendlyNames_MapEmailAndNames()
    {
        var attrs = Attrs(("email", "user@example.com"), ("firstName", "Ada"), ("lastName", "Lovelace"));

        var result = SamlClaimMapper.MapClaims("okta-id-1", null, attrs);

        Assert.Equal("user@example.com", result.Email);
        Assert.Equal("Ada", result.FirstName);
        Assert.Equal("Lovelace", result.LastName);
    }

    [Fact]
    public void MapClaims_ShibbolethOidAndMailAliases_Map()
    {
        var attrs = Attrs(
            ("urn:oid:0.9.2342.19200300.100.1.3", "oid@example.com"),
            ("urn:oid:2.5.4.42", "Grace"),
            ("sn", "Hopper"));

        var result = SamlClaimMapper.MapClaims("shib-id", null, attrs);

        Assert.Equal("oid@example.com", result.Email);
        Assert.Equal("Grace", result.FirstName);
        Assert.Equal("Hopper", result.LastName);
    }

    [Fact]
    public void MapClaims_MicrosoftClaimUris_StillWinOverFriendlyNames()
    {
        // The historic URI aliases are first in each list — Entra/ADFS behaviour is unchanged.
        var attrs = Attrs(
            ("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress", "uri@example.com"),
            ("email", "friendly@example.com"));

        var result = SamlClaimMapper.MapClaims("id", null, attrs);

        Assert.Equal("uri@example.com", result.Email);
    }

    [Fact]
    public void MapClaims_Groups_ComeFromMultiValuedView()
    {
        var multi = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["groups"] = ["Admins", "Engineering", "Everyone"]
        };

        var result = SamlClaimMapper.MapClaims("id", null, Attrs(("groups", "Admins")), multi);

        Assert.Equal(new[] { "Admins", "Engineering", "Everyone" }, result.Groups);
    }

    [Fact]
    public void MapClaims_MemberOfAlias_MapsGroups()
    {
        var multi = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["memberOf"] = ["CN=Staff"]
        };

        var result = SamlClaimMapper.MapClaims("id", null, Attrs(), multi);

        Assert.Equal(new[] { "CN=Staff" }, result.Groups);
    }
}

public class SamlRequestBuilderVendorTests
{
    private static string DecodeSamlRequest(string url)
    {
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        var compressed = Convert.FromBase64String(query["SAMLRequest"]!);
        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void BuildAuthnRequest_Default_RequestsEmailAddressNameId()
    {
        var url = SamlRequestBuilder.BuildAuthnRequestUrl("_r1", "sp", "https://sp/acs", "https://idp/sso");
        var xml = DecodeSamlRequest(url);
        Assert.Contains("NameIDPolicy", xml);
        Assert.Contains(SamlConstants.NameIdEmail, xml);
    }

    [Fact]
    public void BuildAuthnRequest_None_OmitsNameIdPolicy_AdfsSafe()
    {
        var url = SamlRequestBuilder.BuildAuthnRequestUrl("_r1", "sp", "https://sp/acs", "https://idp/sso",
            nameIdFormat: SamlRequestBuilder.NameIdFormatNone);
        var xml = DecodeSamlRequest(url);
        Assert.DoesNotContain("NameIDPolicy", xml);
        // Still a valid AuthnRequest
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        Assert.Equal("AuthnRequest", doc.DocumentElement!.LocalName);
    }

    [Fact]
    public void BuildAuthnRequest_ExplicitFormat_SentVerbatim()
    {
        var url = SamlRequestBuilder.BuildAuthnRequestUrl("_r1", "sp", "https://sp/acs", "https://idp/sso",
            nameIdFormat: SamlConstants.NameIdPersistent);
        var xml = DecodeSamlRequest(url);
        Assert.Contains(SamlConstants.NameIdPersistent, xml);
    }
}

public class SamlMetadataCondenseTests
{
    [Fact]
    public void Condense_RoundTrips_EntityIdSsoUrlAndCert()
    {
        var original = SamlTestHelper.BuildIdpMetadata("https://idp.example", "https://idp.example/sso");

        var condensed = SamlMetadataParser.Condense(original);
        var reparsed = SamlMetadataParser.Parse(condensed);

        Assert.Equal("https://idp.example", reparsed.EntityId);
        Assert.Equal("https://idp.example/sso", reparsed.SingleSignOnServiceUrl);
        Assert.Single(reparsed.SigningCertificates);
        Assert.Equal(SamlTestHelper.TestCertificate.Thumbprint, reparsed.SigningCertificates[0].Thumbprint);
    }

    [Fact]
    public void Condense_InvalidXml_Throws()
    {
        Assert.ThrowsAny<Exception>(() => SamlMetadataParser.Condense("<not-saml-metadata/>"));
    }
}

public class SamlParserMultiValueTests
{
    [Fact]
    public void Parse_MultiValuedGroups_AndFriendlyName_AllCaptured()
    {
        const string acs = "https://sp.test/saml/c1/acs";
        const string audience = "https://sp.test/saml/c1";
        var extra =
            @"<saml:Attribute Name=""groups"">" +
            @"<saml:AttributeValue>Admins</saml:AttributeValue>" +
            @"<saml:AttributeValue>Engineering</saml:AttributeValue>" +
            @"</saml:Attribute>" +
            @"<saml:Attribute Name=""urn:oid:2.5.4.42"" FriendlyName=""givenName"">" +
            @"<saml:AttributeValue>Ada</saml:AttributeValue>" +
            @"</saml:Attribute>";

        var response = SamlTestHelper.BuildSignedResponse(acs, audience, "user@example.com",
            email: "user@example.com", extraAttributesXml: extra);

        var parser = new SamlResponseParser(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SamlResponseParser>.Instance);
        var result = parser.Parse(response, new SamlResponseValidationContext(
            acs, audience, null, [SamlTestHelper.TestCertificate]));

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { "Admins", "Engineering" }, result.AttributeValues["groups"]);
        // FriendlyName is indexed alongside the OID Name
        Assert.Equal("Ada", result.Attributes["givenName"]);
        Assert.Equal("Ada", result.Attributes["urn:oid:2.5.4.42"]);
        // Single-value view keeps the first group (back-compat)
        Assert.Equal("Admins", result.Attributes["groups"]);

        var mapped = SamlClaimMapper.MapClaims(result.NameId!, result.NameIdFormat, result.Attributes, result.AttributeValues);
        Assert.Equal("Ada", mapped.FirstName);
        Assert.Equal(new[] { "Admins", "Engineering" }, mapped.Groups);
    }
}

/// <summary>Endpoint-level flows: pasted metadata (F49), NameIDPolicy plumb-through (F51),
/// cert-rollover refetch (F52), and the SP-initiated server-side return URL round trip (F56).</summary>
[Collection("Azurite")]
public sealed class SamlVendorQuirkEndpointTests(AzuriteFixture azurite) : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _adminToken = null!;

    public async Task InitializeAsync()
    {
        _factory.AzuriteConnectionString = azurite.ConnectionString;
        _factory.SamlHttpHandler = _metadataHandler;
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // Serves whatever metadata the current test assigns; counts fetches for the F52 assertion.
    private readonly StubMetadataHandler _metadataHandler = new();

    private sealed class StubMetadataHandler : HttpMessageHandler
    {
        public volatile string Metadata = "";
        public int Fetches;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Fetches);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Metadata, Encoding.UTF8, "application/xml")
            });
        }
    }

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<string> CreateConnectionAsync(object body)
    {
        // These tests exercise sign-in/provisioning; JIT now defaults off, so opt in unless the body says otherwise.
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body))!.AsObject();
        if (node["jitProvisioningEnabled"] is null) node["jitProvisioningEnabled"] = true;
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/saml/connections", node));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"create failed: {response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("connectionId").GetString()!;
    }

    private static string ExtractRequestId(string redirectUrl)
    {
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(redirectUrl).Query);
        var compressed = Convert.FromBase64String(query["SAMLRequest"]!);
        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        var doc = new XmlDocument();
        doc.LoadXml(reader.ReadToEnd());
        return doc.DocumentElement!.GetAttribute("ID");
    }

    [Fact]
    public async Task F49_PastedMetadata_NoUrl_DrivesLoginAndSignedAcs()
    {
        // No metadata URL anywhere — the Google Workspace shape. The stub handler would count a
        // fetch if the SP tried one; assert it never does.
        var before = _metadataHandler.Fetches;
        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "Google Workspace",
            entityId = "https://sp.test/google",
            metadataXml = SamlTestHelper.BuildIdpMetadata("https://accounts.google.test", "https://accounts.google.test/sso"),
            allowedDomains = new[] { "example.com" }
        });

        // Login redirects to the pasted SSO URL
        var login = await _client.GetAsync($"/saml/{connectionId}/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.StartsWith("https://accounts.google.test/sso", login.Headers.Location!.ToString());

        // Full signed ACS (Google signs the Response element — exactly what BuildSignedResponse does)
        var acsUrl = $"{AuthagonalTestFactory.TestIssuer}/saml/{connectionId}/acs";
        var saml = SamlTestHelper.BuildSignedResponse(
            acsUrl, "https://sp.test/google", "guser@example.com",
            inResponseTo: ExtractRequestId(login.Headers.Location!.ToString()),
            email: "guser@example.com", firstName: "Gina", lastName: "User");

        var acs = await _client.PostAsync($"/saml/{connectionId}/acs",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = saml }));

        Assert.Equal(HttpStatusCode.Redirect, acs.StatusCode);
        Assert.Equal(before, _metadataHandler.Fetches); // never fetched a URL
    }

    [Fact]
    public async Task F51_NameIdFormatNone_OmitsPolicyFromAuthnRequest()
    {
        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "ADFS",
            entityId = "https://sp.test/adfs",
            metadataXml = SamlTestHelper.BuildIdpMetadata("https://adfs.corp.test", "https://adfs.corp.test/adfs/ls"),
            nameIdFormat = "none"
        });

        var login = await _client.GetAsync($"/saml/{connectionId}/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var url = login.Headers.Location!.ToString();
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        var compressed = Convert.FromBase64String(query["SAMLRequest"]!);
        using var deflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var xml = new StreamReader(deflate, Encoding.UTF8).ReadToEnd();

        Assert.DoesNotContain("NameIDPolicy", xml);
    }

    [Fact]
    public async Task F52_CertRollover_RefetchesMetadataAndAcceptsLogin()
    {
        // Metadata initially advertises a STALE cert (not the one signing responses) — the
        // post-rollover state with a pre-rollover cache. The SP must refetch and retry.
        using var staleRsa = RSA.Create(2048);
        var staleReq = new CertificateRequest("CN=Stale IdP Cert", staleRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var staleCert = staleReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(1));

        _metadataHandler.Metadata = SamlTestHelper.BuildIdpMetadata(signingCert: staleCert);

        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "Entra",
            entityId = "https://sp.test/entra",
            metadataLocation = "https://login.microsoftonline.test/federationmetadata.xml",
            allowedDomains = new[] { "example.com" }
        });

        // Prime the metadata cache with the stale cert
        var login = await _client.GetAsync($"/saml/{connectionId}/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        // Roll the cert: the IdP now publishes the real signing cert
        _metadataHandler.Metadata = SamlTestHelper.BuildIdpMetadata();
        var fetchesBeforeAcs = _metadataHandler.Fetches;

        var acsUrl = $"{AuthagonalTestFactory.TestIssuer}/saml/{connectionId}/acs";
        var saml = SamlTestHelper.BuildSignedResponse(
            acsUrl, "https://sp.test/entra", "euser@example.com",
            inResponseTo: ExtractRequestId(login.Headers.Location!.ToString()),
            email: "euser@example.com");

        var acs = await _client.PostAsync($"/saml/{connectionId}/acs",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = saml }));

        Assert.Equal(HttpStatusCode.Redirect, acs.StatusCode);
        Assert.False(acs.Headers.Location!.ToString().Contains("error=", StringComparison.Ordinal),
            $"expected success redirect, got {acs.Headers.Location}");
        Assert.True(_metadataHandler.Fetches > fetchesBeforeAcs, "expected a metadata refetch after the signature failure");
    }

    [Fact]
    public async Task F56_ReturnUrl_RidesServerSide_NotRelayState()
    {
        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "Okta",
            entityId = "https://sp.test/okta",
            metadataXml = SamlTestHelper.BuildIdpMetadata("https://okta.test", "https://okta.test/sso"),
            allowedDomains = new[] { "example.com" }
        });

        // A returnUrl far past the 80-byte RelayState cap
        var returnUrl = "/authorize?client_id=portal&redirect_uri=https%3A%2F%2Fapp.example.com%2Fcallback&scope=openid%20profile%20email%20offline_access&state=abcdef0123456789";
        var login = await _client.GetAsync($"/saml/{connectionId}/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var redirect = login.Headers.Location!.ToString();
        Assert.DoesNotContain("RelayState", redirect); // no URL blob on the wire

        // Complete the round trip WITHOUT any RelayState in the POST — the return URL must come
        // back from the stored request row via InResponseTo.
        var acsUrl = $"{AuthagonalTestFactory.TestIssuer}/saml/{connectionId}/acs";
        var saml = SamlTestHelper.BuildSignedResponse(
            acsUrl, "https://sp.test/okta", "okta-user@example.com",
            inResponseTo: ExtractRequestId(redirect),
            // Must match the entityID in the metadata this connection was created with. The fixture
            // previously issued as the default https://idp.test while configuring Okta's metadata, a
            // mismatch the ACS now rejects — the Issuer is what binds a verified signature to the IdP
            // the connection actually means.
            issuer: "https://okta.test",
            email: "okta-user@example.com");

        var acs = await _client.PostAsync($"/saml/{connectionId}/acs",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = saml }));

        Assert.Equal(HttpStatusCode.Redirect, acs.StatusCode);
        Assert.Equal(returnUrl, acs.Headers.Location!.ToString());
    }

    [Fact]
    public async Task F49_CreateRejects_BothOrNeitherMetadataSource()
    {
        var neither = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/saml/connections",
            new { connectionName = "x", entityId = "https://sp.test/x" }));
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);

        var both = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/saml/connections",
            new
            {
                connectionName = "x",
                entityId = "https://sp.test/x",
                metadataLocation = "https://idp.test/metadata",
                metadataXml = SamlTestHelper.BuildIdpMetadata()
            }));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task F49_CreateRejects_UnparseableMetadataXml_WithActionableError()
    {
        var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/saml/connections",
            new { connectionName = "x", entityId = "https://sp.test/x", metadataXml = "<hello/>" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("IDPSSODescriptor", body);
    }
}

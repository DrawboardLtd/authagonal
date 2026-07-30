using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Authagonal.Server.Services.Saml;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// F54 (SP keypair: EncryptedAssertion decryption + signed AuthnRequests + published KeyDescriptors)
/// and F55 (single logout) — unit level.
/// </summary>
public class SamlSpKeyTests
{
    [Fact]
    public void CreateCertificate_RoundTrips_WithPrivateKey()
    {
        var pfx = SamlSpKey.CreateCertificate("https://sp.test/acme");
        using var cert = SamlSpKey.Load(pfx);

        Assert.True(cert.HasPrivateKey);
        using var rsa = cert.GetRSAPrivateKey();
        Assert.NotNull(rsa);
        Assert.True(cert.NotAfter > DateTime.UtcNow.AddYears(9));
    }

    [Fact]
    public void RedirectBinding_SignThenVerify_RoundTrips()
    {
        var pfx = SamlSpKey.CreateCertificate("https://sp.test");
        using var cert = SamlSpKey.Load(pfx);
        using var rsa = cert.GetRSAPrivateKey()!;

        var url = SamlRequestBuilder.BuildAuthnRequestUrl("_r1", "sp", "https://sp/acs", "https://idp/sso");
        var signed = SamlRedirectBinding.Sign(url, rsa);

        Assert.Contains("SigAlg=", signed);
        Assert.Contains("Signature=", signed);

        var query = new Uri(signed).Query;
        Assert.True(SamlRedirectBinding.Verify(query, [cert]));
        // Wrong cert refuses
        Assert.False(SamlRedirectBinding.Verify(query, [SamlTestHelper.TestCertificate]));
        // Unsigned refuses
        Assert.False(SamlRedirectBinding.Verify(new Uri(url).Query, [cert]));
    }

    [Fact]
    public void MetadataParser_ReadsSloAndWantAuthnRequestsSigned_AndCondensePreservesThem()
    {
        var xml = SamlTestHelper.BuildIdpMetadata(
            sloUrl: "https://idp.test/slo", wantAuthnRequestsSigned: true);

        var parsed = SamlMetadataParser.Parse(xml);
        Assert.Equal("https://idp.test/slo", parsed.SingleLogoutServiceUrl);
        Assert.True(parsed.WantAuthnRequestsSigned);

        var reparsed = SamlMetadataParser.Parse(SamlMetadataParser.Condense(xml));
        Assert.Equal("https://idp.test/slo", reparsed.SingleLogoutServiceUrl);
        Assert.True(reparsed.WantAuthnRequestsSigned);
    }

    [Fact]
    public void Parser_EncryptedAssertion_DecryptsAndValidates()
    {
        const string acs = "https://sp.test/saml/c1/acs";
        const string audience = "https://sp.test/saml/c1";

        using var spCert = SamlSpKey.Load(SamlSpKey.CreateCertificate(audience));
        using var spKey = spCert.GetRSAPrivateKey()!;

        // ADFS shape: signed Assertion, then the assertion encrypted to the SP cert.
        var signedResponse = SamlTestHelper.BuildSignedResponse(acs, audience, "user@example.com",
            email: "user@example.com", signAssertion: true);
        var encrypted = SamlTestHelper.EncryptAssertionInResponse(signedResponse, spCert);

        var parser = new SamlResponseParser(NullLogger<SamlResponseParser>.Instance);
        var result = parser.Parse(encrypted, new SamlResponseValidationContext(
            acs, audience, null, [SamlTestHelper.TestCertificate], DecryptionKey: spKey));

        Assert.True(result.Success, result.Error);
        Assert.Equal("user@example.com", result.NameId);
    }

    /// <summary>
    /// RSA-PKCS#1 v1.5 key transport is refused. XML Encryption 1.1 §5.5.1 deprecates it and the OASIS
    /// SAML encryption profile requires OAEP, because v1.5 unwrapping on an anonymous endpoint is a
    /// Bleichenbacher decryption oracle against the SP private key — and the SP keypair is minted for every
    /// connection whether or not the IdP encrypts, so it was armed by default everywhere.
    /// </summary>
    [Fact]
    public void Parser_EncryptedAssertion_Rsa15_IsRefused()
    {
        const string acs = "https://sp.test/saml/c1/acs";
        const string audience = "https://sp.test/saml/c1";

        using var spCert = SamlSpKey.Load(SamlSpKey.CreateCertificate(audience));
        using var spKey = spCert.GetRSAPrivateKey()!;

        var signedResponse = SamlTestHelper.BuildSignedResponse(acs, audience, "user@example.com",
            email: "user@example.com", signAssertion: true);
        var encrypted = SamlTestHelper.EncryptAssertionInResponse(signedResponse, spCert, useRsa15: true);

        var parser = new SamlResponseParser(NullLogger<SamlResponseParser>.Instance);
        var result = parser.Parse(encrypted, new SamlResponseValidationContext(
            acs, audience, null, [SamlTestHelper.TestCertificate], DecryptionKey: spKey));

        Assert.False(result.Success);
        // And the refusal is indistinguishable from any other decryption failure.
        Assert.Equal(SamlResponseParser.DecryptionFailure, result.Error);
    }

    /// <summary>
    /// Every decryption failure returns ONE constant message. Distinguishable responses are the signal a
    /// padding-oracle or Bleichenbacher attack consumes; the parser used to reflect the underlying
    /// CryptographicException text straight back to an anonymous caller.
    /// </summary>
    [Fact]
    public void Parser_EncryptedAssertion_AllDecryptionFailuresLookIdentical()
    {
        const string acs = "https://sp.test/saml/c1/acs";
        const string audience = "https://sp.test/saml/c1";

        using var spCert = SamlSpKey.Load(SamlSpKey.CreateCertificate(audience));
        // A DIFFERENT key: every unwrap attempt fails cryptographically.
        using var wrongCert = SamlSpKey.Load(SamlSpKey.CreateCertificate("https://other.test"));
        using var wrongKey = wrongCert.GetRSAPrivateKey()!;

        var signedResponse = SamlTestHelper.BuildSignedResponse(acs, audience, "user@example.com",
            email: "user@example.com", signAssertion: true);

        var parser = new SamlResponseParser(NullLogger<SamlResponseParser>.Instance);
        var errors = new List<string?>();

        // Wrong key, correct algorithm.
        errors.Add(parser.Parse(
            SamlTestHelper.EncryptAssertionInResponse(signedResponse, spCert),
            new SamlResponseValidationContext(acs, audience, null, [SamlTestHelper.TestCertificate],
                DecryptionKey: wrongKey)).Error);

        // Refused algorithm, correct key.
        errors.Add(parser.Parse(
            SamlTestHelper.EncryptAssertionInResponse(signedResponse, spCert, useRsa15: true),
            new SamlResponseValidationContext(acs, audience, null, [SamlTestHelper.TestCertificate],
                DecryptionKey: spCert.GetRSAPrivateKey()!)).Error);

        // Every distinct failure cause yields the same response text.
        Assert.All(errors, e => Assert.Equal(SamlResponseParser.DecryptionFailure, e));
        Assert.Single(errors.Distinct());
    }

    [Fact]
    public void Parser_EncryptedAssertion_WithoutKey_FailsWithActionableError()
    {
        const string acs = "https://sp.test/saml/c1/acs";
        const string audience = "https://sp.test/saml/c1";
        using var spCert = SamlSpKey.Load(SamlSpKey.CreateCertificate(audience));

        var encrypted = SamlTestHelper.EncryptAssertionInResponse(
            SamlTestHelper.BuildSignedResponse(acs, audience, "u@example.com", signAssertion: true), spCert);

        var parser = new SamlResponseParser(NullLogger<SamlResponseParser>.Instance);
        var result = parser.Parse(encrypted, new SamlResponseValidationContext(
            acs, audience, null, [SamlTestHelper.TestCertificate]));

        Assert.False(result.Success);
        Assert.Contains("EncryptedAssertion", result.Error);
    }
}

/// <summary>F54/F55 endpoint flows through the real host.</summary>
[Collection("Azurite")]
public sealed class SamlSpKeySloEndpointTests(AzuriteFixture azurite) : IAsyncLifetime
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

    private async Task<string> CreateConnectionAsync(object body)
    {
        // These tests exercise sign-in/provisioning; JIT now defaults off, so opt in unless the body says otherwise.
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body))!.AsObject();
        if (node["jitProvisioningEnabled"] is null) node["jitProvisioningEnabled"] = true;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saml/connections")
        {
            Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"create failed: {response.StatusCode} {text}");
        var json = JsonDocument.Parse(text).RootElement;
        // The SP keypair must never be returned to API callers.
        Assert.True(!json.TryGetProperty("spCertificate", out var sp) || sp.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                    string.IsNullOrEmpty(sp.GetString()),
            "spCertificate leaked in the create response");
        return json.GetProperty("connectionId").GetString()!;
    }

    private async Task<(X509Certificate2 EncryptionCert, string MetadataXml)> GetSpMetadataAsync(string connectionId)
    {
        var response = await _client.GetAsync($"/saml/{connectionId}/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var xml = await response.Content.ReadAsStringAsync();

        var doc = XDocument.Parse(xml);
        XNamespace md = "urn:oasis:names:tc:SAML:2.0:metadata";
        XNamespace ds = "http://www.w3.org/2000/09/xmldsig#";
        var descriptor = doc.Root!.Element(md + "SPSSODescriptor")!;
        var encKey = descriptor.Elements(md + "KeyDescriptor")
            .First(k => (string?)k.Attribute("use") == "encryption");
        var certB64 = encKey.Element(ds + "KeyInfo")!.Element(ds + "X509Data")!.Element(ds + "X509Certificate")!.Value.Trim();
        return (X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certB64)), xml);
    }

    private static string ExtractRequestId(string redirectUrl)
    {
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(redirectUrl).Query);
        var doc = new XmlDocument();
        doc.LoadXml(SamlRedirectBinding.Inflate(query["SAMLRequest"]!));
        return doc.DocumentElement!.GetAttribute("ID");
    }

    [Fact]
    public async Task F54_SpMetadata_PublishesSigningAndEncryptionKeyDescriptors_AndSlo()
    {
        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "ADFS",
            entityId = "https://sp.test/adfs-meta",
            metadataXml = SamlTestHelper.BuildIdpMetadata()
        });

        var (encryptionCert, xml) = await GetSpMetadataAsync(connectionId);
        Assert.NotNull(encryptionCert);
        Assert.Contains("use=\"signing\"", xml);
        Assert.Contains("SingleLogoutService", xml);
        Assert.Contains($"/saml/{connectionId}/slo", xml);
    }

    [Fact]
    public async Task F54_EncryptedAssertion_FullAcsFlow_SignsInUser()
    {
        // ADFS end-to-end: SP advertises an encryption cert → the "IdP" encrypts the (signed)
        // assertion to it → the ACS decrypts with the stored SP key and signs the user in.
        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "ADFS Encrypted",
            entityId = "https://sp.test/adfs-enc",
            metadataXml = SamlTestHelper.BuildIdpMetadata(),
            allowedDomains = new[] { "example.com" }
        });

        var (encryptionCert, _) = await GetSpMetadataAsync(connectionId);

        var acsUrl = $"{AuthagonalTestFactory.TestIssuer}/saml/{connectionId}/acs";
        var signed = SamlTestHelper.BuildSignedResponse(
            acsUrl, "https://sp.test/adfs-enc", "adfs-user@example.com",
            email: "adfs-user@example.com", firstName: "Ada", lastName: "Fs",
            signAssertion: true);
        var encrypted = SamlTestHelper.EncryptAssertionInResponse(signed, encryptionCert);

        var acs = await _client.PostAsync($"/saml/{connectionId}/acs",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = encrypted }));

        Assert.Equal(HttpStatusCode.Redirect, acs.StatusCode);
        Assert.False(acs.Headers.Location!.ToString().Contains("error=", StringComparison.Ordinal),
            $"expected success redirect, got {acs.Headers.Location}");
    }

    [Fact]
    public async Task F54_IdpWantsSignedRequests_LoginRedirectCarriesValidSignature()
    {
        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "ADFS Signed Requests",
            entityId = "https://sp.test/adfs-sig",
            metadataXml = SamlTestHelper.BuildIdpMetadata(wantAuthnRequestsSigned: true)
        });

        var login = await _client.GetAsync($"/saml/{connectionId}/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var url = login.Headers.Location!.ToString();
        Assert.Contains("SigAlg=", url);
        Assert.Contains("Signature=", url);

        // Verifiable with the SP cert published in our own metadata (what the IdP would hold).
        var (_, metadataXml) = await GetSpMetadataAsync(connectionId);
        var doc = XDocument.Parse(metadataXml);
        XNamespace md = "urn:oasis:names:tc:SAML:2.0:metadata";
        XNamespace ds = "http://www.w3.org/2000/09/xmldsig#";
        var signingCertB64 = doc.Root!.Element(md + "SPSSODescriptor")!.Elements(md + "KeyDescriptor")
            .First(k => (string?)k.Attribute("use") == "signing")
            .Element(ds + "KeyInfo")!.Element(ds + "X509Data")!.Element(ds + "X509Certificate")!.Value.Trim();
        var signingCert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(signingCertB64));

        Assert.True(SamlRedirectBinding.Verify(new Uri(url).Query, [signingCert]));
    }

    [Fact]
    public async Task F55_SpInitiatedSlo_SendsLogoutRequest_AndResponseLegLandsOnReturnUrl()
    {
        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "Okta SLO",
            entityId = "https://sp.test/okta-slo",
            metadataXml = SamlTestHelper.BuildIdpMetadata(sloUrl: "https://idp.test/slo"),
            allowedDomains = new[] { "example.com" }
        });

        // Sign in via ACS so the cookie session carries the SAML session claims.
        var login = await _client.GetAsync($"/saml/{connectionId}/login");
        var acsUrl = $"{AuthagonalTestFactory.TestIssuer}/saml/{connectionId}/acs";
        var saml = SamlTestHelper.BuildSignedResponse(
            acsUrl, "https://sp.test/okta-slo", "slo-user@example.com",
            inResponseTo: ExtractRequestId(login.Headers.Location!.ToString()),
            email: "slo-user@example.com", sessionIndex: "sess-42");
        var acs = await _client.PostAsync($"/saml/{connectionId}/acs",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = saml }));
        Assert.Equal(HttpStatusCode.Redirect, acs.StatusCode);

        // SP-initiated logout → LogoutRequest to the IdP with NameID + SessionIndex.
        var logout = await _client.GetAsync($"/saml/{connectionId}/logout?returnUrl=/goodbye");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        var logoutUrl = logout.Headers.Location!.ToString();
        Assert.StartsWith("https://idp.test/slo", logoutUrl);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(logoutUrl).Query);
        var logoutXml = SamlRedirectBinding.Inflate(query["SAMLRequest"]!);
        Assert.Contains("LogoutRequest", logoutXml);
        Assert.Contains("slo-user@example.com", logoutXml);
        Assert.Contains("sess-42", logoutXml);

        // IdP answers with a LogoutResponse → user lands on the stored returnUrl.
        var doc = new XmlDocument();
        doc.LoadXml(logoutXml);
        var logoutRequestId = doc.DocumentElement!.GetAttribute("ID");
        var responseXml = $"""
            <samlp:LogoutResponse xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
                ID="_lr{Guid.NewGuid():N}" Version="2.0" IssueInstant="{DateTime.UtcNow:O}"
                InResponseTo="{logoutRequestId}">
              <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status>
            </samlp:LogoutResponse>
            """;
        var slo = await _client.GetAsync($"/saml/{connectionId}/slo?SAMLResponse={DeflateEncode(responseXml)}");
        Assert.Equal(HttpStatusCode.Redirect, slo.StatusCode);
        Assert.Equal("/goodbye", slo.Headers.Location!.ToString());
    }

    [Fact]
    public async Task F55_IdpInitiatedSlo_WithMatchingSession_SignsOutAndAnswers()
    {
        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "Entra SLO",
            entityId = "https://sp.test/entra-slo",
            metadataXml = SamlTestHelper.BuildIdpMetadata(sloUrl: "https://idp.test/slo"),
            allowedDomains = new[] { "example.com" }
        });

        var login = await _client.GetAsync($"/saml/{connectionId}/login");
        var acsUrl = $"{AuthagonalTestFactory.TestIssuer}/saml/{connectionId}/acs";
        var saml = SamlTestHelper.BuildSignedResponse(
            acsUrl, "https://sp.test/entra-slo", "idpslo-user@example.com",
            inResponseTo: ExtractRequestId(login.Headers.Location!.ToString()),
            email: "idpslo-user@example.com");
        Assert.Equal(HttpStatusCode.Redirect,
            (await _client.PostAsync($"/saml/{connectionId}/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = saml }))).StatusCode);

        // Unsigned IdP-initiated LogoutRequest arriving in the logged-in browser: honored (the
        // session vouches for itself) and answered with a LogoutResponse to the IdP.
        var logoutRequest = $"""
            <samlp:LogoutRequest xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
                ID="_idplr{Guid.NewGuid():N}" Version="2.0" IssueInstant="{DateTime.UtcNow:O}">
              <saml:Issuer xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">https://idp.test</saml:Issuer>
              <saml:NameID xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">idpslo-user@example.com</saml:NameID>
            </samlp:LogoutRequest>
            """;
        var slo = await _client.GetAsync($"/saml/{connectionId}/slo?SAMLRequest={DeflateEncode(logoutRequest)}");
        Assert.Equal(HttpStatusCode.Redirect, slo.StatusCode);
        var responseUrl = slo.Headers.Location!.ToString();
        Assert.StartsWith("https://idp.test/slo", responseUrl);
        Assert.Contains("SAMLResponse=", responseUrl);
    }

    [Fact]
    public async Task F55_UnsignedIdpInitiatedSlo_WithoutSession_IsRejected()
    {
        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "Hostile SLO",
            entityId = "https://sp.test/hostile-slo",
            metadataXml = SamlTestHelper.BuildIdpMetadata(sloUrl: "https://idp.test/slo")
        });

        var logoutRequest = $"""
            <samlp:LogoutRequest xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
                ID="_x{Guid.NewGuid():N}" Version="2.0" IssueInstant="{DateTime.UtcNow:O}">
              <saml:Issuer xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">https://idp.test</saml:Issuer>
              <saml:NameID xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">victim@example.com</saml:NameID>
            </samlp:LogoutRequest>
            """;
        var slo = await _client.GetAsync($"/saml/{connectionId}/slo?SAMLRequest={DeflateEncode(logoutRequest)}");
        Assert.Equal(HttpStatusCode.BadRequest, slo.StatusCode);
    }

    private static string DeflateEncode(string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        using var output = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(bytes, 0, bytes.Length);
        }
        return Uri.EscapeDataString(Convert.ToBase64String(output.ToArray()));
    }
}

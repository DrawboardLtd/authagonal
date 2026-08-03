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
        Assert.True(SamlRedirectBinding.Verify(query, "SAMLRequest", [cert]));
        // Wrong cert refuses
        Assert.False(SamlRedirectBinding.Verify(query, "SAMLRequest", [SamlTestHelper.TestCertificate]));
        // Unsigned refuses
        Assert.False(SamlRedirectBinding.Verify(new Uri(url).Query, "SAMLRequest", [cert]));
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
    /// F258 — an IdP that signs at the Response level AND encrypts the assertion.
    /// </summary>
    /// <remarks>
    /// Decryption calls EncryptedXml.ReplaceData, which rewrites the loaded document in place, and the
    /// Response signature was only verified afterwards — over a DOM that no longer matched what the
    /// IdP signed. responseSignatureValid was therefore unconditionally false for every encrypted
    /// response, so a supported and common combination could not federate at all, and the failure
    /// presented as a signature problem rather than an ordering one.
    /// </remarks>
    [Fact]
    public void Parser_EncryptedAssertion_WithResponseLevelSignature_Validates()
    {
        const string acs = "https://sp.test/saml/c1/acs";
        const string audience = "https://sp.test/saml/c1";

        using var spCert = SamlSpKey.Load(SamlSpKey.CreateCertificate(audience));
        using var spKey = spCert.GetRSAPrivateKey()!;

        // The Response signature is the ONLY signature here — sign: false leaves the assertion
        // unsigned, so if the Response signature is not honoured nothing else can carry the document.
        // Encryption happens first, so the signature covers the EncryptedAssertion, which is what an
        // IdP signing at this level actually produces.
        var unsigned = SamlTestHelper.BuildSignedResponse(acs, audience, "user@example.com",
            email: "user@example.com", sign: false);
        var encrypted = SamlTestHelper.EncryptAssertionInResponse(unsigned, spCert);
        var signed = SamlTestHelper.SignResponseAfterEncryption(encrypted);

        var parser = new SamlResponseParser(NullLogger<SamlResponseParser>.Instance);
        var result = parser.Parse(signed, new SamlResponseValidationContext(
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
        // Same shape for unsolicited responses: these fixtures post IdP-initiated assertions (no
        // InResponseTo), which is now off by default because such a response is bound to no pending
        // request and no browser. Tests that drive a real /saml/{id}/login leave it alone.
        if (node["allowUnsolicitedResponses"] is null) node["allowUnsolicitedResponses"] = true;
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

        Assert.True(SamlRedirectBinding.Verify(new Uri(url).Query, "SAMLRequest", [signingCert]));
    }

    /// <summary>
    /// F300 — the published metadata must state the condition the login path actually applies. That
    /// path signs when there is an SP key AND (the connection forces it OR the IdP's metadata declares
    /// WantAuthnRequestsSigned); the metadata document computed only the first disjunct, so this exact
    /// connection — SignAuthnRequests unset, IdP asking for signatures — signed every AuthnRequest
    /// while publishing <c>AuthnRequestsSigned="false"</c> to the administrator configuring against it.
    /// <para>
    /// Paired deliberately with F54 above, which proves the same connection really does sign.
    /// </para>
    /// </summary>
    [Fact]
    public async Task F300_MetadataDeclaresAuthnRequestsSigned_WhenTheIdpAsksForIt()
    {
        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "ADFS Signed Requests Metadata",
            entityId = "https://sp.test/adfs-sig-metadata",
            metadataXml = SamlTestHelper.BuildIdpMetadata(wantAuthnRequestsSigned: true)
        });

        var (_, metadataXml) = await GetSpMetadataAsync(connectionId);

        XNamespace md = "urn:oasis:names:tc:SAML:2.0:metadata";
        var descriptor = XDocument.Parse(metadataXml).Root!.Element(md + "SPSSODescriptor")!;

        Assert.Equal("true", (string?)descriptor.Attribute("AuthnRequestsSigned"));
    }

    /// <summary>
    /// The control: an IdP that does not ask for signed requests, on a connection that does not force
    /// them, must still publish false — the login path would not sign either. Without this the test
    /// above would pass against an attribute hardcoded to "true".
    /// <para>
    /// Every connection is issued an SP keypair at create time (F54), so "no SP certificate" is not a
    /// reachable state through the admin API and cannot serve as the control here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task F300_MetadataDeclaresAuthnRequestsUnsigned_WhenNeitherSideAsksForIt()
    {
        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "Unsigned Requests Metadata",
            entityId = "https://sp.test/unsigned-requests",
            metadataXml = SamlTestHelper.BuildIdpMetadata()
        });

        var (_, metadataXml) = await GetSpMetadataAsync(connectionId);

        XNamespace md = "urn:oasis:names:tc:SAML:2.0:metadata";
        var descriptor = XDocument.Parse(metadataXml).Root!.Element(md + "SPSSODescriptor")!;

        Assert.Equal("false", (string?)descriptor.Attribute("AuthnRequestsSigned"));
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
                xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion"
                ID="_lr{Guid.NewGuid():N}" Version="2.0" IssueInstant="{DateTime.UtcNow:O}"
                InResponseTo="{logoutRequestId}">
              <saml:Issuer>https://idp.test</saml:Issuer>
              <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status>
            </samlp:LogoutResponse>
            """;
        // Signed, because the response leg is only HONOURED when it authenticates — see F290. The
        // stored return URL is state the message is claiming, so an unsigned one must not reach it.
        var sloUrl = SamlRedirectBinding.Sign(
            $"{AuthagonalTestFactory.TestIssuer}/saml/{connectionId}/slo?SAMLResponse={DeflateEncode(responseXml)}",
            SamlTestHelper.TestKey);
        var slo = await _client.GetAsync(new Uri(sloUrl).PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, slo.StatusCode);
        Assert.Equal("/goodbye", slo.Headers.Location!.ToString());
    }

    /// <summary>
    /// F290 — the LogoutResponse leg was entirely unauthenticated, and it consumes an InResponseTo
    /// from a replay cache whose "request" sort key is shared with pending AuthnRequest IDs. So a
    /// forged LogoutResponse naming a pending AuthnRequest ID consumed it, and the legitimate login
    /// that followed was rejected as a replay — unauthenticated login denial. The unsigned message
    /// must still land the browser somewhere, but must consume nothing.
    /// </summary>
    [Fact]
    public async Task F290_UnsignedLogoutResponse_DoesNotConsumePendingRequestState()
    {
        var connectionId = await CreateConnectionAsync(new
        {
            connectionName = "Denial SLO",
            entityId = "https://sp.test/denial-slo",
            metadataXml = SamlTestHelper.BuildIdpMetadata(sloUrl: "https://idp.test/slo"),
            allowedDomains = new[] { "example.com" }
        });

        // A login is in flight: its request ID is pending in the replay cache.
        var login = await _client.GetAsync($"/saml/{connectionId}/login");
        var pendingRequestId = ExtractRequestId(login.Headers.Location!.ToString());

        // The attacker forges an unsigned LogoutResponse naming that pending request ID.
        var forged = $"""
            <samlp:LogoutResponse xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
                ID="_lr{Guid.NewGuid():N}" Version="2.0" IssueInstant="{DateTime.UtcNow:O}"
                InResponseTo="{pendingRequestId}">
              <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status>
            </samlp:LogoutResponse>
            """;
        await _client.GetAsync($"/saml/{connectionId}/slo?SAMLResponse={DeflateEncode(forged)}");

        // The real login must still complete: its request ID was not consumed.
        var acsUrl = $"{AuthagonalTestFactory.TestIssuer}/saml/{connectionId}/acs";
        var saml = SamlTestHelper.BuildSignedResponse(
            acsUrl, "https://sp.test/denial-slo", "victim@example.com",
            inResponseTo: pendingRequestId, email: "victim@example.com");
        var acs = await _client.PostAsync($"/saml/{connectionId}/acs",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = saml }));

        Assert.Equal(HttpStatusCode.Redirect, acs.StatusCode);
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

        // Signed IdP-initiated LogoutRequest arriving in the logged-in browser: honoured, and answered
        // with a LogoutResponse to the IdP.
        var slo = await _client.GetAsync(
            SignedSloPath(connectionId, LogoutRequestXml(connectionId, "idpslo-user@example.com")));
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

        var slo = await _client.GetAsync(
            UnsignedSloPath(connectionId, LogoutRequestXml(connectionId, "victim@example.com")));
        Assert.Equal(HttpStatusCode.BadRequest, slo.StatusCode);
    }

    /// <summary>
    /// F227 — logout CSRF. The unsigned fallback was honoured whenever the browser's cookie session
    /// named this connection, justified in the code as "an unauthenticated attacker can then log out
    /// nobody but themselves." Backwards: this is an anonymous GET and the SSO cookie cannot be
    /// SameSite=Strict (the ACS POST-binding round trip is itself cross-site), so a third-party page
    /// that navigates the VICTIM's browser here supplies the VICTIM's session. The check proved a
    /// session existed, never who initiated the request — which is the whole of CSRF.
    /// <para>
    /// The second half is the control: the same session is then ended by the same message WITH the
    /// IdP's signature, proving the session survived the refusal rather than the test asserting against
    /// a browser that had already been signed out.
    /// </para>
    /// </summary>
    [Fact]
    public async Task F227_UnsignedIdpInitiatedSlo_WithAMatchingSession_IsRefused()
    {
        var connectionId = await EstablishSamlSessionAsync("csrf-slo", "csrf-user@example.com");

        var refused = await _client.GetAsync(
            UnsignedSloPath(connectionId, LogoutRequestXml(connectionId, "csrf-user@example.com")));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // Control: the session is still there, so the IdP's own signed request still ends it.
        var honoured = await _client.GetAsync(
            SignedSloPath(connectionId, LogoutRequestXml(connectionId, "csrf-user@example.com")));
        Assert.Equal(HttpStatusCode.Redirect, honoured.StatusCode);
    }

    /// <summary>
    /// F310 — Destination is the anti-forwarding binding Core §3.2.2 makes mandatory on a signed
    /// message. Without it a LogoutRequest the IdP minted for a DIFFERENT SP in the same federation
    /// replays here: the signature verifies, the Issuer matches, and nothing else says the message was
    /// ever meant for this endpoint.
    /// </summary>
    [Fact]
    public async Task F310_SignedLogoutRequestAddressedToAnotherSp_IsRefused()
    {
        var connectionId = await EstablishSamlSessionAsync("destination-slo", "dest-user@example.com");

        var elsewhere = LogoutRequestXml(
            connectionId, "dest-user@example.com", destination: "https://other-sp.test/saml/slo");
        var refused = await _client.GetAsync(SignedSloPath(connectionId, elsewhere));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // Control: the session survived, and the same request addressed to us is honoured.
        var honoured = await _client.GetAsync(
            SignedSloPath(connectionId, LogoutRequestXml(connectionId, "dest-user@example.com")));
        Assert.Equal(HttpStatusCode.Redirect, honoured.StatusCode);
    }

    /// <summary>
    /// F290 — the forced-logout gadget. The NameID binding only compared when a NameID was present, so
    /// a LogoutRequest carrying none skipped the comparison and signed the browser out — and every
    /// request carries a fresh ID, so the replay cache never fires on a repeat.
    /// <para>
    /// The second half is the control: the same session is then logged out by a well-formed request,
    /// proving the session survived the refusal rather than the test asserting against a browser that
    /// had already been signed out for some other reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task F290_LogoutRequestWithoutNameId_IsRefused_AndTheSessionSurvives()
    {
        var connectionId = await EstablishSamlSessionAsync("gadget-slo", "gadget-user@example.com");

        var noNameId = $"""
            <samlp:LogoutRequest xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
                ID="_g{Guid.NewGuid():N}" Version="2.0" IssueInstant="{DateTime.UtcNow:O}"
                Destination="{SloUrl(connectionId)}">
              <saml:Issuer xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">https://idp.test</saml:Issuer>
            </samlp:LogoutRequest>
            """;
        var refused = await _client.GetAsync(SignedSloPath(connectionId, noNameId));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // Control: the session is still there, so a request naming the right subject still ends it.
        var honoured = await _client.GetAsync(
            SignedSloPath(connectionId, LogoutRequestXml(connectionId, "gadget-user@example.com")));
        Assert.Equal(HttpStatusCode.Redirect, honoured.StatusCode);
    }

    /// <summary>
    /// The session is deliberately re-established between the two attempts. Without that the second
    /// request is refused by the session-binding gate instead — a refusal that would pass this test
    /// while proving nothing about the replay cache.
    /// </summary>
    [Fact]
    public async Task F290_LogoutRequestReplay_IsRefused()
    {
        var connectionId = await CreateSloConnectionAsync("replay-slo");
        await LoginThroughAcsAsync(connectionId, "replay-slo", "replay-user@example.com");

        var path = SignedSloPath(connectionId, LogoutRequestXml(connectionId, "replay-user@example.com"));
        Assert.Equal(HttpStatusCode.Redirect, (await _client.GetAsync(path)).StatusCode);

        // Log back in, so the session gate cannot be what refuses the replay.
        await LoginThroughAcsAsync(connectionId, "replay-slo", "replay-user@example.com");

        // Same ID a second time: within the freshness window this was replayable without limit.
        var replayed = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Contains("saml_replay", await replayed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task F290_StaleLogoutRequest_IsRefused()
    {
        var connectionId = await EstablishSamlSessionAsync("stale-slo", "stale-user@example.com");

        var stale = LogoutRequestXml(
            connectionId, "stale-user@example.com", issueInstant: DateTime.UtcNow.AddMinutes(-30));
        var response = await _client.GetAsync(SignedSloPath(connectionId, stale));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// NotOnOrAfter is optional on a LogoutRequest, and was read by nothing — so a request its own
    /// issuer had already expired was honoured on the strength of the looser IssueInstant window.
    /// </summary>
    [Fact]
    public async Task F290_LogoutRequestPastItsNotOnOrAfter_IsRefused()
    {
        var connectionId = await EstablishSamlSessionAsync("expiry-slo", "expiry-user@example.com");

        // Fresh IssueInstant — so only the NotOnOrAfter can refuse this one.
        var expired = LogoutRequestXml(
            connectionId, "expiry-user@example.com", notOnOrAfter: DateTime.UtcNow.AddMinutes(-10));
        var response = await _client.GetAsync(SignedSloPath(connectionId, expired));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task F290_LogoutRequestFromAnotherIssuer_IsRefused()
    {
        var connectionId = await EstablishSamlSessionAsync("issuer-slo", "issuer-user@example.com");

        var wrongIssuer = LogoutRequestXml(
            connectionId, "issuer-user@example.com", issuer: "https://attacker.test");
        var response = await _client.GetAsync(SignedSloPath(connectionId, wrongIssuer));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The subject binding itself: a LogoutRequest naming a different subject than the browser's own
    /// session must not end it.
    /// </summary>
    [Fact]
    public async Task F290_LogoutRequestNamingAnotherSubject_IsRefused()
    {
        var connectionId = await EstablishSamlSessionAsync("subject-slo", "mine@example.com");

        var otherSubject = LogoutRequestXml(connectionId, "someone-else@example.com");
        var response = await _client.GetAsync(SignedSloPath(connectionId, otherSubject));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string LogoutRequestXml(
        string connectionId,
        string nameId,
        string issuer = "https://idp.test",
        DateTime? issueInstant = null,
        DateTime? notOnOrAfter = null,
        string? destination = null)
    {
        var notOnOrAfterAttr = notOnOrAfter is null ? "" : $""" NotOnOrAfter="{notOnOrAfter:O}" """;
        return $"""
            <samlp:LogoutRequest xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
                ID="_lq{Guid.NewGuid():N}" Version="2.0" IssueInstant="{issueInstant ?? DateTime.UtcNow:O}"{notOnOrAfterAttr}
                Destination="{destination ?? SloUrl(connectionId)}">
              <saml:Issuer xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">{issuer}</saml:Issuer>
              <saml:NameID xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">{nameId}</saml:NameID>
            </samlp:LogoutRequest>
            """;
    }

    private static string SloUrl(string connectionId) =>
        $"{AuthagonalTestFactory.TestIssuer}/saml/{connectionId}/slo";

    /// <summary>
    /// The IdP-initiated SLO leg as a conformant IdP delivers it: redirect binding, signed over the
    /// query string with the key the connection's metadata publishes (Profiles §4.4.3.1).
    /// </summary>
    private static string SignedSloPath(string connectionId, string logoutRequestXml) =>
        new Uri(SamlRedirectBinding.Sign(
            $"{SloUrl(connectionId)}?SAMLRequest={DeflateEncode(logoutRequestXml)}",
            SamlTestHelper.TestKey)).PathAndQuery;

    /// <summary>The same message with the signature simply left off.</summary>
    private static string UnsignedSloPath(string connectionId, string logoutRequestXml) =>
        $"/saml/{connectionId}/slo?SAMLRequest={DeflateEncode(logoutRequestXml)}";

    /// <summary>Creates a connection, completes a real login through the ACS, and returns its id.</summary>
    private async Task<string> EstablishSamlSessionAsync(string slug, string email)
    {
        var connectionId = await CreateSloConnectionAsync(slug);
        await LoginThroughAcsAsync(connectionId, slug, email);
        return connectionId;
    }

    private Task<string> CreateSloConnectionAsync(string slug) => CreateConnectionAsync(new
    {
        connectionName = slug,
        entityId = $"https://sp.test/{slug}",
        metadataXml = SamlTestHelper.BuildIdpMetadata(sloUrl: "https://idp.test/slo"),
        allowedDomains = new[] { "example.com" }
    });

    private async Task LoginThroughAcsAsync(string connectionId, string slug, string email)
    {
        var login = await _client.GetAsync($"/saml/{connectionId}/login");
        var acsUrl = $"{AuthagonalTestFactory.TestIssuer}/saml/{connectionId}/acs";
        var saml = SamlTestHelper.BuildSignedResponse(
            acsUrl, $"https://sp.test/{slug}", email,
            inResponseTo: ExtractRequestId(login.Headers.Location!.ToString()),
            email: email);

        var acs = await _client.PostAsync($"/saml/{connectionId}/acs",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = saml }));
        Assert.Equal(HttpStatusCode.Redirect, acs.StatusCode);
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

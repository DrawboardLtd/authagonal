using System.Security.Cryptography.X509Certificates;
using Authagonal.Server.Services.Saml;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// IdP metadata carries the signing certificates every assertion on that connection is validated
/// against, and <c>validUntil</c> is the only revocation channel it has: an IdP retiring a compromised
/// key republishes the document with a past expiry. Nothing read the attribute — and
/// <see cref="SamlMetadataParser.Condense"/> deleted it on the way to storage, so pasted metadata (which
/// is never re-fetched) became permanently current the moment it was saved.
/// </summary>
public sealed class SamlMetadataExpiryTests
{
    private static string Metadata(string? validUntil = null, string? cacheDuration = null)
    {
        var certBase64 = Convert.ToBase64String(SamlTestHelper.TestCertificate.Export(X509ContentType.Cert));
        var validUntilAttr = validUntil is null ? "" : $@" validUntil=""{validUntil}""";
        var cacheDurationAttr = cacheDuration is null ? "" : $@" cacheDuration=""{cacheDuration}""";

        return $@"<?xml version=""1.0""?>
<EntityDescriptor xmlns=""urn:oasis:names:tc:SAML:2.0:metadata"" entityID=""https://idp.test""{validUntilAttr}{cacheDurationAttr}>
    <IDPSSODescriptor protocolSupportEnumeration=""urn:oasis:names:tc:SAML:2.0:protocol"">
        <KeyDescriptor use=""signing"">
            <KeyInfo xmlns=""http://www.w3.org/2000/09/xmldsig#"">
                <X509Data><X509Certificate>{certBase64}</X509Certificate></X509Data>
            </KeyInfo>
        </KeyDescriptor>
        <SingleSignOnService
            Binding=""urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect""
            Location=""https://idp.test/sso""/>
    </IDPSSODescriptor>
</EntityDescriptor>";
    }

    private static string Instant(TimeSpan fromNow) => (DateTimeOffset.UtcNow + fromNow).UtcDateTime.ToString("O");

    /// <summary>Metadata with no expiry at all is unaffected — Entra and Okta publish none.</summary>
    [Fact]
    public void Metadata_withNoValidUntil_isAccepted()
    {
        var parsed = SamlMetadataParser.Parse(Metadata());

        Assert.Equal("https://idp.test", parsed.EntityId);
        Assert.Null(parsed.ValidUntil);
    }

    [Fact]
    public void Metadata_withFutureValidUntil_isAcceptedAndTheInstantIsRead()
    {
        var parsed = SamlMetadataParser.Parse(Metadata(validUntil: Instant(TimeSpan.FromDays(14))));

        Assert.NotNull(parsed.ValidUntil);
        Assert.True(parsed.ValidUntil > DateTimeOffset.UtcNow.AddDays(13));
    }

    /// <summary>
    /// The revocation case. An expired document must not supply signing certificates, or republishing
    /// with a past validUntil — the documented way to retire a key — has no effect on this SP.
    /// </summary>
    [Fact]
    public void Metadata_pastItsValidUntil_isRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SamlMetadataParser.Parse(Metadata(validUntil: Instant(TimeSpan.FromHours(-1)))));

        Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A malformed expiry fails closed rather than reading as "no expiry".</summary>
    [Fact]
    public void Metadata_withUnparseableValidUntil_isRefused()
    {
        Assert.Throws<InvalidOperationException>(
            () => SamlMetadataParser.Parse(Metadata(validUntil: "sometime-next-year")));
    }

    /// <summary>
    /// Condense is the ingest path for pasted metadata and re-emits a minimal descriptor. Dropping the
    /// expiry there meant a document the IdP had already dated was stored as one that never expires.
    /// </summary>
    [Fact]
    public void Condense_preservesValidUntilAndCacheDuration()
    {
        var condensed = SamlMetadataParser.Condense(
            Metadata(validUntil: Instant(TimeSpan.FromDays(7)), cacheDuration: "PT30M"));

        var reparsed = SamlMetadataParser.Parse(condensed);

        Assert.NotNull(reparsed.ValidUntil);
        Assert.True(reparsed.ValidUntil > DateTimeOffset.UtcNow.AddDays(6));
        Assert.Equal(TimeSpan.FromMinutes(30), reparsed.CacheDuration);
    }

    /// <summary>
    /// And the stored form keeps expiring: re-reading a condensed document past its expiry is refused
    /// exactly as the original would have been.
    /// </summary>
    [Fact]
    public void Condensed_metadata_stillExpires()
    {
        var condensed = SamlMetadataParser.Condense(Metadata(validUntil: Instant(TimeSpan.FromSeconds(1))));

        // Rewrite the (preserved) expiry into the past, which is what the passage of time does to a
        // stored document — the point being that the attribute survived Condense to be checked at all.
        var expired = condensed.Replace(
            $@"validUntil=""{SamlMetadataParser.Parse(condensed).ValidUntil!.Value.UtcDateTime:O}""",
            $@"validUntil=""{Instant(TimeSpan.FromHours(-1))}""",
            StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => SamlMetadataParser.Parse(expired));
    }

    [Fact]
    public void Metadata_withUnparseableCacheDuration_isRefused()
    {
        Assert.Throws<InvalidOperationException>(
            () => SamlMetadataParser.Parse(Metadata(cacheDuration: "half an hour")));
    }
}

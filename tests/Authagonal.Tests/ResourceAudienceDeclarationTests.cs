using Authagonal.Core.Models;
using Authagonal.Core.Services;

namespace Authagonal.Tests;

/// <summary>
/// #301 — an empty audience list on a client that was asked means "none", not "anything".
/// </summary>
/// <remarks>
/// The finding was originally declined on the grounds that a dynamically registered client has no way to
/// declare audiences (RFC 7591 defines no such field), so reading empty as deny-all would break every DCR
/// client and every MCP client with it. The reasoning was right about the consequence and wrong about this
/// server: <c>audiences</c> has always been accepted as a registration metadata extension and stored. So
/// the permissive reading is now scoped to rows that predate
/// <see cref="OAuthClient.AudiencesDeclared"/>, and a client that answered "none" is held to it.
/// </remarks>
public class ResourceAudienceDeclarationTests
{
    private static OAuthClient Client(bool declared, params string[] audiences) =>
        new() { ClientId = "c", ClientName = "c", AudiencesDeclared = declared, Audiences = [.. audiences] };

    [Fact]
    public void ADeclaredAudienceIsAccepted()
        => Assert.Null(ResourceAudiencePolicy.RejectResource(
            Client(true, "https://api.test/v1"), "https://api.test/v1"));

    [Fact]
    public void AnUndeclaredResourceIsRefusedWhenTheClientNamedOthers()
        => Assert.NotNull(ResourceAudiencePolicy.RejectResource(
            Client(true, "https://api.test/v1"), "https://elsewhere.test/v1"));

    /// <summary>The point of the flag: asked, answered "none", so no resource may be named.</summary>
    [Fact]
    public void AClientThatDeclaredNoAudiencesMayNotNameAResource()
    {
        var rejected = ResourceAudiencePolicy.RejectResource(Client(true), "https://anything.test/api");

        Assert.NotNull(rejected);
        Assert.Contains("declares no audiences", rejected, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stored client from before the flag keeps the permissive reading. Tightening those on upgrade
    /// would break flows that work today — which is the whole reason the flag exists instead of a
    /// straight change of meaning.
    /// </summary>
    [Fact]
    public void AClientThatPredatesTheFlagIsUnaffected()
        => Assert.Null(ResourceAudiencePolicy.RejectResource(Client(false), "https://anything.test/api"));

    /// <summary>
    /// A resource must be an absolute URI that says so, with no fragment.
    /// </summary>
    /// <remarks>
    /// The path cases are the interesting ones and they were a live defect, not a hypothetical: on Unix
    /// <c>Uri.TryCreate(…, UriKind.Absolute, …)</c> parses <c>/admin</c> and <c>//host/path</c> as absolute
    /// URIs by inferring a <c>file:</c> scheme, so the plain check every call site used accepted a bare
    /// path — which then landed verbatim in a signed token's <c>aud</c>. Found while writing this test.
    /// </remarks>
    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("https://api.test/v1#frag")]
    [InlineData("/relative")]
    [InlineData("/admin")]
    [InlineData("//host/path")]
    public void AResourceMustBeAnAbsoluteUriWithAWrittenScheme(string resource)
        => Assert.NotNull(ResourceAudiencePolicy.RejectResource(Client(false), resource));

    [Theory]
    [InlineData("https://api.test/v1")]
    [InlineData("urn:example:api")]
    [InlineData("HTTPS://api.test/v1")]
    public void AProperAbsoluteUriIsAccepted(string resource)
        => Assert.Null(ResourceAudiencePolicy.RejectResource(Client(false), resource));

    [Theory]
    [InlineData("/admin")]
    [InlineData("//host/path")]
    public void ADeclaredAudienceCannotBeABarePathEither(string audience)
        => Assert.NotNull(ResourceAudiencePolicy.RejectAudiences([audience]));

    // ── declared list validation ────────────────────────────────────────────────

    [Fact]
    public void AWellFormedAudienceListIsAccepted()
        => Assert.Null(ResourceAudiencePolicy.RejectAudiences(["https://api.test/v1", "urn:example:api"]));

    [Fact]
    public void AnUnboundedAudienceListIsRefused()
    {
        var many = Enumerable.Range(0, ResourceAudiencePolicy.MaxAudiences + 1)
            .Select(i => $"https://api.test/{i}").ToList();

        Assert.NotNull(ResourceAudiencePolicy.RejectAudiences(many));
    }

    [Fact]
    public void AnOverlongAudienceIsRefused()
        => Assert.NotNull(ResourceAudiencePolicy.RejectAudiences(
            [$"https://api.test/{new string('a', ResourceAudiencePolicy.MaxAudienceLength)}"]));

    [Fact]
    public void AMalformedAudienceIsRefused()
        => Assert.NotNull(ResourceAudiencePolicy.RejectAudiences(["not-a-uri"]));

    [Fact]
    public void AnEmptyAudienceEntryIsRefused()
        => Assert.NotNull(ResourceAudiencePolicy.RejectAudiences(["https://api.test/v1", "  "]));

    [Fact]
    public void NoAudiencesAtAllIsAValidDeclaration()
        => Assert.Null(ResourceAudiencePolicy.RejectAudiences([]));
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Advertised-vs-actual drift on the discovery documents and the configuration reference.
/// </summary>
/// <remarks>
/// Four separate cases of a claim that no code satisfied, or a capability no claim mentioned. All four are the
/// same shape — two hand-maintained copies of one fact, or a fact with no copy at all — so the fixes share the
/// list rather than syncing it, and these tests pin the sharing.
/// </remarks>
public sealed class DiscoveryAndConfigDriftTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    /// <summary>
    /// <c>org_id</c> is emitted under <c>profile</c> by both hosts and was advertised by only one.
    /// </summary>
    /// <remarks>
    /// The two <c>claims_supported</c> lists were maintained by hand and had drifted: the Protocol host's ended
    /// <c>"groups", "org_id"</c> and the Server host's ended <c>"groups"</c>. The Server is the host that emits
    /// it most explicitly — its userinfo returns it under <c>profile</c>, and <c>ProtocolTokenService</c> calls
    /// it the claim that "decides tenancy" — so the copy that omitted it was the one that most needed it. An RP
    /// that builds its claim mapping from <c>claims_supported</c> therefore built no tenancy mapping.
    /// </remarks>
    [Fact]
    public async Task DiscoveryAdvertisesOrgId_WhichBothHostsEmit()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient();

        var doc = await (await client.GetAsync("/.well-known/openid-configuration"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var claims = doc.GetProperty("claims_supported").EnumerateArray()
            .Select(c => c.GetString()).ToList();

        Assert.Contains("org_id", claims);

        // The rest of the set, so a future edit cannot quietly drop one instead.
        foreach (var expected in new[]
                 {
                     "sub", "iss", "aud", "exp", "iat", "auth_time",
                     "email", "email_verified", "name", "given_name", "family_name",
                     "phone_number", "roles", "groups",
                 })
            Assert.Contains(expected, claims);
    }

    /// <summary>
    /// The RFC 8414 path, which an MCP client resolves FIRST and need not fall back from.
    /// </summary>
    /// <remarks>
    /// The Server mapped both paths and the Protocol package mapped only the OIDC one — and the Protocol package
    /// is precisely the one documented for embedding OAuth in an existing app, including for MCP servers. So the
    /// host that most needed it was the one that did not publish it. The paths are one shared list now.
    /// </remarks>
    [Fact]
    public async Task BothMetadataPathsServeTheSameDocument()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient();

        var oidc = await client.GetAsync("/.well-known/openid-configuration");
        var oauth = await client.GetAsync("/.well-known/oauth-authorization-server");

        Assert.Equal(HttpStatusCode.OK, oidc.StatusCode);
        Assert.Equal(HttpStatusCode.OK, oauth.StatusCode);

        var a = await oidc.Content.ReadFromJsonAsync<JsonElement>();
        var b = await oauth.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(a.GetProperty("issuer").GetString(), b.GetProperty("issuer").GetString());
        Assert.Equal(
            a.GetProperty("token_endpoint").GetString(),
            b.GetProperty("token_endpoint").GetString());
    }

    /// <summary>
    /// Every scope discovery advertises must at least be RECOGNISED by dynamic registration.
    /// </summary>
    /// <remarks>
    /// <c>scopes_supported</c> advertises seven; <c>BuiltInScopes</c> held four, and anything outside
    /// <c>knownScopes</c> was answered <c>400 invalid_scope</c> "Unknown scope: {s}". So an RFC 7591 client that
    /// did the conformant thing — read the advertisement, ask for what it said — was told its request was
    /// malformed for a scope the same server had just declared supported, with no obvious recovery.
    /// <para>
    /// "Recognised" is the right assertion rather than "registrable": <c>roles</c> and <c>groups</c> release
    /// authorization-relevant claims, so an anonymous registrant should not self-assign them. The distinction
    /// that was missing is between "no such scope" and "not yours to claim".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryAdvertisedScopeIsRecognisedByDynamicRegistration()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient();

        var doc = await (await client.GetAsync("/.well-known/openid-configuration"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var advertised = doc.GetProperty("scopes_supported").EnumerateArray()
            .Select(s => s.GetString()!).ToList();

        Assert.Contains("phone", advertised);
        Assert.Contains("roles", advertised);
        Assert.Contains("groups", advertised);

        foreach (var scope in advertised)
        {
            var response = await client.PostAsJsonAsync("/connect/register", new
            {
                redirect_uris = new[] { "https://rp.test/cb" },
                scope = $"openid {scope}",
            });

            var body = await response.Content.ReadAsStringAsync();

            // Registration may be disabled, or the scope may not be open-registrable — both are legitimate
            // answers. "Unknown scope" is not, for something this server advertises.
            Assert.DoesNotContain($"Unknown scope: {scope}", body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The configuration reference must not document a key nothing reads, in any locale.
    /// </summary>
    /// <remarks>
    /// <c>Authentication:AlwaysSecureCookie</c> was documented in all seven locales with a stated default and a
    /// stated behaviour, and appeared in no read site anywhere — so an operator hardening a deployment set it,
    /// restarted, changed nothing, and recorded a control that does not exist. Meanwhile the two keys that ARE
    /// read (<c>AllowInsecureCookie</c>, and <c>CookieDomain</c> — which silently costs the <c>__Host-</c>
    /// prefix and its origin binding) were documented nowhere.
    /// <para>
    /// A source check because the claim was spread across seven files and its whole cost was in being believed.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDocumentedCookieKeysAreTheOnesTheCodeReads()
    {
        var root = RepositoryRoot();

        var extensions = File.ReadAllText(Path.Combine(
            root, "src/Authagonal.Server/AuthagonalExtensions.cs".Replace('/', Path.DirectorySeparatorChar)));

        // The keys the code actually reads.
        Assert.Contains("Authentication:AllowInsecureCookie", extensions, StringComparison.Ordinal);
        Assert.Contains("Authentication:CookieDomain", extensions, StringComparison.Ordinal);
        Assert.DoesNotContain("Authentication:AlwaysSecureCookie", extensions, StringComparison.Ordinal);

        foreach (var doc in Directory.GetFiles(
                     Path.Combine(root, "docs"), "configuration.md", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(doc);
            var name = Path.GetRelativePath(root, doc);

            Assert.DoesNotContain("AlwaysSecureCookie", text, StringComparison.Ordinal);
            Assert.Contains("Authentication:AllowInsecureCookie", text, StringComparison.Ordinal);
            Assert.True(
                text.Contains("Authentication:CookieDomain", StringComparison.Ordinal),
                $"{name} does not document Authentication:CookieDomain, which costs the __Host- prefix");
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Authagonal.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

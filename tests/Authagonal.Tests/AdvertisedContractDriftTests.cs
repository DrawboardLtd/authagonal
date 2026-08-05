using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// Two contracts the server advertised and did not keep.
//
// Both share a property that makes them hard to notice: nothing returns an error. A URL that no route
// serves falls through to MapFallbackToFile and answers 200 text/html, and a configuration field that
// nothing reads accepts any value at all. In each case the operator is told something is true, is given
// a success, and has no way to find out otherwise.
// -------------------------------------------------------------------------------------------------
public sealed class AdvertisedContractDriftTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // ── #33/#70: documentationUri pointed at the login page ──────────────────

    /// <summary>
    /// The SCIM <c>documentationUri</c> has to resolve to documentation, not to this server.
    /// </summary>
    /// <remarks>
    /// It was <c>$"{issuer}/docs/scim"</c>. Nothing maps a <c>/docs</c> route and there is no static file
    /// there — <c>docs/scim.md</c> is a Jekyll page for the project site, not content in the image — so
    /// <c>MapFallbackToFile("index.html")</c> answered it with <b>200 text/html</b>: the tenant's login
    /// page, to an administrator who followed the link their provisioning UI surfaced from
    /// ServiceProviderConfig. A 200 means no tool reports a problem.
    /// <para>
    /// The same file records fixing this exact failure for <c>meta.location</c> — four new routes and a
    /// paragraph of reasoning, because "a discovery client cannot tell that from a real answer" — one
    /// field below the value that still had it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheScimDocumentationUriDoesNotPointAtThisServer()
    {
        var (_, token) = await _factory.SeedScimClientAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var config = await (await _client.GetAsync("/scim/v2/ServiceProviderConfig"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var documentationUri = config.GetProperty("documentationUri").GetString();
        Assert.False(string.IsNullOrWhiteSpace(documentationUri));

        var uri = new Uri(documentationUri!, UriKind.Absolute);

        // The issuer under test is this host, so "not this origin" is the decidable form of "a URL this
        // server does not have to serve".
        var issuer = new Uri(AuthagonalTestFactory.TestIssuer, UriKind.Absolute);
        Assert.NotEqual(issuer.Host, uri.Host);
        Assert.Equal("https", uri.Scheme);
    }

    // ── #38: RedirectUrl was required and read by nothing ────────────────────

    /// <summary>
    /// Creating an OIDC connection without a <c>RedirectUrl</c> is accepted.
    /// </summary>
    /// <remarks>
    /// The admin API answered 400 "RedirectUrl is required" and config seeding threw
    /// <c>InvalidOperationException</c> at startup — for a value nothing reads. Both legs of the
    /// federation flow compute the callback per request from the issuer
    /// (<c>OidcEndpoints.CallbackUriFor</c>), because it has to be on the origin the browser is on, so no
    /// stored value could be correct for every tenant sharing a connection. An administrator was
    /// therefore required to supply a field, given no validation of it, and given no indication that it
    /// did nothing.
    /// </remarks>
    [Fact]
    public async Task AnOidcConnectionCanBeCreatedWithNoRedirectUrl()
    {
        var admin = await _factory.GetAdminTokenAsync(_client);

        var response = await PostOidcConnectionAsync(admin, new
        {
            connectionName = "Acme",
            metadataLocation = "https://idp.example.com/.well-known/openid-configuration",
            clientId = "c",
            clientSecret = "s",
        });

        Assert.True(
            response.IsSuccessStatusCode,
            $"creating a connection without RedirectUrl was refused: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>A supplied value is still accepted, so no existing admin client breaks.</summary>
    [Fact]
    public async Task AnOidcConnectionCanStillBeCreatedWithOne()
    {
        var admin = await _factory.GetAdminTokenAsync(_client);

        var response = await PostOidcConnectionAsync(admin, new
        {
            connectionName = "Acme Two",
            metadataLocation = "https://idp2.example.com/.well-known/openid-configuration",
            clientId = "c",
            clientSecret = "s",
            redirectUrl = "https://auth.example.com/oidc/callback",
        });

        Assert.True(response.IsSuccessStatusCode);
    }

    /// <summary>
    /// The callback the flow actually uses comes from one place, and both legs must agree.
    /// </summary>
    /// <remarks>
    /// The literal <c>/oidc/callback</c> existed in three copies — the route registration and both legs —
    /// and the two legs have to match exactly, because the upstream compares the <c>redirect_uri</c> on
    /// the token exchange with the one on the authorize request and rejects a mismatch. A trailing slash
    /// on the issuer is the way that breaks in practice.
    /// </remarks>
    [Theory]
    [InlineData("https://auth.example.com", "https://auth.example.com/oidc/callback")]
    [InlineData("https://auth.example.com/", "https://auth.example.com/oidc/callback")]
    [InlineData("https://auth.example.com///", "https://auth.example.com/oidc/callback")]
    public void TheCallbackUriIsDerivedFromTheIssuer(string issuer, string expected)
        => Assert.Equal(expected, Authagonal.Server.Endpoints.OidcEndpoints.CallbackUriFor(issuer));

    /// <summary>The route that is mapped is the one the derived URI names.</summary>
    [Fact]
    public async Task TheDerivedCallbackNamesAMappedRoute()
    {
        var path = new Uri(
            Authagonal.Server.Endpoints.OidcEndpoints.CallbackUriFor("https://auth.example.com")).AbsolutePath;

        // No state cookie and no code, so this cannot complete — but a mapped route answers something
        // other than the SPA fallback's HTML, which is what a wrong path would return.
        var response = await _client.GetAsync(path);

        Assert.DoesNotContain("text/html", response.Content.Headers.ContentType?.ToString() ?? "");
    }

    /// <summary>
    /// Seeding a provider with no <c>RedirectUrl</c> no longer takes the host down at startup.
    /// </summary>
    /// <remarks>
    /// <c>ProviderSeedService</c> threw <c>InvalidOperationException</c> from a hosted service's
    /// <c>StartAsync</c> — so omitting a field that does nothing prevented the identity provider from
    /// starting at all. Driven directly rather than through the host, because that is where the throw was.
    /// </remarks>
    [Fact]
    public async Task ConfigSeedingWithoutARedirectUrlSucceeds()
    {
        var oidc = new InMemoryOidcProviderStore();
        var seeder = NewSeeder(oidc, new Dictionary<string, string?>
        {
            ["Issuer"] = "https://auth.example.com",
            ["OidcProviders:0:ConnectionId"] = "seeded",
            ["OidcProviders:0:ConnectionName"] = "Seeded",
            ["OidcProviders:0:MetadataLocation"] = "https://idp.example.com/.well-known/openid-configuration",
            ["OidcProviders:0:ClientId"] = "c",
            ["OidcProviders:0:ClientSecret"] = "s",
        });

        await seeder.StartAsync(CancellationToken.None);

        var seeded = await oidc.GetAsync("seeded");
        Assert.NotNull(seeded);
        Assert.Equal("", seeded!.RedirectUrl);
    }

    /// <summary>A seed that names one is stored as given, and the ignored value is reported.</summary>
    [Fact]
    public async Task ASeededRedirectUrlThatIsNotTheDerivedOneIsLogged()
    {
        var oidc = new InMemoryOidcProviderStore();
        var logger = new ListLogger<ProviderSeedService>();
        var seeder = NewSeeder(oidc, new Dictionary<string, string?>
        {
            ["Issuer"] = "https://auth.example.com",
            ["OidcProviders:0:ConnectionId"] = "seeded",
            ["OidcProviders:0:MetadataLocation"] = "https://idp.example.com/.well-known/openid-configuration",
            ["OidcProviders:0:ClientId"] = "c",
            ["OidcProviders:0:ClientSecret"] = "s",
            ["OidcProviders:0:RedirectUrl"] = "https://somewhere.else.example/callback",
        }, logger);

        await seeder.StartAsync(CancellationToken.None);

        Assert.Contains(logger.Messages, m =>
            m.Contains("ignored", StringComparison.OrdinalIgnoreCase)
            && m.Contains("https://auth.example.com/oidc/callback", StringComparison.Ordinal));
    }

    /// <summary>And the derived value produces no noise, so the warning stays meaningful.</summary>
    [Fact]
    public async Task ASeededRedirectUrlThatMatchesTheDerivedOneIsNotLogged()
    {
        var oidc = new InMemoryOidcProviderStore();
        var logger = new ListLogger<ProviderSeedService>();
        var seeder = NewSeeder(oidc, new Dictionary<string, string?>
        {
            ["Issuer"] = "https://auth.example.com",
            ["OidcProviders:0:ConnectionId"] = "seeded",
            ["OidcProviders:0:MetadataLocation"] = "https://idp.example.com/.well-known/openid-configuration",
            ["OidcProviders:0:ClientId"] = "c",
            ["OidcProviders:0:ClientSecret"] = "s",
            ["OidcProviders:0:RedirectUrl"] = "https://auth.example.com/oidc/callback",
        }, logger);

        await seeder.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Messages, m => m.Contains("ignored", StringComparison.OrdinalIgnoreCase));
    }

    private static ProviderSeedService NewSeeder(
        InMemoryOidcProviderStore oidc,
        Dictionary<string, string?> settings,
        ILogger<ProviderSeedService>? logger = null)
        => new(
            new InMemorySamlProviderStore(),
            oidc,
            new InMemorySsoDomainStore(),
            new PlaintextSecretProvider(),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            logger ?? NullLogger<ProviderSeedService>.Instance);

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private Task<HttpResponseMessage> PostOidcConnectionAsync(string adminToken, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/oidc/connections/")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        return _client.SendAsync(request);
    }
}

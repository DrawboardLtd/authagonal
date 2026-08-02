using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Structural piece 4 — assertions about the shared protocol surface, run against BOTH hosts.
/// </summary>
/// <remarks>
/// The tree serves <c>/connect/*</c> from two places: <c>Authagonal.Server</c>, and
/// <c>Authagonal.Protocol</c>, which is the package that ships to nuget.org and the one an embedding consumer
/// actually runs. Tests were written against whichever host the author had open, so a fix could land in one
/// and be missed in the other with a green suite on both sides. Eight findings had that shape, and the
/// clearest was the Server host's <c>/connect/userinfo</c> not requiring the <c>openid</c> scope — the exact
/// defect its Protocol twin had already been fixed for.
/// <para>
/// So these are written once and parameterised by host. Each seeds its OWN client through the shared
/// <c>IClientStore</c> rather than using whatever the host happened to seed, because the point is to compare
/// behaviour and that requires the input to be identical — the two hosts' own fixtures differ in client id,
/// secret and scope names, which is how "the same test" quietly became two different tests before.
/// </para>
/// </remarks>
public sealed class BothHostsProtocolTests
{
    private const string ClientId = "both-hosts-machine";
    private const string ClientSecret = "both-hosts-secret-123";
    private const string ApiScope = "both-hosts-api";

    public static TheoryData<Func<IProtocolSurfaceHost>> Hosts() => BothProtocolHosts.All();

    /// <summary>
    /// Seeds one confidential client and one non-OIDC scope, identically on either host.
    /// </summary>
    /// <remarks>
    /// BCrypt because it is the one secret format both hosts' verifiers accept: the Server host resolves
    /// <c>PasswordHasherClientSecretVerifier</c> and the Protocol package its own default, and this is where
    /// they overlap.
    /// </remarks>
    private static async Task SeedAsync(IProtocolSurfaceHost host)
    {
        if (await host.Scopes.GetAsync(ApiScope) is null)
            await host.Scopes.CreateAsync(new Scope { Name = ApiScope, DisplayName = "Both hosts API" });

        await host.Clients.UpsertAsync(new OAuthClient
        {
            ClientId = ClientId,
            ClientName = "Both-hosts machine client",
            RequireClientSecret = true,
            RequirePkce = false,
            ClientSecretHashes = [BCrypt.Net.BCrypt.HashPassword(ClientSecret)],
            AllowedGrantTypes = ["client_credentials"],
            AllowedScopes = ["openid", ApiScope],
            AccessTokenLifetimeSeconds = 3600,
        });
    }

    private static async Task<HttpResponseMessage> TokenAsync(IProtocolSurfaceHost host, string scope)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = scope,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")));

        return await host.Client.SendAsync(request);
    }

    private static async Task<string> AccessTokenAsync(IProtocolSurfaceHost host, string scope)
    {
        using var response = await TokenAsync(host, scope);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }

    /// <summary>
    /// <c>/connect/userinfo</c> requires the <c>openid</c> scope on every host that serves it.
    /// </summary>
    /// <remarks>
    /// OIDC Core §5.3: userinfo is an OIDC endpoint and a pure-OAuth access token is not a ticket to it. The
    /// Server host's comment above its validation parameters asserted the requirement and nothing enforced
    /// it, so a token carrying only an API scope read the endpoint and got <c>sub</c> back — while the
    /// Protocol host had been fixed. This is the assertion that makes that divergence impossible to repeat.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Userinfo_RequiresTheOpenidScope(Func<IProtocolSurfaceHost> newHost)
    {
        await using var host = newHost();
        await host.InitializeAsync();
        await SeedAsync(host);

        var token = await AccessTokenAsync(host, ApiScope);

        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("insufficient_scope", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: the same token WITH <c>openid</c> is not refused for want of scope.
    /// </summary>
    /// <remarks>
    /// Without this, "refuse everything" satisfies the assertion above on both hosts. A client-credentials
    /// token has no user behind it, so the endpoint may still decline for other reasons — what is asserted is
    /// that the reason is no longer the scope.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Userinfo_WithTheOpenidScope_IsNotRefusedForScope(Func<IProtocolSurfaceHost> newHost)
    {
        await using var host = newHost();
        await host.InitializeAsync();
        await SeedAsync(host);

        var token = await AccessTokenAsync(host, $"openid {ApiScope}");

        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await host.Client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("insufficient_scope", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scope the client did not declare is refused on both hosts, and as <c>invalid_scope</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Token_RefusesAnUndeclaredScope(Func<IProtocolSurfaceHost> newHost)
    {
        await using var host = newHost();
        await host.InitializeAsync();
        await SeedAsync(host);

        using var response = await TokenAsync(host, "some-scope-this-client-never-declared");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_scope", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// A wrong client secret is <c>invalid_client</c> with a 401, on both hosts.
    /// </summary>
    /// <remarks>
    /// RFC 6749 §5.2. The two hosts resolve different <c>IClientSecretVerifier</c> implementations, so this
    /// is a place where they could disagree about the outcome of the same credential — and a 400 where a 401
    /// belongs changes how a conforming client reacts.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Token_RefusesAWrongSecretAsInvalidClient(Func<IProtocolSurfaceHost> newHost)
    {
        await using var host = newHost();
        await host.InitializeAsync();
        await SeedAsync(host);

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = ApiScope,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:not-the-secret")));

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_client", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// A token response is never cacheable, on either host.
    /// </summary>
    /// <remarks>
    /// RFC 6749 §5.1 requires <c>Cache-Control: no-store</c> on a response carrying tokens. A shared cache or
    /// a browser that stores one hands the next requester a live access token.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Token_IsNotCacheable(Func<IProtocolSurfaceHost> newHost)
    {
        await using var host = newHost();
        await host.InitializeAsync();
        await SeedAsync(host);

        using var response = await TokenAsync(host, ApiScope);
        response.EnsureSuccessStatusCode();

        Assert.True(response.Headers.CacheControl?.NoStore,
            $"{host.HostName} returned a token response without Cache-Control: no-store");
    }

    /// <summary>
    /// Discovery advertises the issuer each host was configured with, and advertises it consistently.
    /// </summary>
    /// <remarks>
    /// The issuer in the document is what every relying party pins, and both hosts build it from their own
    /// configuration — so this is the cheapest possible cross-host check that the two are wired to the same
    /// contract rather than to two similar ones.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Discovery_AdvertisesTheConfiguredIssuer(Func<IProtocolSurfaceHost> newHost)
    {
        await using var host = newHost();
        await host.InitializeAsync();

        var body = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");

        Assert.Equal(host.Issuer, body.GetProperty("issuer").GetString());
        Assert.Equal($"{host.Issuer}/connect/token", body.GetProperty("token_endpoint").GetString());
        Assert.Equal($"{host.Issuer}/connect/userinfo", body.GetProperty("userinfo_endpoint").GetString());
    }
}

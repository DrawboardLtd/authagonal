using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// RFC 6749 §3.1/§3.2 require TLS at the authorization and token endpoints. UseAuthagonal refuses
/// plaintext requests to /connect/* unless Auth:AllowInsecureHttp says otherwise; TestServer speaks
/// http, so the harness sets that opt-in and these tests turn it back off to exercise the gate.
/// </summary>
public sealed class TransportSecurityTests
{
    [Fact]
    public async Task Authorize_OverPlainHttp_IsRefused_WhenInsecureHttpNotAllowed()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var response = await client.GetAsync(
            "/connect/authorize?client_id=test-client&response_type=code&scope=openid" +
            "&redirect_uri=https%3A%2F%2Fapp.test%2Fcallback&code_challenge=abc&code_challenge_method=S256");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request", body.RootElement.GetProperty("error").GetString());
        Assert.Contains("TLS is required", body.RootElement.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task Token_OverPlainHttp_IsRefused_WhenInsecureHttpNotAllowed()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var response = await client.SendAsync(BuildClientCredentialsRequest());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request", body.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// The gate reads the scheme after forwarded-header processing, which is the whole point: in the
    /// deployment it is written for, TLS terminates at an ingress and the request reaches Kestrel as
    /// plain http, so X-Forwarded-Proto is the only truthful answer to "was this encrypted".
    /// </summary>
    [Fact]
    public async Task Token_OverPlainHttp_IsAllowed_WhenProxyForwardsHttpsScheme()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var request = BuildClientCredentialsRequest();
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(token.GetProperty("access_token").GetString()));
    }

    /// <summary>
    /// Only the protocol surface the RFCs name is gated. The health endpoint has to answer over
    /// plaintext or an ingress cannot probe the pod before TLS is in front of it.
    /// </summary>
    [Fact]
    public async Task Health_OverPlainHttp_IsNotGated()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowInsecureHttp = false,
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/health");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Token_OverPlainHttp_IsAllowed_WhenInsecureHttpOptInIsSet()
    {
        // The harness default — this is what keeps docker-compose and the suite working.
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var response = await client.SendAsync(BuildClientCredentialsRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpRequestMessage BuildClientCredentialsRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = AuthagonalTestFactory.AdminScope,
            }),
        };
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{AuthagonalTestFactory.AdminClientId}:{AuthagonalTestFactory.AdminClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        return request;
    }
}

/// <summary>
/// SecretProvider:RequireVaultReferences closes the unprefixed-reference bypass in the vault-backed
/// providers, where a reference without a <c>kv:</c> / <c>sm:</c> prefix is otherwise returned as the
/// secret value itself.
/// </summary>
public sealed class VaultReferenceRequirementTests
{
    [Fact]
    public async Task KeyVault_UnprefixedReference_IsReturnedVerbatim_ByDefault()
    {
        // The vault client is never reached on this path — that is exactly the problem being pinned.
        var provider = new KeyVaultSecretProvider(
            null!, new SecretProviderOptions(), NullLogger<KeyVaultSecretProvider>.Instance);

        Assert.Equal("legacy-plaintext-secret", await provider.ResolveAsync("legacy-plaintext-secret"));
    }

    [Fact]
    public async Task KeyVault_UnprefixedReference_Throws_WhenVaultReferencesRequired()
    {
        var provider = new KeyVaultSecretProvider(
            null!,
            new SecretProviderOptions { RequireVaultReferences = true },
            NullLogger<KeyVaultSecretProvider>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ResolveAsync("legacy-plaintext-secret"));

        // The message must never quote the reference: on this branch it IS the cleartext secret.
        Assert.DoesNotContain("legacy-plaintext-secret", ex.Message);
        Assert.Contains("RequireVaultReferences", ex.Message);
    }
}

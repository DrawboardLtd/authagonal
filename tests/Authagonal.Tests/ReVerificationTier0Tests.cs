using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Oidc;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// The 2026-08-01 comparative re-run checked all 342 findings against the tree rather than against the
// commit history, and found 97 where something survived. These pin the Tier 0 set — the ones an
// unauthenticated or barely-authenticated attacker could still reach.
//
// Every one of them is a SIBLING PATH: a fix that landed in one host, provider, or call site and not
// its twin. That is the pattern worth defending against, so each test here names the sibling.
// -------------------------------------------------------------------------------------------------

/// <summary>
/// #163 — the OIDC discovery document is the trust anchor for an entire federated connection: `issuer`
/// out of it becomes ValidIssuer and `jwks_uri` out of it supplies the keys every upstream id_token is
/// checked against. Fetched over plaintext, both halves are substitutable together by anyone on the
/// path, and the callback then signs an attacker's assertion in as any user.
/// </summary>
public sealed class OidcDiscoveryTrustAnchorTests
{
    [Fact]
    public async Task PlaintextDiscoveryUrl_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client().GetDiscoveryAsync("http://idp.test/.well-known/openid-configuration"));

        Assert.Contains("https", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The SAML sibling has required https at its metadata URL since the outbound-URL work. This is the
    /// same requirement on the OIDC side, which is where it was missing.
    /// </summary>
    [Fact]
    public async Task RelativeOrUnparseableDiscoveryUrl_IsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client().GetDiscoveryAsync("/.well-known/openid-configuration"));
    }

    /// <summary>
    /// OIDC Discovery §4.3. Without the binding, `issuer` is whatever the document says it is — so
    /// whoever can serve the URL can claim to BE any issuer, and the downstream ValidIssuer check
    /// compares the forged document against itself.
    /// </summary>
    [Fact]
    public async Task IssuerThatDoesNotMatchTheDiscoveryUrl_IsRefused()
    {
        var handler = new OidcMockHandler { Issuer = "https://the-real-idp.test" };
        var client = Client(handler);

        // Served from a host the issuer does not vouch for.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetDiscoveryAsync("https://attacker.test/.well-known/openid-configuration"));

        Assert.Contains("issuer mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The control: the conventional, matching URL still resolves.</summary>
    [Fact]
    public async Task IssuerMatchingTheDiscoveryUrl_IsAccepted()
    {
        var handler = new OidcMockHandler { Issuer = "https://oidc-idp.test" };

        var doc = await Client(handler).GetDiscoveryAsync(
            "https://oidc-idp.test/.well-known/openid-configuration");

        Assert.Equal("https://oidc-idp.test", doc.Issuer);
    }

    private static OidcDiscoveryClient Client(HttpMessageHandler? handler = null) =>
        new(new StubHttpClientFactory(handler ?? new OidcMockHandler()),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new CacheOptions()));

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>
/// #132 — SCIM enforced its email-domain allowlist on create only. The update paths re-checked
/// plausibility and the global email index, but not the domain, so a rename walked straight around it.
/// </summary>
public sealed class ScimEmailDomainRenameTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new()
    {
        Configuration = { ["Scim:Clients:scim-client:AllowedEmailDomains:0"] = "allowed.example" },
    };

    [Fact]
    public async Task RenamingToADomainTheClientDoesNotOwn_IsRefused_OnPut()
    {
        var (client, id) = await SeedUserAsync();

        var response = await client.PutAsJsonAsync($"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "ceo@corp.example",
            active = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("corp.example", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RenamingToADomainTheClientDoesNotOwn_IsRefused_OnPatch()
    {
        var (client, id) = await SeedUserAsync();

        var response = await client.PatchAsync($"/scim/v2/Users/{id}", JsonContent.Create(new
        {
            schemas = new[] { "urn:ietf:params:scim:PatchOp" },
            Operations = new[] { new { op = "replace", path = "userName", value = "ceo@corp.example" } },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The control. Without it, both refusals above are consistent with rename being broken outright.
    /// </summary>
    [Fact]
    public async Task RenamingWithinTheAllowedDomain_IsStillPermitted()
    {
        var (client, id) = await SeedUserAsync();

        var response = await client.PutAsJsonAsync($"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "renamed@allowed.example",
            active = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("renamed@allowed.example", json.GetProperty("userName").GetString());
    }

    private async Task<(HttpClient Client, string Id)> SeedUserAsync()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var created = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "starter@allowed.example",
            active = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        return (client, json.GetProperty("id").GetString()!);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

/// <summary>
/// #59 — RFC 9068 §4: a resource server MUST reject a JWT whose typ is not at+jwt. Every token this
/// issuer mints shares one signing key and one issuer, so without the check an id_token — minted for a
/// browser, not a credential for any API — passes issuer, audience, lifetime and algorithm validation
/// on the host's own bearer scheme. The token-exchange path had pinned this for some time; the scheme
/// every consumer gets from AddAuthagonal had not.
/// </summary>
public sealed class ResourceServerTokenTypeTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    /// <summary>
    /// 401, not 403, is the whole assertion. Before the fix the id_token AUTHENTICATED and was refused
    /// only by the admin scope policy, so the API was protected by authorization alone and any endpoint
    /// relying on the scheme itself was not protected at all.
    /// </summary>
    [Fact]
    public async Task IdTokenPresentedAsABearerToken_FailsAuthentication()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        var idToken = await MintIdTokenAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The control: a real access token from the same issuer and key still authenticates.</summary>
    [Fact]
    public async Task AccessToken_StillAuthenticates()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        var adminToken = await _factory.GetAdminTokenAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clients");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> MintIdTokenAsync(HttpClient client)
    {
        await _factory.SeedTestUserAsync();
        await client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });

        var verifier = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var authorize = await client.GetAsync(
            $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://app.test/callback")}" +
            $"&response_type=code&scope=openid+profile&state=s" +
            $"&code_challenge={challenge}&code_challenge_method=S256");
        var code = System.Web.HttpUtility.ParseQueryString(authorize.Headers.Location!.Query)["code"]!;

        var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "https://app.test/callback",
                ["code_verifier"] = verifier,
                ["client_id"] = AuthagonalTestFactory.TestClientId,
            }));
        tokenResponse.EnsureSuccessStatusCode();

        var json = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id_token").GetString()!;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

/// <summary>
/// #38 — while a credential is staged, only a link bound to that exact credential may promote it. The
/// acceptance test used to be "if a binding is present it must match", which accepts a link carrying no
/// binding at all — and the tree still mints those: the admin resend and admin-create links are bare
/// three-segment tokens, and either can be requested while a claim is pending.
/// </summary>
public sealed class PasswordlessClaimBindingTests
{
    [Fact]
    public async Task AnUnboundVerificationLink_DoesNotPromoteAStagedCredential()
    {
        await using var factory = new AuthagonalTestFactory
        {
            ConfigureAuthOptions = o => o.AllowPasswordlessAccountClaim = true,
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        var federated = new AuthUser
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = "victim@example.com",
            NormalizedEmail = "VICTIM@EXAMPLE.COM",
            PasswordHash = null,
            EmailConfirmed = true,
            LockoutEnabled = true,
            SecurityStamp = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await factory.UserStore.CreateAsync(federated);

        // The attacker claims the account. The credential is staged, not active.
        var register = await client.PostAsJsonAsync("/api/auth/register",
            new { email = "victim@example.com", password = "Attacker1234!", firstName = "A", lastName = "B" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var staged = await factory.UserStore.GetAsync(federated.Id);
        Assert.NotNull(staged!.PendingPasswordHash);

        // A bare three-segment link, exactly as the admin resend path mints one: correct stamp, correct
        // address, no pc= binding. The genuine owner would receive this as an ordinary verification mail.
        var unbound = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{staged.SecurityStamp}||{staged.Email}||{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}"));

        var confirm = await client.PostAsync("/api/auth/confirm-email",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", unbound)]));

        // The staged credential must NOT have been promoted...
        var after = await factory.UserStore.GetAsync(federated.Id);
        Assert.Null(after!.PasswordHash);

        // ...and the attacker's password must not work.
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "victim@example.com", password = "Attacker1234!" });
        Assert.NotEqual(HttpStatusCode.OK, login.StatusCode);

        Assert.NotEqual(HttpStatusCode.OK, confirm.StatusCode);
    }
}

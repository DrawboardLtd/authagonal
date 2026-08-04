using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// A SCIM credential's email-domain bound: the only control over WHICH identities it may create.
/// </summary>
/// <remarks>
/// It was reachable solely from <c>Scim:Clients:{clientId}:AllowedEmailDomains</c> — a configuration key no
/// document mentioned and which <c>POST /api/v1/scim/tokens</c>, the documented way to mint a SCIM credential,
/// could not write. So the documented onboarding produced an unrestricted directory-wide credential and there
/// was no way to narrow it without hand-editing configuration.
/// <para>
/// Unrestricted is wider than it sounds. A SCIM-created user is written <c>EmailConfirmed = true</c>, so the
/// connector can mint <c>ceo@someone-elses-domain.example</c> as a pre-verified account;
/// <c>FederationAdoptionPolicy</c> adopts a record with no external logins unconditionally, so the real owner's
/// first federated sign-in binds to it; and <c>ScimProvisionedByClientId</c> leaves the squatting connector in
/// full ownership of that object — able to rename it, deactivate it (revoking every grant), or delete it, which
/// purges the victim's passkeys and tombstones the row so the legitimate connector gets 404 forever.
/// </para>
/// </remarks>
public sealed class ScimDomainScopingTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    private async Task<HttpClient> ScimClientAsync(params string[] allowedDomains)
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync(allowedEmailDomains: allowedDomains);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        return client;
    }

    private static Task<HttpResponseMessage> CreateUserAsync(HttpClient client, string userName) =>
        client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName,
            active = true,
        });

    [Fact]
    public async Task ATokenBoundToADomain_RefusesEveryOtherDomain()
    {
        var client = await ScimClientAsync("acme.example");

        Assert.Equal(HttpStatusCode.Created, (await CreateUserAsync(client, "alice@acme.example")).StatusCode);

        var refused = await CreateUserAsync(client, "ceo@someone-else.example");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("not permitted", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>The bound is enforced on rename too, or creating in-domain then renaming out is the way round it.</summary>
    [Fact]
    public async Task ATokenBoundToADomain_CannotRenameAUserOutOfIt()
    {
        var client = await ScimClientAsync("acme.example");

        var created = await CreateUserAsync(client, "alice@acme.example");
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var replaced = await client.PutAsJsonAsync($"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "ceo@someone-else.example",
            active = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, replaced.StatusCode);

        var patch = new StringContent("""
            {
              "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "Operations": [{ "op": "replace", "path": "userName", "value": "ceo@someone-else.example" }]
            }
            """, Encoding.UTF8, "application/scim+json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsync($"/scim/v2/Users/{id}", patch)).StatusCode);
    }

    [Fact]
    public async Task MultipleDomainsAreAllPermitted()
    {
        var client = await ScimClientAsync("acme.example", "acme-eu.example");

        Assert.Equal(HttpStatusCode.Created, (await CreateUserAsync(client, "a@acme.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateUserAsync(client, "b@acme-eu.example")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateUserAsync(client, "c@other.example")).StatusCode);
    }

    [Fact]
    public async Task TheBoundIsCaseInsensitive()
    {
        var client = await ScimClientAsync("ACME.example");
        Assert.Equal(HttpStatusCode.Created, (await CreateUserAsync(client, "a@acme.EXAMPLE")).StatusCode);
    }

    /// <summary>
    /// The historical default: no bound anywhere means unrestricted, so an upgrade does not start refusing.
    /// </summary>
    [Fact]
    public async Task ATokenWithNoBound_IsStillUnrestricted()
    {
        var client = await ScimClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await CreateUserAsync(client, "anyone@anywhere.example")).StatusCode);
    }

    // ── the admin API can now actually set it ────────────────────────────────

    [Fact]
    public async Task MintingATokenRecordsAndEchoesItsDomainBound()
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient();
        var adminToken = await _factory.GetAdminTokenAsync(client);

        await _factory.ClientStore.UpsertAsync(new Authagonal.Core.Models.OAuthClient
        {
            ClientId = "hr-sync", ClientName = "HR", RequireClientSecret = false,
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scim/tokens")
        {
            Content = JsonContent.Create(new
            {
                clientId = "hr-sync",
                allowedEmailDomains = new[] { "acme.example" },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("acme.example", Assert.Single(json.GetProperty("allowedEmailDomains").EnumerateArray()).GetString());

        // And the minted credential is actually bound by it.
        var raw = json.GetProperty("token").GetString()!;
        var scim = _factory.CreateClient();
        scim.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateUserAsync(scim, "x@other.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateUserAsync(scim, "x@acme.example")).StatusCode);
    }

    /// <summary>A value that could never match is refused rather than stored as a bound permitting nothing.</summary>
    [Theory]
    [InlineData("@acme.example")]
    [InlineData("alice@acme.example")]
    [InlineData("acme example")]
    [InlineData("localhost")]
    public async Task MintingRefusesSomethingThatIsNotADomain(string value)
    {
        await _factory.SeedTestDataAsync();
        var client = _factory.CreateClient();
        var adminToken = await _factory.GetAdminTokenAsync(client);

        await _factory.ClientStore.UpsertAsync(new Authagonal.Core.Models.OAuthClient
        {
            ClientId = "hr-sync", ClientName = "HR", RequireClientSecret = false,
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scim/tokens")
        {
            Content = JsonContent.Create(new { clientId = "hr-sync", allowedEmailDomains = new[] { value } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The admin writes that decide who may sign in, and as whom, leave an attributable audit row.
/// </summary>
/// <remarks>
/// IAuditLogger was called by exactly three endpoint groups — clients, provisioning apps, agents — while
/// the account-takeover-grade operations called it nowhere. Minting a directory-wide SCIM credential,
/// attaching a federated IdP for a domain, granting a role and ungating a scope all completed with no
/// record of who did them, so an operator reconstructing an incident could see who renamed a client but
/// not who created the connection that now vouches for the whole domain.
/// </remarks>
public sealed class AdminWriteAuditTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _adminToken = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.SeedTestDataAsync();
        _adminToken = await _factory.GetAdminTokenAsync(_client);
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private (string Actor, string Action, string EntityType, string? EntityId, string? Detail) Entry(string action)
        => Assert.Single(_factory.AuditLog.Entries.Where(e => e.Action == action));

    [Fact]
    public async Task ScimTokenMintAndRevoke_AreAudited()
    {
        await _factory.ClientStore.UpsertAsync(new Authagonal.Core.Models.OAuthClient
        {
            ClientId = "scim-audited-client",
            ClientName = "SCIM Audited",
        });

        var created = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scim/tokens",
            new { clientId = "scim-audited-client", description = "Audited" }));
        created.EnsureSuccessStatusCode();
        var tokenId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tokenId").GetString();

        var mint = Entry("scim_token.created");
        Assert.Equal(AuthagonalTestFactory.AdminClientId, mint.Actor);
        Assert.Equal(tokenId, mint.EntityId);

        var revoked = await _client.SendAsync(AdminRequest(HttpMethod.Delete,
            $"/api/v1/scim/tokens/{tokenId}?clientId=scim-audited-client"));
        revoked.EnsureSuccessStatusCode();

        Assert.Equal(tokenId, Entry("scim_token.revoked").EntityId);
    }

    [Fact]
    public async Task SamlConnectionLifecycle_IsAudited()
    {
        var created = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/saml/connections",
            new
            {
                connectionName = "Audited IdP",
                entityId = "https://idp.test/saml",
                metadataLocation = "https://idp.test/metadata",
                allowedDomains = new[] { "audited.test" },
            }));
        created.EnsureSuccessStatusCode();
        var connectionId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("connectionId").GetString()!;

        // The claimed domain travels in the detail: which domain an IdP was attached to is the fact that
        // turns the row into an incident.
        var create = Entry("saml_connection.created");
        Assert.Equal(connectionId, create.EntityId);
        Assert.Contains("audited.test", create.Detail);

        var updated = await _client.SendAsync(AdminRequest(HttpMethod.Put, $"/api/v1/saml/connections/{connectionId}",
            new { allowedDomains = new[] { "repointed.test" } }));
        updated.EnsureSuccessStatusCode();
        Assert.Contains("repointed.test", Entry("saml_connection.updated").Detail);

        var deleted = await _client.SendAsync(AdminRequest(HttpMethod.Delete, $"/api/v1/saml/connections/{connectionId}"));
        deleted.EnsureSuccessStatusCode();
        Assert.Equal(connectionId, Entry("saml_connection.deleted").EntityId);
    }

    [Fact]
    public async Task OidcConnectionLifecycle_IsAudited()
    {
        var created = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/oidc/connections",
            new
            {
                connectionName = "Audited OP",
                metadataLocation = "https://op.test/.well-known/openid-configuration",
                clientId = "op-client",
                clientSecret = "op-secret",
                redirectUrl = "https://test.authagonal.local/signin-oidc",
                allowedDomains = new[] { "op.test" },
            }));
        created.EnsureSuccessStatusCode();
        var connectionId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("connectionId").GetString()!;

        Assert.Contains("op.test", Entry("oidc_connection.created").Detail);

        var deleted = await _client.SendAsync(AdminRequest(HttpMethod.Delete, $"/api/v1/oidc/connections/{connectionId}"));
        deleted.EnsureSuccessStatusCode();
        Assert.Equal(connectionId, Entry("oidc_connection.deleted").EntityId);
    }

    [Fact]
    public async Task RoleCreationAndAssignment_AreAudited()
    {
        var created = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles",
            new { name = "audited-role", description = "For the trail" }));
        created.EnsureSuccessStatusCode();
        Assert.Equal("audited-role", Entry("role.created").Detail);

        var user = await _factory.SeedTestUserAsync("role-audit@example.com");

        var assigned = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles/assign",
            new { userId = user.Id, roleName = "audited-role" }));
        assigned.EnsureSuccessStatusCode();

        // Granting a role lands in the next token's `roles` claim and passes every scope gate naming it —
        // a privilege grant, and it recorded nothing.
        var grant = Entry("role.assigned");
        Assert.Equal(user.Id, grant.EntityId);
        Assert.Equal("audited-role", grant.Detail);

        var unassigned = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/roles/unassign",
            new { userId = user.Id, roleName = "audited-role" }));
        unassigned.EnsureSuccessStatusCode();
        Assert.Equal(user.Id, Entry("role.unassigned").EntityId);
    }

    [Fact]
    public async Task ScopeGateEdits_AreAudited()
    {
        var created = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/api/v1/scopes",
            new { name = "audited.scope", displayName = "Audited", allowedRoles = new[] { "admin" } }));
        created.EnsureSuccessStatusCode();
        Assert.Contains("admin", Entry("scope.created").Detail);

        // Clearing AllowedRoles means "anyone may hold this scope" — a silent privilege widening.
        var updated = await _client.SendAsync(AdminRequest(HttpMethod.Put, "/api/v1/scopes/audited.scope",
            new { allowedRoles = Array.Empty<string>() }));
        updated.EnsureSuccessStatusCode();
        Assert.Equal("ungated", Entry("scope.updated").Detail);

        var deleted = await _client.SendAsync(AdminRequest(HttpMethod.Delete, "/api/v1/scopes/audited.scope"));
        deleted.EnsureSuccessStatusCode();
        Assert.Equal("audited.scope", Entry("scope.deleted").EntityId);
    }
}

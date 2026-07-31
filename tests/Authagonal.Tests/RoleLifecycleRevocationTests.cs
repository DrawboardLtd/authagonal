using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Role lifecycle as a privilege-revocation path. Entitlement is carried by the role NAME string in
/// <c>AuthUser.Roles</c> and <c>Scope.AllowedRoles</c> — nothing resolves either against the role
/// store — so deleting or renaming the role row revoked nothing while removing it from the admin
/// console, which is the operator being actively misled about a privilege they believe they removed.
/// </summary>
public sealed class RoleLifecycleRevocationTests : IAsyncLifetime
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

    // -----------------------------------------------------------------------
    // Delete
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeletingRoleWithHolders_IsRefusedRatherThanSilentlyRevokingNothing()
    {
        var (roleId, user) = await SeedRoleWithHolderAsync("owner");

        var response = await Send(HttpMethod.Delete, $"/api/v1/roles/{roleId}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("role_has_members", error.GetProperty("error").GetString());
        Assert.Contains("1 user", error.GetProperty("error_description").GetString()!);

        // Still listed, so the operator can still find out who holds it — deleting used to destroy
        // the only enumeration API along with the role.
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/api/v1/roles/{roleId}")).StatusCode);
        Assert.Contains("owner", (await _factory.UserStore.GetAsync(user.Id))!.Roles);
    }

    [Fact]
    public async Task ForceDeletingRole_RevokesItFromEveryHolder()
    {
        var (roleId, user) = await SeedRoleWithHolderAsync("owner");

        var response = await Send(HttpMethod.Delete, $"/api/v1/roles/{roleId}?force=true");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.DoesNotContain("owner", (await _factory.UserStore.GetAsync(user.Id))!.Roles);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/api/v1/roles/{roleId}")).StatusCode);
    }

    [Fact]
    public async Task ForceDeletingRole_ClearsItFromEveryScopeGate()
    {
        var (roleId, _) = await SeedRoleWithHolderAsync("owner");
        await _factory.ScopeStore.CreateAsync(new Scope
        {
            Name = "billing:write",
            AllowedRoles = ["owner", "finance"],
        });

        await Send(HttpMethod.Delete, $"/api/v1/roles/{roleId}?force=true");

        var scope = await _factory.ScopeStore.GetAsync("billing:write");
        Assert.NotNull(scope);
        Assert.DoesNotContain("owner", scope.AllowedRoles);
        Assert.Contains("finance", scope.AllowedRoles);
    }

    [Fact]
    public async Task DeletingRoleWithNoHolders_StillSucceedsWithoutForce()
    {
        var roleId = await CreateRoleAsync("unused-role");

        Assert.Equal(HttpStatusCode.NoContent,
            (await Send(HttpMethod.Delete, $"/api/v1/roles/{roleId}")).StatusCode);
    }

    // -----------------------------------------------------------------------
    // Rename
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RenamingRole_RewritesTheNameOnEveryHolder()
    {
        var (roleId, user) = await SeedRoleWithHolderAsync("owner");

        var response = await Send(HttpMethod.Put, $"/api/v1/roles/{roleId}", new { name = "administrator" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Renaming used to leave the holder on the old name: entitled under a role the console no
        // longer showed, and recreating "owner" later silently re-granted it to them.
        var reloaded = (await _factory.UserStore.GetAsync(user.Id))!;
        Assert.Contains("administrator", reloaded.Roles);
        Assert.DoesNotContain("owner", reloaded.Roles);
    }

    [Fact]
    public async Task RenamingRole_RewritesTheNameInScopeGates()
    {
        var (roleId, _) = await SeedRoleWithHolderAsync("owner");
        await _factory.ScopeStore.CreateAsync(new Scope
        {
            Name = "billing:write",
            AllowedRoles = ["owner"],
        });

        await Send(HttpMethod.Put, $"/api/v1/roles/{roleId}", new { name = "administrator" });

        var scope = await _factory.ScopeStore.GetAsync("billing:write");
        Assert.NotNull(scope);
        Assert.Contains("administrator", scope.AllowedRoles);
        Assert.DoesNotContain("owner", scope.AllowedRoles);
    }

    [Fact]
    public async Task RenamingRole_ToAnExistingName_Is409()
    {
        var roleId = await CreateRoleAsync("owner");
        await CreateRoleAsync("administrator");

        var response = await Send(HttpMethod.Put, $"/api/v1/roles/{roleId}", new { name = "administrator" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UpdatingDescriptionOnly_DoesNotDisturbHolders()
    {
        var (roleId, user) = await SeedRoleWithHolderAsync("owner");

        var response = await Send(HttpMethod.Put, $"/api/v1/roles/{roleId}", new { description = "Account owner" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains("owner", (await _factory.UserStore.GetAsync(user.Id))!.Roles);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<string> CreateRoleAsync(string name)
    {
        var response = await Send(HttpMethod.Post, "/api/v1/roles", new { name });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetString()!;
    }

    private async Task<(string RoleId, AuthUser User)> SeedRoleWithHolderAsync(string roleName)
    {
        var roleId = await CreateRoleAsync(roleName);
        var user = await _factory.SeedTestUserAsync();

        var assign = await Send(HttpMethod.Post, "/api/v1/roles/assign",
            new { userId = user.Id, roleName });
        assign.EnsureSuccessStatusCode();

        return (roleId, user);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return _client.SendAsync(request);
    }
}

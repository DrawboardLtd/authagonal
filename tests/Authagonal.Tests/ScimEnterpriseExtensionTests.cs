using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// An attribute from a schema extension this provider does not implement must not fail the sync.
/// </summary>
/// <remarks>
/// This provider implements core SCIM only. Entra and Okta both map <c>department</c> and
/// <c>manager</c> through the enterprise user extension in their DEFAULT attribute mappings, so a
/// stock connector sends it on every update.
/// <para>
/// POST and PUT have always ignored the enterprise block, because nothing binds those JSON members.
/// PATCH refused it, and refused the request WHOLE: one unsupported attribute discarded every other
/// operation in the same payload. A connector therefore created users happily and then failed every
/// incremental sync with 400 <c>invalidPath</c>, and a payload carrying <c>department</c> alongside
/// <c>active: false</c> left the leaver ACTIVE.
/// </para>
/// </remarks>
public sealed class ScimEnterpriseExtensionTests : IAsyncDisposable
{
    private const string Enterprise = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";

    private readonly AuthagonalTestFactory _factory = new();

    private async Task<(HttpClient Client, string UserId)> ProvisionedAsync(string userName = "ada@acme.example")
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var created = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName,
            active = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        return (client, id);
    }

    private static StringContent Patch(params object[] operations) =>
        new(JsonSerializer.Serialize(new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = operations,
        }), Encoding.UTF8, "application/scim+json");

    /// <summary>
    /// The whole reason this matters. A stock Entra or Okta sync sends the department alongside the
    /// deactivation, and refusing the payload for the attribute we do not store left the account live.
    /// </summary>
    [Fact]
    public async Task DeactivationSurvivesAnEnterpriseAttributeInTheSamePatch()
    {
        var (client, userId) = await ProvisionedAsync();

        var response = await client.PatchAsync($"/scim/v2/Users/{userId}", Patch(
            new { op = "replace", path = $"{Enterprise}:department", value = "Engineering" },
            new { op = "replace", path = "active", value = false }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var user = await _factory.UserStore.GetAsync(userId);
        Assert.False(user!.IsActive, "the leaver must be deactivated even though the same payload carried an attribute we do not store");
    }

    [Fact]
    public async Task AnEnterpriseAttributeAloneIsAccepted()
    {
        var (client, userId) = await ProvisionedAsync();

        var response = await client.PatchAsync($"/scim/v2/Users/{userId}", Patch(
            new { op = "replace", path = $"{Enterprise}:department", value = "Engineering" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("employeeNumber")]
    [InlineData("costCenter")]
    [InlineData("division")]
    [InlineData("organization")]
    [InlineData("manager.value")]
    public async Task EveryEnterpriseAttributeIsTolerated(string attribute)
    {
        var (client, userId) = await ProvisionedAsync();

        var response = await client.PatchAsync($"/scim/v2/Users/{userId}", Patch(
            new { op = "replace", path = $"{Enterprise}:{attribute}", value = "x" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Ignoring is not storing. The extension is not implemented, and an attribute we drop must not
    /// reappear as though it had been kept.
    /// </summary>
    [Fact]
    public async Task AnEnterpriseAttributeIsNotStoredAnywhere()
    {
        var (client, userId) = await ProvisionedAsync();

        await client.PatchAsync($"/scim/v2/Users/{userId}", Patch(
            new { op = "replace", path = $"{Enterprise}:organization", value = "org_from_the_idp" }));

        var user = await _factory.UserStore.GetAsync(userId);
        Assert.True(string.IsNullOrEmpty(user!.OrganizationId),
            "the enterprise 'organization' attribute is IdP-asserted and must not become the org_id binding, which is operator-controlled");
        Assert.DoesNotContain(user.CustomAttributes, kv => kv.Value == "org_from_the_idp");
    }

    [Fact]
    public async Task RemovingAnEnterpriseAttributeIsAlsoTolerated()
    {
        var (client, userId) = await ProvisionedAsync();

        var response = await client.PatchAsync($"/scim/v2/Users/{userId}", Patch(
            new { op = "remove", path = $"{Enterprise}:department" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The relaxation is narrow on purpose. A misspelled CORE attribute is a mapping mistake the
    /// operator has to see, and reporting success for a write that did not happen is the failure this
    /// file's neighbours exist to prevent.
    /// </summary>
    [Fact]
    public async Task AMisspelledCoreAttributeIsStillRefused()
    {
        var (client, userId) = await ProvisionedAsync();

        var response = await client.PatchAsync($"/scim/v2/Users/{userId}", Patch(
            new { op = "replace", path = "name.givenNam", value = "Ada" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalidPath", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Read-only core attributes keep their own refusal, which is a different rule.</summary>
    [Fact]
    public async Task AReadOnlyCoreAttributeIsStillRefused()
    {
        var (client, userId) = await ProvisionedAsync();

        var response = await client.PatchAsync($"/scim/v2/Users/{userId}", Patch(
            new { op = "replace", path = "id", value = "hijacked" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("mutability", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The core prefix is stripped before the check, so a fully qualified CORE path still resolves to a
    /// real attribute rather than being mistaken for an unimplemented extension and dropped.
    /// </summary>
    [Fact]
    public async Task AFullyQualifiedCoreAttributeStillApplies()
    {
        var (client, userId) = await ProvisionedAsync();

        var response = await client.PatchAsync($"/scim/v2/Users/{userId}", Patch(
            new { op = "replace", path = "urn:ietf:params:scim:schemas:core:2.0:User:active", value = false }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await _factory.UserStore.GetAsync(userId);
        Assert.False(user!.IsActive);
    }

    /// <summary>
    /// A path that is the bare core schema URN with no attribute addresses the whole resource, which is
    /// what omitting the path means. It must APPLY, not be swallowed by the extension rule: the tolerance
    /// keys on a path still starting with "urn:", and this one does.
    /// </summary>
    [Fact]
    public async Task ABareCoreSchemaUrnPathAppliesTheWholeObject()
    {
        var (client, userId) = await ProvisionedAsync();

        var response = await client.PatchAsync($"/scim/v2/Users/{userId}", Patch(
            new
            {
                op = "replace",
                path = "urn:ietf:params:scim:schemas:core:2.0:User",
                value = new { active = false },
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await _factory.UserStore.GetAsync(userId);
        Assert.False(user!.IsActive, "a whole-resource patch must be applied, never silently dropped");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();
}

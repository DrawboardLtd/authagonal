using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Two SCIM credentials in one tenant must not be able to see or touch each other's resources.
/// </summary>
/// <remarks>
/// The isolation is enforced on every route — <c>ScimUserEndpoints.IsVisibleTo</c> (:47-50) on the user
/// surface, <c>ScimGroupEndpoints.OwnedByCaller</c> (:39-41) on the group surface, and
/// <c>ScimGroupEndpoints.RetainOwnedMembersAsync</c> (:331-352) on every membership write — but the DENY
/// branch of all three was covered by nothing. Every other SCIM test seeds the default <c>scim-client</c>
/// and drives one credential, so deleting the ownership comparison from any of those three left the whole
/// suite green while one connector could read, rename, deprovision and delete another's directory.
/// <para>
/// The membership half is the privilege path rather than a confidentiality one: group membership drives role
/// assignment through <c>IScimGroupRoleMappingStore</c>, so writing a foreign user id into a role-mapped
/// group grants that client's mapped roles to somebody else's user at the next token mint.
/// </para>
/// <para>
/// Every test here seeds TWO clients and issues the request as the one that does NOT own the resource. The
/// existing "nonexistent id returns 404" tests hit the null branch and prove nothing about ownership.
/// </para>
/// </remarks>
public sealed class ScimCrossClientIsolationTests : IAsyncDisposable
{
    private const string ClientA = "scim-client-a";
    private const string ClientB = "scim-client-b";

    private readonly AuthagonalTestFactory _factory = new();

    /// <summary>Two SCIM credentials in the same tenant: A owns the resources, B is the intruder.</summary>
    private async Task<(HttpClient A, HttpClient B)> TwoClientsAsync()
    {
        await _factory.SeedTestDataAsync();
        var (_, tokenA) = await _factory.SeedScimClientAsync(ClientA);
        var (_, tokenB) = await _factory.SeedScimClientAsync(ClientB);
        return (Authorized(tokenA), Authorized(tokenB));
    }

    private HttpClient Authorized(string rawToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        return client;
    }

    private static async Task<string> ProvisionUserAsync(HttpClient client, string userName, string? externalId = null)
    {
        var response = await client.PostAsJsonAsync("/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName,
            externalId,
            active = true,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateGroupAsync(HttpClient client, string displayName, params string[] memberIds)
    {
        var response = await client.PostAsJsonAsync("/scim/v2/Groups", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
            displayName,
            members = memberIds.Select(id => new { value = id }).ToArray(),
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static StringContent Patch(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/scim+json");

    private static StringContent AddMembersPatch(params string[] memberIds) =>
        Patch(new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[]
            {
                new
                {
                    op = "add",
                    path = "members",
                    value = (object)memberIds.Select(id => new { value = id }).ToArray(),
                },
            },
        });

    private static async Task<List<string>> MemberIdsAsync(HttpClient client, string groupId)
    {
        var response = await client.GetAsync($"/scim/v2/Groups/{groupId}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.TryGetProperty("members", out var members) && members.ValueKind is JsonValueKind.Array
            ? [.. members.EnumerateArray().Select(m => m.GetProperty("value").GetString()!)]
            : [];
    }

    private static async Task<List<string>> ResourceIdsAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. json.GetProperty("Resources").EnumerateArray().Select(r => r.GetProperty("id").GetString()!)];
    }

    // ── users: the ownership guard on every route ────────────────────────────

    /// <summary>Pins <c>ScimUserEndpoints.GetUserAsync</c> :204 — the <c>IsVisibleTo</c> deny branch.</summary>
    /// <remarks>
    /// Distinct from <c>GetUser_NotFound_Returns404</c>, which asks for an id that names nothing and so only
    /// exercises the null clause. Here the row exists and is readable; the only reason for the 404 is that
    /// the caller does not own it.
    /// </remarks>
    [Fact]
    public async Task ReadingAnotherClientsUser_Is404()
    {
        var (a, b) = await TwoClientsAsync();
        var userId = await ProvisionUserAsync(a, "owned-by-a@acme.example");

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/scim/v2/Users/{userId}")).StatusCode);

        // Same id, owning client: proves the 404 is about ownership and not about the id.
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/scim/v2/Users/{userId}")).StatusCode);
    }

    /// <summary>Pins <c>ScimUserEndpoints.ReplaceUserAsync</c> :499.</summary>
    [Fact]
    public async Task ReplacingAnotherClientsUser_Is404_AndChangesNothing()
    {
        var (a, b) = await TwoClientsAsync();
        var userId = await ProvisionUserAsync(a, "rename-me@acme.example");

        var replaced = await b.PutAsJsonAsync($"/scim/v2/Users/{userId}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "taken-over@intruder.example",
            active = true,
        });
        Assert.Equal(HttpStatusCode.NotFound, replaced.StatusCode);

        var read = await a.GetAsync($"/scim/v2/Users/{userId}");
        read.EnsureSuccessStatusCode();
        var json = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rename-me@acme.example", json.GetProperty("userName").GetString());
    }

    /// <summary>Pins <c>ScimUserEndpoints.PatchUserAsync</c> :608.</summary>
    [Fact]
    public async Task PatchingAnotherClientsUser_Is404_AndChangesNothing()
    {
        var (a, b) = await TwoClientsAsync();
        var userId = await ProvisionUserAsync(a, "patch-me@acme.example");

        // `active = false` is the operation that matters: a PATCH that lands revokes every grant the
        // subject holds, so this is deprovisioning somebody else's user.
        var patch = Patch(new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[] { new { op = "replace", path = "active", value = false } },
        });
        Assert.Equal(HttpStatusCode.NotFound, (await b.PatchAsync($"/scim/v2/Users/{userId}", patch)).StatusCode);

        var read = await a.GetAsync($"/scim/v2/Users/{userId}");
        read.EnsureSuccessStatusCode();
        Assert.True((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("active").GetBoolean());
    }

    /// <summary>Pins <c>ScimUserEndpoints.DeleteUserAsync</c> :720.</summary>
    /// <remarks>
    /// The read-back is the point. The delete is a soft delete — it tombstones the row and revokes every
    /// grant — so a 404 on its own would not distinguish "refused" from "deleted, then reported as gone".
    /// </remarks>
    [Fact]
    public async Task DeletingAnotherClientsUser_Is404_AndTheUserSurvives()
    {
        var (a, b) = await TwoClientsAsync();
        var userId = await ProvisionUserAsync(a, "delete-me@acme.example");

        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/scim/v2/Users/{userId}")).StatusCode);

        var read = await a.GetAsync($"/scim/v2/Users/{userId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var json = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("delete-me@acme.example", json.GetProperty("userName").GetString());
        Assert.True(json.GetProperty("active").GetBoolean());
    }

    /// <summary>
    /// Pins the scoping of the user listing at <c>ScimUserEndpoints.ListUsersAsync</c> :156 — the page is
    /// asked of the store FOR THIS CLIENT (<c>ListByScimClientPageAsync(clientId, ...)</c>), so a foreign
    /// user is never materialised at all.
    /// </summary>
    [Fact]
    public async Task ListingUsers_ShowsOnlyTheCallersOwn()
    {
        var (a, b) = await TwoClientsAsync();
        var aliceA = await ProvisionUserAsync(a, "alice@acme.example");
        var bobA = await ProvisionUserAsync(a, "bob@acme.example");
        var carolB = await ProvisionUserAsync(b, "carol@intruder.example");

        var seenByB = await ResourceIdsAsync(b, "/scim/v2/Users?count=100");
        Assert.Equal([carolB], seenByB);
        Assert.DoesNotContain(aliceA, seenByB);
        Assert.DoesNotContain(bobA, seenByB);

        // And A still sees its own two, so the listing is scoped rather than broken.
        var seenByA = await ResourceIdsAsync(a, "/scim/v2/Users?count=100");
        Assert.Equal(2, seenByA.Count);
        Assert.Contains(aliceA, seenByA);
        Assert.Contains(bobA, seenByA);
    }

    /// <summary>
    /// Pins <c>ScimUserEndpoints.ListUsersAsync</c> :123-139 — the indexed-equality fast path, which is the
    /// one place the listing leaves the client-scoped store call.
    /// </summary>
    /// <remarks>
    /// <c>userName eq "..."</c> is the query every IdP provisioning agent sends before a create, and it
    /// resolves through <c>FindByEmailAsync</c> (:126), a TENANT-WIDE blind-index lookup with no client in
    /// it. The only thing standing between that and one connector enumerating another's directory by address
    /// — a directory whose addresses are, by construction, guessable — is the <c>IsVisibleTo</c> filter at
    /// :129. It is a different guard from the one the paged listing relies on, and nothing covered it.
    /// </remarks>
    [Fact]
    public async Task FilteringByAnotherClientsUserName_ResolvesToNothing()
    {
        var (a, b) = await TwoClientsAsync();
        const string email = "findable@acme.example";
        var userId = await ProvisionUserAsync(a, email);

        var filter = Uri.EscapeDataString($"userName eq \"{email}\"");

        var asB = await b.GetAsync($"/scim/v2/Users?filter={filter}");
        Assert.Equal(HttpStatusCode.OK, asB.StatusCode);
        var json = await asB.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("totalResults").GetInt32());
        Assert.Empty(json.GetProperty("Resources").EnumerateArray());

        // The same filter as the owner resolves, so the empty answer is the guard and not a broken lookup.
        Assert.Equal([userId], await ResourceIdsAsync(a, $"/scim/v2/Users?filter={filter}"));
    }

    // ── groups: the ownership guard on every route ───────────────────────────

    /// <summary>Pins <c>ScimGroupEndpoints.GetGroupAsync</c> :302 — the <c>OwnedByCaller</c> deny branch.</summary>
    [Fact]
    public async Task ReadingAnotherClientsGroup_Is404()
    {
        var (a, b) = await TwoClientsAsync();
        var groupId = await CreateGroupAsync(a, "Engineering");

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/scim/v2/Groups/{groupId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/scim/v2/Groups/{groupId}")).StatusCode);
    }

    /// <summary>Pins <c>ScimGroupEndpoints.ReplaceGroupAsync</c> :477.</summary>
    [Fact]
    public async Task ReplacingAnotherClientsGroup_Is404_AndChangesNothing()
    {
        var (a, b) = await TwoClientsAsync();
        var groupId = await CreateGroupAsync(a, "Engineering");

        var replaced = await b.PutAsJsonAsync($"/scim/v2/Groups/{groupId}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
            displayName = "Owned By The Intruder",
        });
        Assert.Equal(HttpStatusCode.NotFound, replaced.StatusCode);

        var read = await a.GetAsync($"/scim/v2/Groups/{groupId}");
        read.EnsureSuccessStatusCode();
        Assert.Equal("Engineering", (await read.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("displayName").GetString());
    }

    /// <summary>Pins <c>ScimGroupEndpoints.PatchGroupAsync</c> :547.</summary>
    [Fact]
    public async Task PatchingAnotherClientsGroup_Is404_AndChangesNothing()
    {
        var (a, b) = await TwoClientsAsync();
        var groupId = await CreateGroupAsync(a, "Engineering");

        var patch = Patch(new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[] { new { op = "replace", path = "displayName", value = "Owned By The Intruder" } },
        });
        Assert.Equal(HttpStatusCode.NotFound, (await b.PatchAsync($"/scim/v2/Groups/{groupId}", patch)).StatusCode);

        var read = await a.GetAsync($"/scim/v2/Groups/{groupId}");
        read.EnsureSuccessStatusCode();
        Assert.Equal("Engineering", (await read.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("displayName").GetString());
    }

    /// <summary>Pins <c>ScimGroupEndpoints.DeleteGroupAsync</c> :643.</summary>
    /// <remarks>
    /// The group delete is a HARD delete, so the read-back as the owner is the only thing separating
    /// "refused" from "deleted and then reported as not found".
    /// </remarks>
    [Fact]
    public async Task DeletingAnotherClientsGroup_Is404_AndTheGroupSurvives()
    {
        var (a, b) = await TwoClientsAsync();
        var groupId = await CreateGroupAsync(a, "Engineering");

        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/scim/v2/Groups/{groupId}")).StatusCode);

        var read = await a.GetAsync($"/scim/v2/Groups/{groupId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("Engineering", (await read.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("displayName").GetString());
    }

    /// <summary>
    /// Pins the scoping of the group listing — <c>ScimGroupEndpoints.ListGroupsAsync</c> asks the store for
    /// the caller's groups on both the unfiltered (:207) and the filtered (:243) path.
    /// </summary>
    [Fact]
    public async Task ListingGroups_ShowsOnlyTheCallersOwn()
    {
        var (a, b) = await TwoClientsAsync();
        var groupA = await CreateGroupAsync(a, "Engineering");
        var groupB = await CreateGroupAsync(b, "Intruders");

        var unfiltered = await ResourceIdsAsync(b, "/scim/v2/Groups?count=100");
        Assert.Equal([groupB], unfiltered);
        Assert.DoesNotContain(groupA, unfiltered);

        // The filtered path scans a different way, so it gets its own assertion: naming A's group by
        // displayName must not surface it either.
        var filter = Uri.EscapeDataString("displayName eq \"Engineering\"");
        var filtered = await b.GetAsync($"/scim/v2/Groups?filter={filter}");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var json = await filtered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("totalResults").GetInt32());
        Assert.Empty(json.GetProperty("Resources").EnumerateArray());
    }

    // ── membership: the privilege-escalation guard ───────────────────────────

    /// <summary>
    /// Pins <c>ScimGroupEndpoints.RetainOwnedMembersAsync</c> (:331-352) on the PATCH path (called :611).
    /// </summary>
    /// <remarks>
    /// This is the escalation, not merely a confidentiality leak. B owns the group, so <c>OwnedByCaller</c>
    /// admits the request; the only thing refusing it is the per-member ownership check. Group membership
    /// drives role assignment through <c>IScimGroupRoleMappingStore</c>, so a foreign id accepted into a
    /// role-mapped group hands B's mapped roles to A's user at the next token mint — with nothing in A's
    /// directory recording that it happened.
    /// </remarks>
    [Fact]
    public async Task AddingAnotherClientsUserToYourOwnGroup_Is400_AndMembershipIsUnchanged()
    {
        var (a, b) = await TwoClientsAsync();
        var victim = await ProvisionUserAsync(a, "victim@acme.example");
        var ownMember = await ProvisionUserAsync(b, "insider@intruder.example");
        var groupId = await CreateGroupAsync(b, "Privileged", ownMember);

        var response = await b.PatchAsync($"/scim/v2/Groups/{groupId}", AddMembersPatch(victim));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(victim, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal([ownMember], await MemberIdsAsync(b, groupId));
    }

    /// <summary>
    /// Pins <c>RetainOwnedMembersAsync</c> on the CREATE path (called :438), which runs before
    /// <c>groupStore.CreateAsync</c> — so a refused create must leave no group behind.
    /// </summary>
    [Fact]
    public async Task CreatingAGroupNamingAnotherClientsUser_Is400_AndCreatesNothing()
    {
        var (a, b) = await TwoClientsAsync();
        var victim = await ProvisionUserAsync(a, "victim@acme.example");

        var response = await b.PostAsJsonAsync("/scim/v2/Groups", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
            displayName = "Privileged",
            members = new[] { new { value = victim } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(victim, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Empty(await ResourceIdsAsync(b, "/scim/v2/Groups?count=100"));
    }

    /// <summary>
    /// Pins <c>RetainOwnedMembersAsync</c> on the REPLACE path (called :511). PUT rewrites the whole
    /// membership array, so it is the shortest way to swap a foreign id in.
    /// </summary>
    [Fact]
    public async Task ReplacingYourGroupsMembershipWithAnotherClientsUser_Is400_AndMembershipIsUnchanged()
    {
        var (a, b) = await TwoClientsAsync();
        var victim = await ProvisionUserAsync(a, "victim@acme.example");
        var ownMember = await ProvisionUserAsync(b, "insider@intruder.example");
        var groupId = await CreateGroupAsync(b, "Privileged", ownMember);

        var response = await b.PutAsJsonAsync($"/scim/v2/Groups/{groupId}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
            displayName = "Privileged",
            members = new[] { new { value = victim } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(victim, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal([ownMember], await MemberIdsAsync(b, groupId));
    }

    /// <summary>
    /// Pins the null half of <c>RetainOwnedMembersAsync</c> :340 — an id naming no user at all is rejected
    /// on the same terms as a foreign one.
    /// </summary>
    /// <remarks>
    /// Worth its own test because the two halves fail differently: a foreign id fails the ownership
    /// comparison, an unknown id never reaches it. A guard that only checked existence would admit the
    /// foreign one, and a guard that only checked ownership would throw on the unknown one.
    /// </remarks>
    [Fact]
    public async Task AddingAMemberIdThatNamesNoUser_Is400()
    {
        var (_, b) = await TwoClientsAsync();
        var ownMember = await ProvisionUserAsync(b, "insider@intruder.example");
        var groupId = await CreateGroupAsync(b, "Privileged", ownMember);

        var ghost = Guid.NewGuid().ToString("N");
        var response = await b.PatchAsync($"/scim/v2/Groups/{groupId}", AddMembersPatch(ghost));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(ghost, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal([ownMember], await MemberIdsAsync(b, groupId));
    }

    // ── external ids ─────────────────────────────────────────────────────────

    /// <summary>
    /// Isolation cuts both ways: the externalId index is keyed on (clientId, externalId), so two connectors
    /// may each carry the same externalId for a different person without colliding.
    /// </summary>
    /// <remarks>
    /// The uniqueness checks on create (:370), replace (:548) and patch (:669) all pass the CALLER's client
    /// id to <c>FindByExternalIdAsync</c>. Were any of them tenant-wide, the second connector onboarded
    /// would start answering 409 on identities the first happens to number the same way — and Entra and Okta
    /// both number from 1. The resolution assertion is the other half: each client's filter must reach its
    /// OWN user, not merely a user.
    /// </remarks>
    [Fact]
    public async Task TwoClientsMayEachUseTheSameExternalId()
    {
        var (a, b) = await TwoClientsAsync();

        var userA = await ProvisionUserAsync(a, "one@acme.example", externalId: "ext-001");
        var userB = await ProvisionUserAsync(b, "one@intruder.example", externalId: "ext-001");
        Assert.NotEqual(userA, userB);

        var filter = Uri.EscapeDataString("externalId eq \"ext-001\"");
        Assert.Equal([userA], await ResourceIdsAsync(a, $"/scim/v2/Users?filter={filter}"));
        Assert.Equal([userB], await ResourceIdsAsync(b, $"/scim/v2/Users?filter={filter}"));
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

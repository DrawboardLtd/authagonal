using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// F56 / F299 / F309 / F280 — the resource lifecycle a provisioning connector drives, the discovery
/// URLs it is told to follow, and the two SCIM-supplied strings that become storage keys.
/// </summary>
public sealed class ScimLifecycleAndProjectionTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();
    private HttpClient _client = null!;
    private string _token = null!;
    private string _scimClientId = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        await _factory.SeedTestDataAsync();
        (_scimClientId, _token) = await _factory.SeedScimClientAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    // -----------------------------------------------------------------------
    // F56 — a deleted resource is gone
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeletedUser_IsNotReadable_NotListable_AndDoesNotBlockReCreation()
    {
        var created = await CreateUserAsync("leaver@example.com", "ext-leaver");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Delete, $"/scim/v2/Users/{id}")).StatusCode);

        // RFC 7644 §3.6: 404 for every operation on the deleted resource. Deactivation alone left it
        // returning 200 with the full record, PII included.
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, $"/scim/v2/Users/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Delete, $"/scim/v2/Users/{id}")).StatusCode);

        // …and omitted from query results, on both the scan path and the indexed one.
        var list = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/scim/v2/Users"));
        Assert.Empty(list.GetProperty("Resources").EnumerateArray());

        var byName = await ReadJsonAsync(await SendAsync(
            HttpMethod.Get, "/scim/v2/Users?filter=" + Uri.EscapeDataString("userName eq \"leaver@example.com\"")));
        Assert.Empty(byName.GetProperty("Resources").EnumerateArray());

        // The one that wedges provisioning: the tombstone still owns the email index entry, so a
        // re-hire answered 409 uniqueness forever, with no recovery path the connector could see.
        var recreated = await CreateUserAsync("leaver@example.com", "ext-leaver");
        Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);

        var recreatedId = (await ReadJsonAsync(recreated)).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/scim/v2/Users/{recreatedId}")).StatusCode);
    }

    /// <summary>
    /// A re-provisioned address is a NEW resource: new <c>id</c>, hence new OIDC <c>sub</c>.
    /// </summary>
    /// <remarks>
    /// The reclaim kept the tombstoned row's id, so the resource created for whoever next held the address was
    /// issued the departed employee's identifier. RFC 7643 §3.1 requires <c>id</c> to be "a stable,
    /// non-reassignable identifier", and here it is the OIDC subject — so at every relying party the new person
    /// WAS the old one, inheriting their documents, permissions and audit identity, with nothing in the IdP
    /// recording that the human had changed.
    /// <para>
    /// Nothing asserted the reuse, which is why it survived: the existing re-creation test checks only that the
    /// create succeeds.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ReProvisioningAnAddress_IssuesANewSubject_NotTheDepartedUsers()
    {
        var first = await CreateUserAsync("rehire@example.com", "ext-rehire");
        var firstId = (await ReadJsonAsync(first)).GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Delete, $"/scim/v2/Users/{firstId}")).StatusCode);

        var second = await CreateUserAsync("rehire@example.com", "ext-rehire");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondId = (await ReadJsonAsync(second)).GetProperty("id").GetString()!;

        Assert.NotEqual(firstId, secondId);

        // The new resource is readable at its own id, and the old identifier is gone for good.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/scim/v2/Users/{secondId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/scim/v2/Users/{firstId}")).StatusCode);
    }

    /// <summary>
    /// A deleted account's second factors do not survive to authenticate the next account.
    /// </summary>
    /// <remarks>
    /// <c>IMfaStore.DeleteAllCredentialsAsync</c> had exactly one caller in the product — the admin MFA-reset
    /// endpoint — so neither delete path removed a thing. Credentials are keyed on the user id, and the
    /// passwordless sign-in path resolves the account from the credential without ever consulting
    /// <c>MfaEnabled</c>: an attacker who had enrolled a passkey therefore kept a way in across the exact
    /// remedy an incident responder reaches for, needing no password, no email and no session.
    /// </remarks>
    [Fact]
    public async Task DeletingAUser_RemovesTheirMfaCredentials()
    {
        var id = (await ReadJsonAsync(await CreateUserAsync("mfa-leaver@example.com")))
            .GetProperty("id").GetString()!;

        await _factory.MfaStore.CreateCredentialAsync(new Authagonal.Core.Models.MfaCredential
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = id,
            Type = Authagonal.Core.Models.MfaCredentialType.Totp,
            SecretProtected = "seed",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        Assert.NotEmpty(await _factory.MfaStore.GetCredentialsAsync(id));

        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Delete, $"/scim/v2/Users/{id}")).StatusCode);

        Assert.Empty(await _factory.MfaStore.GetCredentialsAsync(id));
    }

    [Fact]
    public async Task DeactivationIsStillReversible()
    {
        // The tombstone must not swallow ordinary deactivation: `active: false` is how an IdP
        // suspends someone, and it has to stay undoable.
        var id = (await ReadJsonAsync(await CreateUserAsync("suspended@example.com")))
            .GetProperty("id").GetString()!;

        await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[] { new { op = "replace", path = "active", value = false } },
        });

        var suspended = await ReadJsonAsync(await SendAsync(HttpMethod.Get, $"/scim/v2/Users/{id}"));
        Assert.False(suspended.GetProperty("active").GetBoolean());

        await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[] { new { op = "replace", path = "active", value = true } },
        });

        var restored = await ReadJsonAsync(await SendAsync(HttpMethod.Get, $"/scim/v2/Users/{id}"));
        Assert.True(restored.GetProperty("active").GetBoolean());
    }

    // -----------------------------------------------------------------------
    // F60 — a PUT that says nothing about `active` must not change it
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Put_WithActiveOmitted_DoesNotReactivateADeprovisionedUser()
    {
        var id = (await ReadJsonAsync(await CreateUserAsync("deprovisioned@example.com")))
            .GetProperty("id").GetString()!;

        await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[] { new { op = "replace", path = "active", value = false } },
        });

        // A profile-only PUT — exactly what an IdP sends when a name changes. `active` binds to a
        // non-nullable bool defaulting to true, so an omitted value read as an explicit `true` and
        // silently brought a deprovisioned account back.
        var response = await SendAsync(HttpMethod.Put, $"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName = "deprovisioned@example.com",
            name = new { givenName = "Renamed", familyName = "User" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ReadJsonAsync(response)).GetProperty("active").GetBoolean());
    }

    // -----------------------------------------------------------------------
    // F147 — a PATCH that could not be applied is not reported as applied
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Patch_WithAnUnsupportedOperation_IsRefusedNotSilentlyDropped()
    {
        var id = (await ReadJsonAsync(await CreateUserAsync("patchreport@example.com")))
            .GetProperty("id").GetString()!;

        // patch.supported = true is advertised, so answering 200 to an operation that was dropped
        // left the directory believing a write had landed. For a deprovisioning PATCH that means the
        // IdP records the user as disabled while they still have every session they had before.
        var response = await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[] { new { op = "replace", path = "nonSuchAttribute", value = "x" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalidPath", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // F309 — attributes / excludedAttributes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Attributes_NarrowsTheResource()
    {
        var id = (await ReadJsonAsync(await CreateUserAsync("projected@example.com")))
            .GetProperty("id").GetString()!;

        var resource = await ReadJsonAsync(
            await SendAsync(HttpMethod.Get, $"/scim/v2/Users/{id}?attributes=userName"));

        Assert.True(resource.TryGetProperty("userName", out _));

        // Everything not asked for is gone — this is the whole point of the parameter, and a
        // connector uses it precisely to avoid pulling PII it has no use for.
        Assert.False(resource.TryGetProperty("name", out _));
        Assert.False(resource.TryGetProperty("emails", out _));
        Assert.False(resource.TryGetProperty("displayName", out _));

        // RFC 7643 §7 gives id and schemas returned="always"; meta rides with them because without
        // it the client cannot address what it just received.
        Assert.True(resource.TryGetProperty("id", out _));
        Assert.True(resource.TryGetProperty("schemas", out _));
        Assert.True(resource.TryGetProperty("meta", out _));
    }

    [Fact]
    public async Task ExcludedAttributes_RemovesOnlyWhatWasNamed()
    {
        var id = (await ReadJsonAsync(await CreateUserAsync("excluded@example.com")))
            .GetProperty("id").GetString()!;

        var resource = await ReadJsonAsync(
            await SendAsync(HttpMethod.Get, $"/scim/v2/Users/{id}?excludedAttributes=emails,name.formatted"));

        Assert.False(resource.TryGetProperty("emails", out _));

        // Naming a sub-attribute excludes that sub-attribute, not the complex attribute holding it.
        var name = resource.GetProperty("name");
        Assert.False(name.TryGetProperty("formatted", out _));
        Assert.True(name.TryGetProperty("givenName", out _));
    }

    [Fact]
    public async Task Attributes_AppliesToListings()
    {
        await CreateUserAsync("listed@example.com");

        var list = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/scim/v2/Users?attributes=userName"));
        var resource = list.GetProperty("Resources").EnumerateArray().Single();

        Assert.True(resource.TryGetProperty("userName", out _));
        Assert.False(resource.TryGetProperty("emails", out _));
    }

    [Fact]
    public async Task BothProjectionParameters_IsRefused()
    {
        // RFC 7644 §3.9 makes them mutually exclusive. Quietly honouring one answers a question the
        // caller did not ask — the same failure the parameter exists to prevent.
        var response = await SendAsync(
            HttpMethod.Get, "/scim/v2/Users?attributes=userName&excludedAttributes=emails");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // F299 — the discovery URLs a client is told to follow
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("/scim/v2/Schemas")]
    [InlineData("/scim/v2/ResourceTypes")]
    public async Task DiscoveryMetaLocations_Resolve(string collection)
    {
        var list = await ReadJsonAsync(await SendAsync(HttpMethod.Get, collection));

        foreach (var resource in list.GetProperty("Resources").EnumerateArray())
        {
            var location = resource.GetProperty("meta").GetProperty("location").GetString()!;
            var path = new Uri(location).AbsolutePath;

            var response = await SendAsync(HttpMethod.Get, path);

            // These fell through the API routes to the SPA fallback and answered 200 text/html — the
            // login page, handed to a client parsing it as a SCIM resource.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/scim+json", response.Content.Headers.ContentType?.MediaType);

            var single = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(resource.GetProperty("id").GetString(), single.GetProperty("id").GetString());
        }
    }

    [Fact]
    public async Task UnknownDiscoveryResource_Is404_NotTheSpa()
    {
        var response = await SendAsync(HttpMethod.Get, "/scim/v2/ResourceTypes/NoSuchThing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // F280 — SCIM strings that become storage keys
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("a#b@example.com")]
    [InlineData("a/b@example.com")]
    [InlineData("a\\b@example.com")]
    [InlineData("a?b@example.com")]
    public async Task UserNameWithKeyHostileCharacters_IsRefused(string userName)
    {
        // With the default non-tokenizing configuration the normalized email IS the email index's
        // partition key, and the profile row is written first — so a key the storage service rejects
        // failed AFTER the user was durably created, leaving a record no lookup could reach and no
        // duplicate check could see.
        var response = await CreateUserAsync(userName);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await _factory.UserStore.FindByEmailAsync(userName));
    }

    [Fact]
    public async Task ExternalIdWithKeyHostileCharacters_IsRefused()
    {
        // externalId is the other component of a composite index key.
        var response = await CreateUserAsync("ext-check@example.com", "tenant/../other");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OverlongExternalId_IsRefused()
    {
        var response = await CreateUserAsync("long-ext@example.com", new string('x', 300));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchCannotIntroduceAKeyHostileUserName()
    {
        // The applier writes straight onto the model, so PATCH was the way around the create-path
        // guard entirely.
        var id = (await ReadJsonAsync(await CreateUserAsync("patchkey@example.com")))
            .GetProperty("id").GetString()!;

        var response = await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{id}", new
        {
            schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            Operations = new[] { new { op = "replace", path = "userName", value = "bad#key@example.com" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Only the response is asserted. The in-memory store hands out the live entity, so the
        // applier's in-place mutation is visible through it even though UpdateAsync never ran and
        // nothing was persisted — that is an artefact of the test double (every other rejection path
        // in PatchUserAsync shares it), not of this guard.
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private Task<HttpResponseMessage> CreateUserAsync(string userName, string? externalId = null) =>
        SendAsync(HttpMethod.Post, "/scim/v2/Users", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            userName,
            externalId,
            name = new { givenName = "Test", familyName = "User" },
            emails = new[] { new { value = userName, primary = true } },
            active = true,
        });

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/scim+json");
        }
        return _client.SendAsync(request);
    }
}

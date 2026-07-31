using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Services;

namespace Authagonal.Tests;

// -------------------------------------------------------------------------------------------------
// F334 — an authorization failure is not a login challenge.
//
// These branches were previously recorded as untestable: the shipped IClientScopeGuard is the
// single-role AllowAllClientScopeGuard, so no request reaches a denial in-harness. That gap was not
// harmless. The create path was fixed to answer a JSON 403; the update path kept Results.Forbid()
// and nothing noticed, because nothing could reach either branch.
//
// The guard is an extension seam by design — AllowAll is the documented default and hosts with a
// real role hierarchy register their own — so substituting a refusing guard is using the seam, not
// stubbing past it. What is asserted below is the ENDPOINT's rendering of a denial, which is
// production code.
//
// Assertions are on the response BODY, not only the status. Results.Forbid() delegates to the
// authentication scheme's forbid handler: on the cookie scheme that is a 302 to /login (the original
// defect), but on this harness's bearer scheme it is an empty 403 — indistinguishable from the fix
// by status alone. The body is what actually carries "you may not grant that scope" to an API
// caller, so the body is what is pinned.
// -------------------------------------------------------------------------------------------------

/// <summary>
/// Refuses exactly one scope, standing in for a host with a real role hierarchy.
/// </summary>
internal sealed class DenyScopeGuard : IClientScopeGuard
{
    /// <summary>A scope this guard never grants. Deliberately not the reserved admin scope, which is
    /// refused by a separate check further down both handlers.</summary>
    public const string DeniedScope = "tenant:admin";

    public string? FindUngrantableScope(ClaimsPrincipal user, IEnumerable<string>? requestedScopes)
        => requestedScopes?.FirstOrDefault(s => s == DeniedScope);
}

public sealed class ClientScopeGuardDenialTests : IAsyncLifetime
{
    private readonly AdminSurfaceHost _host = new() { ScopeGuard = new DenyScopeGuard() };
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _host.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static StringContent Json(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [Fact]
    public async Task CreateClient_UngrantableScope_Returns403NamingTheScope()
    {
        var response = await _client.PostAsync("/api/v1/clients/", Json(new
        {
            clientId = "escalator",
            clientName = "Escalator",
            allowedScopes = new[] { "openid", DenyScopeGuard.DeniedScope },
        }));

        await AssertDeniedAsJson(response);
        Assert.Null(await _host.ClientStore.GetAsync("escalator"));
    }

    [Fact]
    public async Task UpdateClient_UngrantableScope_Returns403NamingTheScope()
    {
        await _host.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = "climber",
            ClientName = "Climber",
            AllowedScopes = ["openid"],
        });

        var response = await _client.PutAsync("/api/v1/clients/climber", Json(new
        {
            clientId = "climber",
            clientName = "Climber",
            allowedScopes = new[] { "openid", DenyScopeGuard.DeniedScope },
        }));

        await AssertDeniedAsJson(response);
        // The refusal must also not have partially applied.
        Assert.Equal(["openid"], (await _host.ClientStore.GetAsync("climber"))!.AllowedScopes);
    }

    /// <summary>
    /// The update guard is deliberately scoped to newly added scopes, so a client that already holds
    /// an ungrantable scope stays editable. Without this, a guard change would silently freeze every
    /// client already carrying the scope.
    /// </summary>
    [Fact]
    public async Task UpdateClient_PreexistingUngrantableScope_IsNotRefused()
    {
        await _host.ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = "grandfathered",
            ClientName = "Grandfathered",
            AllowedScopes = ["openid", DenyScopeGuard.DeniedScope],
        });

        var response = await _client.PutAsync("/api/v1/clients/grandfathered", Json(new
        {
            clientId = "grandfathered",
            clientName = "Renamed",
            allowedScopes = new[] { "openid", DenyScopeGuard.DeniedScope },
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await _host.ClientStore.GetAsync("grandfathered"))!.ClientName);
    }

    /// <summary>
    /// Pins the whole contract of the fix: a JSON 403 that names the offending scope, and in
    /// particular NOT a redirect to a login page.
    /// </summary>
    private static async Task AssertDeniedAsJson(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden_scope", json.GetProperty("error").GetString());
        Assert.Contains(DenyScopeGuard.DeniedScope, json.GetProperty("error_description").GetString());
    }
}

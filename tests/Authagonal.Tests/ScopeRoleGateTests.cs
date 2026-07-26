using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Authagonal.Core.Constants;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// Per-user scope entitlement (<see cref="Scope.AllowedRoles"/>): a scope may declare the roles
/// entitled to it, and a user without one of them has it dropped from the grant rather than being
/// refused outright.
/// </summary>
/// <remarks>
/// The two enforcement points are covered separately because they answer different questions.
/// <c>/connect/authorize</c> decides what a NEW grant contains, and runs before consent so the user
/// is never offered a permission that cannot be granted. Refresh re-decides on every rotation, which
/// is the only place revoking a role can actually take effect — the grant still records what was
/// approved at login.
/// </remarks>
public sealed class ScopeRoleGateTests
{
    private const string StaffScope = "staff-admin";
    private const string StaffRole = "staff";
    private const string RedirectUri = "https://app.test/callback";

    // -----------------------------------------------------------------------
    // /connect/authorize
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Authorize_UngatedScope_IsUntouched()
    {
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();

        // Registered with no AllowedRoles — the default, and the state every existing scope is in.
        await factory.ScopeStore.CreateAsync(new Scope { Name = StaffScope, DisplayName = "Staff" });
        await AllowScopeOnTestClientAsync(factory, StaffScope);

        await SeedAndLoginAsync(factory, client, roles: []);

        var granted = await CompleteFlowAsync(client, $"openid {StaffScope}");

        Assert.Contains(StaffScope, granted);
    }

    [Fact]
    public async Task Authorize_GatedScope_EntitledUser_KeepsIt()
    {
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();
        await factory.ScopeStore.CreateAsync(new Scope { Name = StaffScope, AllowedRoles = [StaffRole] });
        await AllowScopeOnTestClientAsync(factory, StaffScope);

        await SeedAndLoginAsync(factory, client, roles: [StaffRole]);

        var granted = await CompleteFlowAsync(client, $"openid {StaffScope}");

        Assert.Contains(StaffScope, granted);
        Assert.Contains("openid", granted);
    }

    [Fact]
    public async Task Authorize_GatedScope_UnentitledUser_DropsItButKeepsTheRest()
    {
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();
        await factory.ScopeStore.CreateAsync(new Scope { Name = StaffScope, AllowedRoles = [StaffRole] });
        await AllowScopeOnTestClientAsync(factory, StaffScope);

        await SeedAndLoginAsync(factory, client, roles: ["some-other-role"]);

        var granted = await CompleteFlowAsync(client, $"openid profile {StaffScope}");

        // The point of dropping rather than refusing: the client asked for its full set and is still
        // usable, it is just told it got less.
        Assert.DoesNotContain(StaffScope, granted);
        Assert.Contains("openid", granted);
        Assert.Contains("profile", granted);
    }

    [Fact]
    public async Task Authorize_EveryRequestedScopeGated_UnentitledUser_FailsWithAccessDenied()
    {
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();
        await factory.ScopeStore.CreateAsync(new Scope { Name = StaffScope, AllowedRoles = [StaffRole] });
        await AllowScopeOnTestClientAsync(factory, StaffScope);

        await SeedAndLoginAsync(factory, client, roles: []);

        var response = await client.GetAsync(BuildAuthorizeUrl(StaffScope, GeneratePkce().Challenge));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        Assert.Equal("access_denied", query["error"]);
        Assert.Contains("not entitled", query["error_description"]);
    }

    // -----------------------------------------------------------------------
    // Device flow — the approval step is its equivalent of the authorize gate
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeviceApproval_UnentitledUser_DropsTheGatedScopeFromTheIssuedToken()
    {
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();
        await factory.ScopeStore.CreateAsync(new Scope { Name = StaffScope, AllowedRoles = [StaffRole] });
        await ConfigureDeviceClientAsync(factory);

        await SeedAndLoginAsync(factory, client, roles: []);

        var granted = await CompleteDeviceFlowAsync(client, $"openid {StaffScope}");

        Assert.DoesNotContain(StaffScope, granted);
        Assert.Contains("openid", granted);
    }

    [Fact]
    public async Task DeviceApproval_EveryRequestedScopeGated_UnentitledUser_IsRefused()
    {
        await using var factory = new AuthagonalTestFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await factory.SeedTestDataAsync();
        await factory.ScopeStore.CreateAsync(new Scope { Name = StaffScope, AllowedRoles = [StaffRole] });
        await ConfigureDeviceClientAsync(factory);

        await SeedAndLoginAsync(factory, client, roles: []);

        var codes = await RequestDeviceCodesAsync(client, StaffScope);
        var approveResponse = await client.PostAsync("/api/auth/device/approve", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["user_code"] = codes.UserCode }));

        Assert.Equal(HttpStatusCode.Forbidden, approveResponse.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Refresh — where revoking a role takes effect
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Refresh_RoleRevokedAfterLogin_DropsTheGatedScope()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();
        await factory.ScopeStore.CreateAsync(new Scope { Name = StaffScope, AllowedRoles = [StaffRole] });
        await AllowScopeOnTestClientAsync(factory, StaffScope);

        var user = await factory.SeedTestUserAsync();
        user.Roles = [StaffRole];
        await factory.UserStore.UpdateAsync(user);

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();
        var oauthClient = (await factory.Services.GetRequiredService<IClientStore>()
            .GetAsync(AuthagonalTestFactory.TestClientId))!;

        var handle = await tokens.CreateRefreshTokenAsync(
            await resolver.BuildSubjectAsync(user, oauthClient),
            oauthClient,
            ["openid", "offline_access", StaffScope]);

        // Still entitled — the scope survives the first rotation.
        var first = await tokens.HandleRefreshTokenAsync(handle, AuthagonalTestFactory.TestClientId);
        Assert.Contains(StaffScope, first.Scope!.Split(' '));

        // Role revoked. The grant still records the scope; the gate is what stops it being re-minted.
        user.Roles = [];
        await factory.UserStore.UpdateAsync(user);

        var second = await tokens.HandleRefreshTokenAsync(first.RefreshToken!, AuthagonalTestFactory.TestClientId);

        Assert.DoesNotContain(StaffScope, second.Scope!.Split(' '));
        Assert.Contains("openid", second.Scope!.Split(' '));
    }

    [Fact]
    public async Task Refresh_LastEntitledScopeRevoked_EndsTheChain()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();
        await factory.ScopeStore.CreateAsync(new Scope { Name = StaffScope, AllowedRoles = [StaffRole] });
        await AllowScopeOnTestClientAsync(factory, StaffScope);

        var user = await factory.SeedTestUserAsync();
        user.Roles = [StaffRole];
        await factory.UserStore.UpdateAsync(user);

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();
        var oauthClient = (await factory.Services.GetRequiredService<IClientStore>()
            .GetAsync(AuthagonalTestFactory.TestClientId))!;

        var handle = await tokens.CreateRefreshTokenAsync(
            await resolver.BuildSubjectAsync(user, oauthClient),
            oauthClient,
            [StaffScope]);

        user.Roles = [];
        await factory.UserStore.UpdateAsync(user);

        // Nothing left to issue a token for, so the refresh fails rather than handing back an
        // empty-scoped token the client would treat as success.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tokens.HandleRefreshTokenAsync(handle, AuthagonalTestFactory.TestClientId));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>The seeded test client only allows the four standard scopes; widen it so the gate,
    /// rather than the client registration, is what the test is measuring.</summary>
    private static async Task AllowScopeOnTestClientAsync(AuthagonalTestFactory factory, string scope)
    {
        var client = (await factory.ClientStore.GetAsync(AuthagonalTestFactory.TestClientId))!;
        client.AllowedScopes.Add(scope);
        await factory.ClientStore.UpsertAsync(client);
    }

    private static async Task SeedAndLoginAsync(AuthagonalTestFactory factory, HttpClient client, string[] roles)
    {
        var user = await factory.SeedTestUserAsync();
        if (roles.Length > 0)
        {
            user.Roles = [.. roles];
            await factory.UserStore.UpdateAsync(user);
        }
        await client.PostAsJsonAsync("/api/auth/login", new { email = "test@example.com", password = "Test1234!" });
    }

    /// <summary>Runs authorize → token and returns the scopes the token response actually granted.
    /// RFC 6749 §3.3 — the echoed <c>scope</c> is how a client learns it got less than it asked for,
    /// so it is the right thing to assert on.</summary>
    private static async Task<string[]> CompleteFlowAsync(HttpClient client, string requestedScope)
    {
        var (verifier, challenge) = GeneratePkce();

        var authorizeResponse = await client.GetAsync(BuildAuthorizeUrl(requestedScope, challenge));
        Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);
        var code = HttpUtility.ParseQueryString(authorizeResponse.Headers.Location!.Query)["code"];
        Assert.NotNull(code);

        var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = verifier,
                ["client_id"] = AuthagonalTestFactory.TestClientId,
            }));

        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var json = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("scope").GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>The device flow needs a confidential client; the seeded admin client is the one that
    /// already has a secret, so it gets the device grant and the gated scope.</summary>
    private static async Task ConfigureDeviceClientAsync(AuthagonalTestFactory factory)
    {
        var client = (await factory.ClientStore.GetAsync(AuthagonalTestFactory.AdminClientId))!;
        client.AllowedGrantTypes = ["client_credentials", GrantTypes.DeviceCode];
        client.AllowedScopes = ["openid", "profile", StaffScope];
        await factory.ClientStore.UpsertAsync(client);
    }

    private static async Task<(string DeviceCode, string UserCode)> RequestDeviceCodesAsync(
        HttpClient client, string scope)
    {
        var response = await client.PostAsync("/connect/deviceauthorization", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = AuthagonalTestFactory.AdminClientId,
                ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
                ["scope"] = scope,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("device_code").GetString()!, json.GetProperty("user_code").GetString()!);
    }

    /// <summary>Runs device authorization → approve → poll and returns the granted scopes.</summary>
    private static async Task<string[]> CompleteDeviceFlowAsync(HttpClient client, string requestedScope)
    {
        var codes = await RequestDeviceCodesAsync(client, requestedScope);

        var approveResponse = await client.PostAsync("/api/auth/device/approve", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["user_code"] = codes.UserCode }));
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);

        var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.DeviceCode,
                ["device_code"] = codes.DeviceCode,
                ["client_id"] = AuthagonalTestFactory.AdminClientId,
                ["client_secret"] = AuthagonalTestFactory.AdminClientSecret,
            }));

        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var json = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("scope").GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string BuildAuthorizeUrl(string scope, string codeChallenge) =>
        $"/connect/authorize?client_id={AuthagonalTestFactory.TestClientId}" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        $"&response_type=code" +
        $"&scope={Uri.EscapeDataString(scope)}" +
        $"&state=test-state-123" +
        $"&code_challenge={codeChallenge}" +
        $"&code_challenge_method=S256";

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }
}

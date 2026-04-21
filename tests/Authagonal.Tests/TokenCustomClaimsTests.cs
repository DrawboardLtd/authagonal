using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Authagonal.Protocol.Services;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Authagonal.Tests;

/// <summary>
/// Custom claims released on access / ID tokens must be gated by the UserClaims whitelist of
/// requested scopes, and requested scopes must be rejected when not allowed on the client.
/// These tests drive <see cref="IProtocolTokenService"/> directly and the
/// /connect/token and /connect/deviceauthorization endpoints end-to-end.
/// </summary>
public sealed class TokenCustomClaimsTests
{
    private const string ClientId = AuthagonalTestFactory.TestClientId;

    [Fact]
    public async Task AccessToken_EmitsUserCustomAttributes_OnlyForScopesListingThem()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        // Client may request an app scope that releases org_id + org_role.
        var client = (await factory.Services.GetRequiredService<IClientStore>().GetAsync(ClientId))!;
        client.AllowedScopes = [.. client.AllowedScopes, "projects-api.read"];
        await factory.ClientStore.UpsertAsync(client);

        await factory.ScopeStore.CreateAsync(new Scope
        {
            Name = "projects-api.read",
            UserClaims = ["org_id", "org_role"],
        });

        var user = await factory.SeedTestUserAsync();
        user.CustomAttributes["org_id"] = "org-123";
        user.CustomAttributes["org_role"] = "admin";
        user.CustomAttributes["secret_internal"] = "should-not-leak";
        await factory.UserStore.UpdateAsync(user);

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();
        var subject = await resolver.BuildSubjectAsync(user, client);

        // With the app scope: listed claims appear.
        var withScope = await tokens.CreateAccessTokenAsync(subject, client, ["openid", "projects-api.read"]);
        var claimsWith = ReadClaims(withScope);
        Assert.Equal("org-123", claimsWith["org_id"]);
        Assert.Equal("admin", claimsWith["org_role"]);
        Assert.False(claimsWith.ContainsKey("secret_internal"));

        // Without the app scope: no custom claims leak through.
        var withoutScope = await tokens.CreateAccessTokenAsync(subject, client, ["openid"]);
        var claimsWithout = ReadClaims(withoutScope);
        Assert.False(claimsWithout.ContainsKey("org_id"));
        Assert.False(claimsWithout.ContainsKey("org_role"));
    }

    [Fact]
    public async Task AccessToken_AdditionalClaims_EmittedUngatedByScope()
    {
        // Protocol's subject-based flow routes forced additional claims via
        // OidcSubject.AdditionalClaims — by design these bypass the scope whitelist so
        // bounded-scope tokens (e.g. share-link tokens) can carry their own claim
        // regardless of the requested scopes. Reserved names are still filtered.
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        var client = (await factory.Services.GetRequiredService<IClientStore>().GetAsync(ClientId))!;
        client.AllowedScopes = [.. client.AllowedScopes, "projects-api.read"];
        await factory.ClientStore.UpsertAsync(client);

        await factory.ScopeStore.CreateAsync(new Scope
        {
            Name = "projects-api.read",
            UserClaims = ["LinkShareToken", "org_id"],
        });

        var user = await factory.SeedTestUserAsync();
        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();

        var baseSubject = await resolver.BuildSubjectAsync(user, client);
        var subject = new OidcSubject
        {
            SubjectId = baseSubject.SubjectId,
            Email = baseSubject.Email,
            EmailVerified = baseSubject.EmailVerified,
            GivenName = baseSubject.GivenName,
            FamilyName = baseSubject.FamilyName,
            Phone = baseSubject.Phone,
            OrganizationId = baseSubject.OrganizationId,
            Roles = baseSubject.Roles,
            Groups = baseSubject.Groups,
            CustomAttributes = baseSubject.CustomAttributes,
            SessionMaxExpiresAt = baseSubject.SessionMaxExpiresAt,
            AdditionalClaims = new Dictionary<string, string>
            {
                ["LinkShareToken"] = "tok-abc",
                ["org_id"] = "org-xyz",
                ["unlisted_claim"] = "carried",
            },
        };

        var jwt = await tokens.CreateAccessTokenAsync(subject, client, ["projects-api.read"]);
        var claims = ReadClaims(jwt);

        Assert.Equal("tok-abc", claims["LinkShareToken"]);
        Assert.Equal("org-xyz", claims["org_id"]);
        Assert.Equal("carried", claims["unlisted_claim"]);
    }

    [Fact]
    public async Task AccessToken_AdditionalClaims_CannotShadowReservedProtocolClaims()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        var client = (await factory.Services.GetRequiredService<IClientStore>().GetAsync(ClientId))!;
        client.AllowedScopes = [.. client.AllowedScopes, "projects-api.read"];
        await factory.ClientStore.UpsertAsync(client);

        // Even if a tenant (accidentally or maliciously) lists protocol claims in a scope's
        // UserClaims, the reserved-names guard keeps the token honest.
        await factory.ScopeStore.CreateAsync(new Scope
        {
            Name = "projects-api.read",
            UserClaims = ["sub", "iss", "scope", "client_id", "roles"],
        });

        var user = await factory.SeedTestUserAsync();
        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();
        var baseSubject = await resolver.BuildSubjectAsync(user, client);
        var subject = new OidcSubject
        {
            SubjectId = baseSubject.SubjectId,
            Email = baseSubject.Email,
            EmailVerified = baseSubject.EmailVerified,
            GivenName = baseSubject.GivenName,
            FamilyName = baseSubject.FamilyName,
            Phone = baseSubject.Phone,
            OrganizationId = baseSubject.OrganizationId,
            Roles = baseSubject.Roles,
            Groups = baseSubject.Groups,
            CustomAttributes = baseSubject.CustomAttributes,
            SessionMaxExpiresAt = baseSubject.SessionMaxExpiresAt,
            AdditionalClaims = new Dictionary<string, string>
            {
                ["sub"] = "hijack",
                ["iss"] = "https://evil.example",
                ["scope"] = "admin",
                ["client_id"] = "other-client",
                ["roles"] = "super-admin",
            },
        };

        var jwt = await tokens.CreateAccessTokenAsync(subject, client, ["projects-api.read"]);
        var raw = new JsonWebTokenHandler().ReadJsonWebToken(jwt);

        Assert.Equal(user.Id, raw.Subject);
        Assert.Equal(AuthagonalTestFactory.TestIssuer, raw.Issuer);
        Assert.Equal(ClientId, raw.GetClaim("client_id").Value);
        Assert.Equal("projects-api.read", raw.GetClaim("scope").Value);
    }

    [Fact]
    public async Task IdToken_EmitsUserCustomAttributes_ForRequestedScope()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        var client = (await factory.Services.GetRequiredService<IClientStore>().GetAsync(ClientId))!;
        client.AllowedScopes = [.. client.AllowedScopes, "profile.extras"];
        await factory.ClientStore.UpsertAsync(client);

        await factory.ScopeStore.CreateAsync(new Scope
        {
            Name = "profile.extras",
            UserClaims = ["department"],
        });

        var user = await factory.SeedTestUserAsync();
        user.CustomAttributes["department"] = "Engineering";
        await factory.UserStore.UpdateAsync(user);

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IProtocolTokenService>();
        var resolver = scope.ServiceProvider.GetRequiredService<UserStoreOidcSubjectResolver>();
        var subject = await resolver.BuildSubjectAsync(user, client);

        var idToken = await tokens.CreateIdTokenAsync(subject, client, ["openid", "profile.extras"]);
        var claims = ReadClaims(idToken);

        Assert.Equal("Engineering", claims["department"]);
    }

    [Fact]
    public async Task ClientCredentials_RejectsScope_NotAllowedOnClient()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        var http = factory.CreateClient();
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{AuthagonalTestFactory.AdminClientId}:{AuthagonalTestFactory.AdminClientSecret}"));

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "not-a-real-scope",
        });
        var req = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        var response = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeviceAuthorization_RejectsScope_NotAllowedOnClient()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        // Grant the test client device code + add a known allowed scope.
        var client = (await factory.Services.GetRequiredService<IClientStore>().GetAsync(ClientId))!;
        client.AllowedGrantTypes = [.. client.AllowedGrantTypes, "urn:ietf:params:oauth:grant-type:device_code"];
        await factory.ClientStore.UpsertAsync(client);

        var http = factory.CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "openid ghost-scope",
        });

        var response = await http.PostAsync("/connect/deviceauthorization", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_scope", body.GetProperty("error").GetString());
    }

    private static Dictionary<string, string> ReadClaims(string jwt)
    {
        var token = new JsonWebTokenHandler().ReadJsonWebToken(jwt);
        var dict = new Dictionary<string, string>();
        foreach (var claim in token.Claims)
            dict[claim.Type] = claim.Value;
        return dict;
    }
}

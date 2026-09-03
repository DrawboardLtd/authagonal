using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Authagonal.Protocol;
using Authagonal.Tests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Authagonal.Tests;

/// <summary>
/// The client-credentials host seam. A first-party service client names the context its token acts
/// in through a non-protocol form parameter; the host's transformer decides whether that binding
/// stands and which claims it becomes. Driven end-to-end through /connect/token, because the seam's
/// contract is exactly what reaches it from the wire: which parameters are forwarded, which are not,
/// and how a rejection is reported.
/// </summary>
public sealed class ClientCredentialsClaimsTransformerTests
{
    private static async Task<HttpResponseMessage> RequestTokenAsync(
        AuthagonalTestFactory factory, params (string Key, string Value)[] extra)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = AuthagonalTestFactory.AdminScope,
        };
        foreach (var (key, value) in extra)
            form[key] = value;

        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{AuthagonalTestFactory.AdminClientId}:{AuthagonalTestFactory.AdminClientSecret}"));
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        return await factory.CreateClient().SendAsync(request);
    }

    private static Dictionary<string, object> ReadClaims(string jwt) =>
        new JsonWebToken(jwt).Claims.ToDictionary(c => c.Type, c => (object)c.Value);

    [Fact]
    public async Task ExtensionParameters_ReachTheTransformer_AndProtocolFieldsDoNot()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        var response = await RequestTokenAsync(factory, ("organization_id", "org-1"), ("tenant", "acme"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var call = Assert.Single(factory.ClientCredentialsTransformer.Calls);
        Assert.Equal(AuthagonalTestFactory.AdminClientId, call.ClientId);
        Assert.Contains(AuthagonalTestFactory.AdminScope, call.Scopes);
        Assert.Equal("org-1", call.ExtraParameters["organization_id"]);
        Assert.Equal("acme", call.ExtraParameters["tenant"]);
        Assert.False(call.ExtraParameters.ContainsKey("grant_type"));
        Assert.False(call.ExtraParameters.ContainsKey("scope"));
        Assert.False(call.ExtraParameters.ContainsKey("client_id"));
    }

    [Fact]
    public async Task ForcedClaims_LandOnTheToken_ReservedNamesDoNot()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();
        factory.ClientCredentialsTransformer.Handler = (_, _, extra) =>
            ClientCredentialsClaimsResult.Allow(new Dictionary<string, string>
            {
                ["organization:id"] = extra["organization_id"],
                ["role"] = "org:administrator",
                // Reserved: a host seam asserts context, never protocol.
                ["sub"] = "forged-subject",
                ["scope"] = "authagonal-admin something-else",
            });

        var response = await RequestTokenAsync(factory, ("organization_id", "org-1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var claims = ReadClaims(json.GetProperty("access_token").GetString()!);
        Assert.Equal("org-1", claims["organization:id"]);
        Assert.Equal("org:administrator", claims["role"]);
        Assert.False(claims.ContainsKey("sub"), "a client-credentials token has no subject, forced or otherwise");
        Assert.Equal(AuthagonalTestFactory.AdminScope, claims["scope"]);
    }

    [Fact]
    public async Task ARejection_IsReportedAsTheGivenOAuthError_AndNoTokenIsIssued()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();
        factory.ClientCredentialsTransformer.Handler = (_, _, _) =>
            ClientCredentialsClaimsResult.Reject("invalid_request", "organization_id is not honoured for this client");

        var response = await RequestTokenAsync(factory, ("organization_id", "org-1"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
        Assert.False(json.TryGetProperty("access_token", out _));
    }

    [Fact]
    public async Task WithNothingToForce_TheTokenIsUnchanged()
    {
        await using var factory = new AuthagonalTestFactory();
        await factory.SeedTestDataAsync();

        var response = await RequestTokenAsync(factory);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var call = Assert.Single(factory.ClientCredentialsTransformer.Calls);
        Assert.Empty(call.ExtraParameters);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var claims = ReadClaims(json.GetProperty("access_token").GetString()!);
        Assert.Equal(AuthagonalTestFactory.AdminClientId, claims["client_id"]);
        Assert.False(claims.ContainsKey("role"));
    }
}

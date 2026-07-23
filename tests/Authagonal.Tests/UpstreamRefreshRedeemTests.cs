using System.Net;
using System.Text;
using Authagonal.Core.Models;
using Authagonal.Protocol;
using Authagonal.Server.Services;
using Authagonal.Server.Services.Oidc;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

/// <summary>M4: upstream-refresh revocation keys on error=invalid_grant only. A different 4xx —
/// invalid_client from a rotated/misconfigured client secret — is transient, so the session survives
/// instead of mass-terminating every federated session on the connection.</summary>
public sealed class UpstreamRefreshRedeemTests
{
    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TokenStub : HttpMessageHandler
    {
        public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;
        public string TokenBody { get; set; } = "{\"access_token\":\"a\"}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.ToString();
            (HttpStatusCode Status, string Body) r =
                uri.Contains("openid-configuration") || uri.Contains("well-known")
                    ? (HttpStatusCode.OK, "{\"issuer\":\"https://up.test\",\"authorization_endpoint\":\"https://up.test/authorize\",\"token_endpoint\":\"https://up.test/token\",\"jwks_uri\":\"https://up.test/jwks\"}")
                : uri.Contains("jwks")
                    ? (HttpStatusCode.OK, "{\"keys\":[]}")
                    : (TokenStatus, TokenBody); // the token endpoint
            return Task.FromResult(new HttpResponseMessage(r.Status) { Content = new StringContent(r.Body, Encoding.UTF8, "application/json") });
        }
    }

    private static UserStoreOidcSubjectResolver BuildResolver(TokenStub stub)
    {
        var factory = new StubFactory(stub);
        var oidc = new InMemoryOidcProviderStore();
        oidc.UpsertAsync(new OidcProviderConfig
        {
            ConnectionId = "conn-1",
            MetadataLocation = "https://up.test/.well-known/openid-configuration",
            ClientId = "cid",
            ClientSecret = "csecret",
        }).GetAwaiter().GetResult();

        var users = new InMemoryUserStore();
        users.CreateAsync(new AuthUser { Id = "user-1", Email = "u@example.com", NormalizedEmail = "U@EXAMPLE.COM", EmailConfirmed = true, IsActive = true }).GetAwaiter().GetResult();

        return new UserStoreOidcSubjectResolver(
            users, new InMemoryScimGroupStore(), new InMemoryScimGroupRoleMappingStore(), new InMemoryClientStore(),
            oidc,
            new OidcDiscoveryClient(factory, new MemoryCache(new MemoryCacheOptions()), Options.Create(new CacheOptions())),
            new PlaintextSecretProvider(),
            factory,
            NullLogger<UserStoreOidcSubjectResolver>.Instance);
    }

    private static OidcSubject Prior() => new()
    {
        SubjectId = "user-1",
        UpstreamRefreshToken = "rt-current",
        UpstreamConnectionId = "conn-1",
    };

    [Fact]
    public async Task InvalidClient_isTransient_sessionSurvives()
    {
        var stub = new TokenStub { TokenStatus = HttpStatusCode.BadRequest, TokenBody = "{\"error\":\"invalid_client\"}" };
        var result = await BuildResolver(stub).ResolveRefreshAsync(Prior(), new OidcSubjectResolutionContext("client-1", ["openid"], []));
        Assert.IsType<OidcSubjectResult.Allowed>(result); // NOT revoked — a config fault must not kill the session
    }

    [Fact]
    public async Task InvalidGrant_revokes_theSession()
    {
        var stub = new TokenStub { TokenStatus = HttpStatusCode.BadRequest, TokenBody = "{\"error\":\"invalid_grant\"}" };
        var result = await BuildResolver(stub).ResolveRefreshAsync(Prior(), new OidcSubjectResolutionContext("client-1", ["openid"], []));
        Assert.IsType<OidcSubjectResult.Rejected>(result); // the federated credential is gone — revoke
    }
}

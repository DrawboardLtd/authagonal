using System.Net;
using System.Security.Claims;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// Minimal host exercising the published Authagonal.Protocol surface directly —
/// AddAuthagonalProtocol + MapAuthagonalProtocolEndpoints with in-memory stores and a
/// stub IOidcSubjectResolver, with none of Authagonal.Server wired in. This is the
/// consumer shape (e.g. bullclip): host-owned authentication, drop-in OIDC endpoints.
/// </summary>
public sealed class ProtocolTestHost : IAsyncDisposable
{
    public const string TestIssuer = "https://protocol.test.local";
    public const string SpaClientId = "protocol-spa";
    public const string SpaRedirectUri = "https://rp.test/callback";
    public const string MachineClientId = "protocol-machine";
    public const string MachineClientSecret = "machine-secret-789";
    public const string TestSubjectId = "protocol-user-1";
    public const string TestEmail = "proto-user@example.com";

    public InMemoryClientStore ClientStore { get; } = new();
    public InMemoryGrantStore GrantStore { get; } = new();
    public InMemoryScopeStore ScopeStore { get; } = new();
    public InMemorySigningKeyStore SigningKeyStore { get; } = new();

    private WebApplication? _app;
    private bool _started;

    public HttpClient CreateClient(bool allowAutoRedirect = false)
    {
        EnsureStarted();
        var testServer = _app!.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer not found");

        if (allowAutoRedirect)
            return testServer.CreateClient();

        // No redirect following but still maintain cookies between requests.
        var handler = new CookieHandler(testServer.CreateHandler());
        return new HttpClient(handler) { BaseAddress = testServer.BaseAddress };
    }

    private void EnsureStarted()
    {
        if (_started) return;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var services = builder.Services;
        services.AddSingleton<ITenantContext>(new TestTenantContext(TestIssuer));
        services.AddSingleton<IClientStore>(ClientStore);
        services.AddSingleton<IGrantStore>(GrantStore);
        services.AddSingleton<IScopeStore>(ScopeStore);
        services.AddSingleton<ISigningKeyStore>(SigningKeyStore);
        services.AddSingleton<IOidcSubjectResolver, PrincipalSubjectResolver>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.LoginPath = "/host-login");

        services.AddAuthagonalProtocol(o =>
            o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.MapAuthagonalProtocolEndpoints();

        // Test-only sign-in endpoint so the authorize flow can obtain a session cookie.
        _app.MapGet("/test-login", async (HttpContext ctx) =>
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, TestSubjectId),
                    new Claim("sub", TestSubjectId),
                    new Claim(ClaimTypes.Email, TestEmail),
                ],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.NoContent();
        });

        _app.StartAsync().GetAwaiter().GetResult();
        _started = true;
        SeedClientsAsync().GetAwaiter().GetResult();
    }

    private async Task SeedClientsAsync()
    {
        await ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = SpaClientId,
            ClientName = "Protocol SPA",
            RequireClientSecret = false,
            RequirePkce = true,
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            RedirectUris = [SpaRedirectUri],
            AllowedScopes = ["openid", "profile", "email", "offline_access"],
            AllowOfflineAccess = true,
            AccessTokenLifetimeSeconds = 3600,
        });

        await ClientStore.UpsertAsync(new OAuthClient
        {
            ClientId = MachineClientId,
            ClientName = "Protocol Machine",
            RequireClientSecret = true,
            RequirePkce = false,
            ClientSecretHashes = [BCrypt.Net.BCrypt.HashPassword(MachineClientSecret)],
            AllowedGrantTypes = ["client_credentials"],
            AllowedScopes = ["openid", "machine-api"],
            AccessTokenLifetimeSeconds = 3600,
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// Resolves the OIDC subject straight off the host-authenticated principal — the
    /// minimal consumer implementation (no user store).
    /// </summary>
    private sealed class PrincipalSubjectResolver : IOidcSubjectResolver
    {
        public Task<OidcSubjectResult> ResolveAsync(
            ClaimsPrincipal authenticatedPrincipal, OidcSubjectResolutionContext context, CancellationToken ct = default)
        {
            var subjectId = authenticatedPrincipal.FindFirstValue("sub")
                ?? authenticatedPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(subjectId))
                return Task.FromResult(OidcSubjectResult.Reject(OidcRejection.LoginRequired));

            return Task.FromResult(OidcSubjectResult.Allow(new OidcSubject
            {
                SubjectId = subjectId,
                Email = authenticatedPrincipal.FindFirstValue(ClaimTypes.Email),
                EmailVerified = true,
            }));
        }

        public Task<OidcSubjectResult> ResolveRefreshAsync(
            OidcSubject priorSubject, OidcSubjectResolutionContext context, CancellationToken ct = default)
            => Task.FromResult(OidcSubjectResult.Allow(priorSubject));
    }

    private sealed class CookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly CookieContainer _cookies = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var cookieHeader = _cookies.GetCookieHeader(request.RequestUri!);
            if (!string.IsNullOrEmpty(cookieHeader))
                request.Headers.Add("Cookie", cookieHeader);

            var response = await base.SendAsync(request, cancellationToken);

            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                    _cookies.SetCookies(request.RequestUri!, setCookie);
            }

            return response;
        }
    }
}

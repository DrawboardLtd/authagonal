using System.Net;
using System.Security.Claims;
using Authagonal.OidcProvider;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;

namespace Authagonal.Tests.Infrastructure;

internal sealed class OidcTestDbContext(DbContextOptions<OidcTestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();
    }
}

internal sealed class TestSubjectResolver : IOidcSubjectResolver
{
    public Task<OidcSubject?> ResolveAsync(
        ClaimsPrincipal principal,
        OidcSubjectResolutionContext context,
        CancellationToken ct = default)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue(OpenIddictConstants.Claims.Subject);
        if (string.IsNullOrEmpty(sub))
        {
            return Task.FromResult<OidcSubject?>(null);
        }

        return Task.FromResult<OidcSubject?>(new OidcSubject
        {
            SubjectId = sub,
            Email = $"{sub}@test.local",
            EmailVerified = true,
            Name = $"Test {sub}",
        });
    }
}

/// <summary>
/// Minimal in-process host for exercising Authagonal.OidcProvider end-to-end. Uses a
/// SQLite in-memory database for OpenIddict storage and exposes <c>/__test/login</c> to
/// drive cookie sign-in from tests.
/// </summary>
internal sealed class OidcProviderTestHost : IAsyncDisposable
{
    public const string TestClientId = "test-client";
    public const string TestRedirectUri = "https://app.test/cb";

    private readonly SqliteConnection _connection;
    private readonly IHost _host;

    public OidcProviderTestHost()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.UseContentRoot(AppContext.BaseDirectory);

                web.ConfigureServices(services =>
                {
                    services.AddDbContext<OidcTestDbContext>(o =>
                    {
                        o.UseSqlite(_connection);
                        o.UseOpenIddict();
                    });

                    services.AddAuthagonalOidcProvider(o =>
                    {
                        o.Issuer = "https://auth.test";
                        o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        o.Clients.Add(new OidcClientDescriptor
                        {
                            ClientId = TestClientId,
                            RedirectUris = { TestRedirectUri },
                            AllowedScopes = { "offline_access" },
                            RequirePkce = true,
                        });
                    })
                    .AddCore(core =>
                    {
                        core.UseEntityFrameworkCore().UseDbContext<OidcTestDbContext>();
                    })
                    .AddServer(server =>
                    {
                        server.AddDevelopmentEncryptionCertificate()
                              .AddDevelopmentSigningCertificate();
                        server.UseAspNetCore().DisableTransportSecurityRequirement();
                    })
                    .AddValidation(validation =>
                    {
                        validation.UseLocalServer();
                        validation.UseAspNetCore();
                    });

                    services.AddScoped<IOidcSubjectResolver, TestSubjectResolver>();

                    services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                            .AddCookie(o =>
                            {
                                o.Cookie.Name = "test.auth";
                                o.LoginPath = "/__test/login";
                            });

                    services.AddAuthorization();
                    services.AddRouting();
                });

                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();

                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/__test/login", async http =>
                        {
                            var sub = http.Request.Query["sub"].ToString();
                            var returnUrl = http.Request.Query["returnUrl"].ToString();
                            if (string.IsNullOrEmpty(returnUrl))
                            {
                                returnUrl = http.Request.Query["ReturnUrl"].ToString();
                            }

                            var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
                            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
                            identity.AddClaim(new Claim(ClaimTypes.Name, sub));
                            await http.SignInAsync(
                                CookieAuthenticationDefaults.AuthenticationScheme,
                                new ClaimsPrincipal(identity));

                            http.Response.Redirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
                        });

                        endpoints.MapAuthagonalOidcEndpoints();
                    });
                });
            })
            .Build();

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OidcTestDbContext>();
            db.Database.EnsureCreated();
        }

        _host.Start();
    }

    public HttpClient CreateClient()
    {
        var server = _host.GetTestServer();
        server.BaseAddress = new Uri("https://localhost/");
        var cookieHandler = new CookieHandler { InnerHandler = server.CreateHandler() };
        return new HttpClient(cookieHandler)
        {
            BaseAddress = new Uri("https://localhost/"),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// HttpClient on TestServer doesn't speak cookies natively. This accumulates
    /// Set-Cookie responses into a container and replays them on outgoing requests,
    /// matching what a browser (or HttpClientHandler with UseCookies=true) would do.
    /// </summary>
    private sealed class CookieHandler : DelegatingHandler
    {
        private readonly CookieContainer _cookies = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var cookieHeader = _cookies.GetCookieHeader(request.RequestUri!);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.Remove("Cookie");
                request.Headers.Add("Cookie", cookieHeader);
            }

            var response = await base.SendAsync(request, cancellationToken);

            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var sc in setCookies)
                {
                    _cookies.SetCookies(request.RequestUri!, sc);
                }
            }

            return response;
        }
    }
}

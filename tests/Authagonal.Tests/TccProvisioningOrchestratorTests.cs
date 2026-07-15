using System.Net;
using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Try-Confirm-Cancel provisioning saga. The HTTP side is faked with a recording handler keyed by
/// app host + phase path, so each test asserts the exact callback sequence: all-tries-succeed →
/// confirm fan-out; a try rejection/failure → compensation cancels for the already-tried subset;
/// a confirm failure → the unconfirmed subset is cancelled and only confirmed apps get provision rows.
/// </summary>
public class TccProvisioningOrchestratorTests
{
    // ── Test doubles ─────────────────────────────────────────────────

    /// <summary>One recorded provisioning callback: "app1:/try", "app2:/cancel", …</summary>
    private sealed record Call(string AppId, string Phase, string? Bearer, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        /// <summary>Optional overrides keyed "appId:/phase"; default is 200 with an empty body (→ approved).</summary>
        public Dictionary<string, Func<HttpResponseMessage>> Responders { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var appId = request.RequestUri!.Host.Split('.')[0]; // https://app1.test/... → app1
            var phase = request.RequestUri.AbsolutePath;         // /try, /confirm, /cancel
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add(new Call(appId, phase, request.Headers.Authorization?.Parameter, body));

            return Responders.TryGetValue($"{appId}:{phase}", out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeAppProvider(params ProvisioningApp[] apps) : IProvisioningAppProvider
    {
        public Task<IReadOnlyList<ProvisioningApp>> GetAppsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProvisioningApp>>(apps);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ProvisioningApp App(string id, string? apiKey = null) => new(id, $"https://{id}.test", apiKey);

    private static AuthUser User() => new()
    {
        Id = "user-1",
        Email = "ada@acme.test",
        NormalizedEmail = "ada@acme.test",
        FirstName = "Ada",
    };

    private readonly RecordingHandler _handler = new();
    private readonly InMemoryUserProvisionStore _provisions = new();

    private TccProvisioningOrchestrator NewOrchestrator(params ProvisioningApp[] apps)
    {
        // The orchestrator resolves IUserProvisionStore from the current request's services.
        var requestServices = new ServiceCollection()
            .AddSingleton<IUserProvisionStore>(_provisions)
            .BuildServiceProvider();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { RequestServices = requestServices }
        };

        return new TccProvisioningOrchestrator(
            new FakeHttpClientFactory(_handler),
            accessor,
            new FakeAppProvider(apps),
            NullLogger<TccProvisioningOrchestrator>.Instance);
    }

    private IEnumerable<string> CallSequence => _handler.Calls.Select(c => $"{c.AppId}:{c.Phase}");

    // ── Happy path ───────────────────────────────────────────────────

    [Fact]
    public async Task AllTriesSucceed_ConfirmsAll_AndPersistsProvisionRecords()
    {
        var orchestrator = NewOrchestrator(App("app1"), App("app2"), App("app3"));

        await orchestrator.ProvisionAsync(User());

        Assert.Equal(
            ["app1:/try", "app2:/try", "app3:/try", "app1:/confirm", "app2:/confirm", "app3:/confirm"],
            CallSequence);

        var records = await _provisions.GetByUserAsync("user-1");
        Assert.Equal(["app1", "app2", "app3"], records.Select(p => p.AppId).Order());
    }

    [Fact]
    public async Task TryPayload_CarriesUserIdentity_AndBearerApiKey()
    {
        var orchestrator = NewOrchestrator(App("app1", apiKey: "sk-secret"));

        await orchestrator.ProvisionAsync(User());

        var tryCall = _handler.Calls.Single(c => c.Phase == "/try");
        Assert.Equal("sk-secret", tryCall.Bearer);
        Assert.Contains("\"userId\":\"user-1\"", tryCall.Body);
        Assert.Contains("\"email\":\"ada@acme.test\"", tryCall.Body);
        // All three phases hit the same app under one transaction id.
        var confirmCall = _handler.Calls.Single(c => c.Phase == "/confirm");
        Assert.Contains("\"transactionId\"", confirmCall.Body);
        Assert.Equal("sk-secret", confirmCall.Bearer);
    }

    [Fact]
    public async Task NoConfiguredApps_MakesNoCalls()
    {
        await NewOrchestrator().ProvisionAsync(User());

        Assert.Empty(_handler.Calls);
        Assert.Empty(await _provisions.GetByUserAsync("user-1"));
    }

    [Fact]
    public async Task AlreadyProvisionedApps_AreSkipped()
    {
        await _provisions.StoreAsync(new UserProvision
        {
            UserId = "user-1",
            AppId = "app1",
            ProvisionedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });
        var orchestrator = NewOrchestrator(App("app1"), App("app2"));

        await orchestrator.ProvisionAsync(User());

        Assert.Equal(["app2:/try", "app2:/confirm"], CallSequence);
    }

    // ── Try-phase compensation ───────────────────────────────────────

    [Fact]
    public async Task TryRejected_CancelsPreviouslyTriedApps_AndReportsFailure()
    {
        _handler.Responders["app2:/try"] = () => Json("""{"approved":false,"reason":"quota exceeded"}""");
        var orchestrator = NewOrchestrator(App("app1"), App("app2"), App("app3"));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() => orchestrator.ProvisionAsync(User()));

        Assert.Equal("app2", ex.AppId);
        Assert.Equal("quota exceeded", ex.Reason);
        // app1 (the only successful try) is compensated; app3 was never reached; nothing confirmed.
        Assert.Equal(["app1:/try", "app2:/try", "app1:/cancel"], CallSequence);
        Assert.Empty(await _provisions.GetByUserAsync("user-1"));
    }

    [Fact]
    public async Task TryHttpFailure_CancelsPreviouslyTriedApps_AndReportsFailure()
    {
        _handler.Responders["app2:/try"] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var orchestrator = NewOrchestrator(App("app1"), App("app2"), App("app3"));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() => orchestrator.ProvisionAsync(User()));

        Assert.Equal("app2", ex.AppId);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal(["app1:/try", "app2:/try", "app1:/cancel"], CallSequence);
        Assert.Empty(await _provisions.GetByUserAsync("user-1"));
    }

    [Fact]
    public async Task FirstTryFails_NothingToCompensate()
    {
        _handler.Responders["app1:/try"] = () => Json("""{"approved":false,"reason":"nope"}""");
        var orchestrator = NewOrchestrator(App("app1"), App("app2"));

        await Assert.ThrowsAsync<ProvisioningException>(() => orchestrator.ProvisionAsync(User()));

        Assert.Equal(["app1:/try"], CallSequence); // no cancels, no confirms
    }

    // ── Confirm-phase compensation ───────────────────────────────────

    [Fact]
    public async Task ConfirmFailure_CancelsUnconfirmedSubset_AndPersistsOnlyConfirmed()
    {
        _handler.Responders["app2:/confirm"] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var orchestrator = NewOrchestrator(App("app1"), App("app2"), App("app3"));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() => orchestrator.ProvisionAsync(User()));

        Assert.Equal("app2", ex.AppId);
        // app1 confirmed before the failure and keeps its provision. app3 is still try-only →
        // cancelled. app2 itself is deliberately NOT cancelled (its confirm may or may not have
        // landed downstream; the reservation expires via TTL) — asserting current behavior.
        Assert.Equal(
            ["app1:/try", "app2:/try", "app3:/try", "app1:/confirm", "app2:/confirm", "app3:/cancel"],
            CallSequence);

        var records = await _provisions.GetByUserAsync("user-1");
        Assert.Equal("app1", Assert.Single(records).AppId);
    }

    // ── Try-response merge semantics ─────────────────────────────────

    [Fact]
    public async Task TryResponses_MergeOrganizationIdFirstWriterWins_AndUnionCustomAttributes()
    {
        _handler.Responders["app1:/try"] = () =>
            Json("""{"approved":true,"organizationId":"org-1","customAttributes":{"org_role":"admin"}}""");
        _handler.Responders["app2:/try"] = () =>
            Json("""{"approved":true,"organizationId":"org-2","customAttributes":{"team":"engineering"}}""");
        var orchestrator = NewOrchestrator(App("app1"), App("app2"));
        var user = User();

        await orchestrator.ProvisionAsync(user);

        Assert.Equal("org-1", user.OrganizationId); // app2 sees app1's assignment and can't overwrite
        Assert.Equal("admin", user.CustomAttributes["org_role"]);
        Assert.Equal("engineering", user.CustomAttributes["team"]);
    }

    // ── Per-client required-app overload ─────────────────────────────

    [Fact]
    public async Task RequiredAppId_NotConfigured_Throws()
    {
        var orchestrator = NewOrchestrator(App("app1"));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(
            () => orchestrator.ProvisionAsync(User(), ["ghost"]));

        Assert.Equal("ghost", ex.AppId);
        Assert.Empty(_handler.Calls); // fails during config resolution, before any Try
    }

    [Fact]
    public async Task RequiredAppIds_ResolveThroughProvider_AndProvision()
    {
        var orchestrator = NewOrchestrator(App("app1"), App("app2"));

        await orchestrator.ProvisionAsync(User(), ["app2"]);

        Assert.Equal(["app2:/try", "app2:/confirm"], CallSequence);
        Assert.Equal("app2", Assert.Single(await _provisions.GetByUserAsync("user-1")).AppId);
    }

    // ── Deprovision ──────────────────────────────────────────────────

    [Fact]
    public async Task DeprovisionAll_DeletesDownstream_AndRemovesRecords()
    {
        await _provisions.StoreAsync(new UserProvision { UserId = "user-1", AppId = "app1", ProvisionedAt = DateTimeOffset.UtcNow });
        await _provisions.StoreAsync(new UserProvision { UserId = "user-1", AppId = "app2", ProvisionedAt = DateTimeOffset.UtcNow });
        var orchestrator = NewOrchestrator(App("app1"), App("app2"));

        await orchestrator.DeprovisionAllAsync("user-1");

        Assert.Equal(2, _handler.Calls.Count(c => c.Phase == "/users/user-1"));
        Assert.Empty(await _provisions.GetByUserAsync("user-1"));
    }

    [Fact]
    public async Task DeprovisionAll_DownstreamFailure_StillRemovesRecord()
    {
        await _provisions.StoreAsync(new UserProvision { UserId = "user-1", AppId = "app1", ProvisionedAt = DateTimeOffset.UtcNow });
        _handler.Responders["app1:/users/user-1"] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var orchestrator = NewOrchestrator(App("app1"));

        await orchestrator.DeprovisionAllAsync("user-1"); // does not throw

        Assert.Empty(await _provisions.GetByUserAsync("user-1"));
    }
}

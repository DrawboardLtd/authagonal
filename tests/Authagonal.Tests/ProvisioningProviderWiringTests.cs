using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Tests;

/// <summary>
/// An app created through the admin API is the app the orchestrator provisions into.
/// </summary>
/// <remarks>
/// <c>/api/v1/provisioning/apps</c> (list/create/update/delete/test) reads and writes
/// <c>IProvisioningAppStore</c>, while <c>TccProvisioningOrchestrator</c> resolves apps ONLY through
/// <c>IProvisioningAppProvider</c> — and the sole registration of that interface anywhere was
/// <c>TryAddScoped&lt;IProvisioningAppProvider, ConfigProvisioningAppProvider&gt;()</c>, which reads the
/// <c>ProvisioningApps:*</c> configuration section. <c>StoreProvisioningAppProvider</c>, the class whose whole
/// purpose is to bridge the store to the orchestrator, was registered nowhere in the repository.
/// <para>
/// So every app an operator created or edited through the admin API was persisted and then consulted by
/// nothing: the endpoint returned 200, the row existed, and provisioning ran against configuration alone.
/// The admin surface is mapped unconditionally whenever the admin API is enabled, so there was no
/// deployment in which it did what it appeared to do.
/// </para>
/// </remarks>
public sealed class ProvisioningProviderWiringTests : IAsyncLifetime
{
    private readonly AuthagonalTestFactory _factory = new();

    public async Task InitializeAsync()
    {
        _ = _factory.CreateClient();
        await _factory.SeedTestDataAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    /// <summary>An app written to the store is visible to the provider the orchestrator resolves.</summary>
    [Fact]
    public async Task AnAppWrittenToTheStoreIsSeenByTheOrchestratorsProvider()
    {
        using var scope = _factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IProvisioningAppStore>();
        await store.UpsertAsync(new ProvisioningAppConfig
        {
            AppId = "billing",
            CallbackUrl = "https://billing.test/provisioning",
            TryTimeoutSeconds = 5,
        });

        var provider = scope.ServiceProvider.GetRequiredService<IProvisioningAppProvider>();
        var apps = await provider.GetAppsAsync();

        var app = Assert.Single(apps);
        Assert.Equal("billing", app.AppId);
        Assert.Equal("https://billing.test/provisioning", app.CallbackUrl);
    }

    /// <summary>
    /// The provider resolved is the store-backed one whenever a store is registered.
    /// </summary>
    /// <remarks>
    /// Asserted on the concrete type as well as on behaviour, because the two failure modes look different:
    /// an empty config section and an empty store both yield an empty list, so a behavioural assertion alone
    /// would pass against the old wiring on a deployment that had configured nothing.
    /// </remarks>
    [Fact]
    public void TheStoreBackedProviderIsTheDefaultWhenAStoreExists()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IProvisioningAppStore>());
        Assert.IsType<StoreProvisioningAppProvider>(
            scope.ServiceProvider.GetRequiredService<IProvisioningAppProvider>());
    }
}

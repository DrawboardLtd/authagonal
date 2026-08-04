using Authagonal.Migration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// <c>GET /admin/migration/status</c> has to be reachable, and its absence has to be audible.
/// </summary>
/// <remarks>
/// <c>MapMigrationStatusEndpoint</c> was defined and called by nobody — not by
/// <c>AddAuthagonalDuendeMigration</c> (which registers services, not routes), not by
/// <c>MapAuthagonalEndpoints</c> (which cannot see this package, since the dependency runs the other way),
/// and not by any test. Meanwhile <c>docs/migration.md</c> named the route twice as the way to read the
/// report, so the documented cutover's verification step answered 404 — the same answer the endpoint's own
/// <c>IdentityAdmin</c> policy produces for an unauthorized caller, which is why it read as a permissions
/// problem rather than a missing route.
/// <para>
/// A probe against an unmapped route cannot fail, which is why an earlier session recorded a fix here as
/// "probed, nothing failed, so the fix was right and the test was missing". The route was never there.
/// </para>
/// </remarks>
public class MigrationStatusEndpointTests
{
    [Fact]
    public void MappingRegistersTheDocumentedRoute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<MigrationEndpointRegistration>();
        var app = builder.Build();

        app.MapAuthagonalDuendeMigration();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.Contains("/admin/migration/status", routes);
    }

    [Fact]
    public void MappingMarksTheRegistration_SoTheStartupCheckStaysQuiet()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<MigrationEndpointRegistration>();
        var app = builder.Build();

        var registration = app.Services.GetRequiredService<MigrationEndpointRegistration>();
        Assert.False(registration.Mapped);

        app.MapAuthagonalDuendeMigration();

        Assert.True(registration.Mapped);
    }

    /// <summary>
    /// Enabled migration plus an unmapped endpoint is the state that shipped, so it has to be reported.
    /// </summary>
    [Fact]
    public async Task StartupCheck_WarnsWhenTheMigrationIsEnabledAndTheEndpointIsNotMapped()
    {
        var logger = new CapturingLogger<MigrationStatusEndpointCheck>();
        var check = new MigrationStatusEndpointCheck(
            new DuendeMigrationOptions { Enabled = true },
            new MigrationEndpointRegistration(),
            logger);

        await check.StartAsync(CancellationToken.None);

        Assert.Contains(logger.Messages,
            m => m.Contains("/admin/migration/status", StringComparison.Ordinal)
                 && m.Contains("MapAuthagonalDuendeMigration", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartupCheck_SaysNothingOnceTheEndpointIsMapped()
    {
        var logger = new CapturingLogger<MigrationStatusEndpointCheck>();
        var registration = new MigrationEndpointRegistration();
        registration.MarkMapped();

        var check = new MigrationStatusEndpointCheck(
            new DuendeMigrationOptions { Enabled = true }, registration, logger);

        await check.StartAsync(CancellationToken.None);

        Assert.Empty(logger.Messages);
    }

    /// <summary>A host that never enabled the migration is not nagged about its endpoint.</summary>
    [Fact]
    public async Task StartupCheck_SaysNothingWhenTheMigrationIsDisabled()
    {
        var logger = new CapturingLogger<MigrationStatusEndpointCheck>();
        var check = new MigrationStatusEndpointCheck(
            new DuendeMigrationOptions { Enabled = false },
            new MigrationEndpointRegistration(),
            logger);

        await check.StartAsync(CancellationToken.None);

        Assert.Empty(logger.Messages);
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

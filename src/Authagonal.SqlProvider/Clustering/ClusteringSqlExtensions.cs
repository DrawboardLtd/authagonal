using Authagonal.Core.Clustering;
using Authagonal.SqlProvider.Sql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.SqlProvider.Clustering;

/// <summary>
/// SQL backend for the Authagonal clustering layer — the counterpart to
/// <c>ClusteringStorageExtensions.UseAzureStorage</c> and <c>ClusteringAwsExtensions.UseAwsDynamo</c>.
/// Leadership via a conditional-upsert lease row, the cross-node event bus via an append-only log.
/// Reuses the same database; no extra infrastructure.
/// <para>
/// Only worth wiring on PostgreSQL. A SQLite deployment is one process by construction, and the
/// in-process lease and bus registered by default are both correct and cheaper there.
/// </para>
/// </summary>
public static class ClusteringSqlExtensions
{
    /// <summary>
    /// Full backend: leadership via a SQL lease and the event bus via a SQL log. Use on the node(s)
    /// that run leader-gated work (e.g. the Auth service).
    /// </summary>
    public static ClusteringBuilder UseSql(
        this ClusteringBuilder builder,
        SqlDataSource source,
        string leaseTable = "ClusterLeases",
        string eventTable = "ClusterEvents",
        TimeSpan? pollInterval = null)
    {
        source.EnsureTableAsync(leaseTable).GetAwaiter().GetResult();
        var leases = new SqlTable(source, leaseTable);

        builder.Services.Replace(ServiceDescriptor.Singleton<ILeaseProvider>(sp =>
            new SqlLeaseProvider(leases, sp.GetRequiredService<ILogger<SqlLeaseProvider>>())));

        AddSqlBus(builder, source, eventTable, pollInterval);
        return builder;
    }

    /// <summary>
    /// Event bus only — keeps the in-process (always-leader) lease. Use on nodes that must receive
    /// cluster events but must not contend for leadership (e.g. Portal/Admin).
    /// </summary>
    public static ClusteringBuilder UseSqlBus(
        this ClusteringBuilder builder,
        SqlDataSource source,
        string eventTable = "ClusterEvents",
        TimeSpan? pollInterval = null)
    {
        // Never-granting lease, so this really is bus-only.
        //
        // This left InProcessLeaseProvider in place, which unconditionally grants — so a node wired the
        // way the summary above describes became leader on its first tick and every tick after, while a
        // real cluster node held the distributed lease too. Two leaders is what leader election exists
        // to prevent: the guarded work is signing-key generation and the expiry reaper.
        builder.Services.Replace(ServiceDescriptor.Singleton<ILeaseProvider, NeverLeaseProvider>());

        AddSqlBus(builder, source, eventTable, pollInterval);
        return builder;
    }

    private static void AddSqlBus(ClusteringBuilder builder, SqlDataSource source, string eventTable, TimeSpan? pollInterval)
    {
        source.EnsureTableAsync(eventTable).GetAwaiter().GetResult();
        var events = new SqlTable(source, eventTable);
        // Caller argument wins, then Cluster:PollIntervalSeconds, then the built-in default.
        var interval = pollInterval ?? builder.PollInterval ?? TimeSpan.FromSeconds(3);

        builder.Services.RemoveAll<SqlClusterEventBus>();
        builder.Services.AddSingleton(sp => new SqlClusterEventBus(
            events, interval, sp.GetRequiredService<ILogger<SqlClusterEventBus>>()));

        // Same instance serves the bus contract and runs the polling loop.
        builder.Services.Replace(ServiceDescriptor.Singleton<IClusterEventBus>(
            sp => sp.GetRequiredService<SqlClusterEventBus>()));
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SqlClusterEventBus>());
    }
}

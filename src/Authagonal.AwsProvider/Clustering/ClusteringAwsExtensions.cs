using Amazon.DynamoDBv2;
using Authagonal.AwsProvider.Dynamo;
using Authagonal.Core.Clustering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.AwsProvider.Clustering;

/// <summary>
/// AWS (DynamoDB) backend for the Authagonal clustering layer — the counterpart to
/// <c>ClusteringStorageExtensions.UseAzureStorage</c>. Leadership via a conditional-write lease item,
/// the cross-node event bus via an append-only table. Reuses one DynamoDB account; no extra AWS service.
/// </summary>
public static class ClusteringAwsExtensions
{
    /// <summary>
    /// Full backend: leadership via a DynamoDB lease and the event bus via a DynamoDB log. Use on the
    /// node(s) that run leader-gated work (e.g. the Auth service).
    /// </summary>
    public static ClusteringBuilder UseAwsDynamo(
        this ClusteringBuilder builder,
        IAmazonDynamoDB db,
        string leaseTable = "ClusterLeases",
        string eventTable = "ClusterEvents",
        TimeSpan? pollInterval = null)
    {
        DynamoTableProvisioner.EnsureTableAsync(db, leaseTable).GetAwaiter().GetResult();

        builder.Services.Replace(ServiceDescriptor.Singleton<ILeaseProvider>(sp =>
            new DynamoLeaseProvider(db, leaseTable, sp.GetRequiredService<ILogger<DynamoLeaseProvider>>())));

        AddDynamoBus(builder, db, eventTable, pollInterval);
        return builder;
    }

    /// <summary>
    /// Event bus only — keeps the in-process (always-leader) lease. Use on nodes that must receive
    /// cluster events but must not contend for leadership (e.g. Portal/Admin).
    /// </summary>
    public static ClusteringBuilder UseAwsDynamoBus(
        this ClusteringBuilder builder,
        IAmazonDynamoDB db,
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

        AddDynamoBus(builder, db, eventTable, pollInterval);
        return builder;
    }

    private static void AddDynamoBus(ClusteringBuilder builder, IAmazonDynamoDB db, string eventTable, TimeSpan? pollInterval)
    {
        DynamoTableProvisioner.EnsureTableAsync(db, eventTable).GetAwaiter().GetResult();
        // Caller argument wins, then Cluster:PollIntervalSeconds, then the built-in default.
        var interval = pollInterval ?? builder.PollInterval ?? TimeSpan.FromSeconds(3);

        builder.Services.RemoveAll<DynamoClusterEventBus>();
        builder.Services.AddSingleton(sp => new DynamoClusterEventBus(
            new DynamoTable(db, eventTable),
            interval,
            sp.GetRequiredService<ILogger<DynamoClusterEventBus>>()));

        // Same instance serves the bus contract and runs the polling loop.
        builder.Services.Replace(ServiceDescriptor.Singleton<IClusterEventBus>(
            sp => sp.GetRequiredService<DynamoClusterEventBus>()));
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DynamoClusterEventBus>());
    }
}

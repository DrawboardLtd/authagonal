using Authagonal.Core.Clustering;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.AzureProvider.Clustering;

/// <summary>
/// Azure-storage backend for the Authagonal clustering layer. Reuses an existing storage account
/// (blob lease for leadership, table log for the event bus) — no new Azure service required, and it
/// works on both ACA and AKS.
/// </summary>
public static class ClusteringAzureExtensions
{
    /// <summary>
    /// Full backend: leadership via a blob lease and the cross-node event bus via a table log.
    /// Use on the node(s) that run leader-gated work (e.g. the Auth service).
    /// </summary>
    public static ClusteringBuilder UseAzureStorage(
        this ClusteringBuilder builder,
        BlobServiceClient blobServiceClient,
        TableServiceClient tableServiceClient,
        string leaseContainer = "cluster",
        string eventTable = "clusterevents",
        TimeSpan? pollInterval = null)
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<ILeaseProvider>(sp =>
            new BlobLeaseProvider(blobServiceClient, leaseContainer, sp.GetRequiredService<ILogger<BlobLeaseProvider>>())));

        AddTableBus(builder, tableServiceClient, eventTable, pollInterval);
        return builder;
    }

    /// <summary>
    /// Event bus only — keeps the in-process (always-leader) lease. Use on nodes that must receive
    /// cluster events but must not contend for leadership (e.g. Portal/Admin), so they can't win the
    /// lease away from the node that actually runs the leader-gated jobs.
    /// </summary>
    public static ClusteringBuilder UseAzureStorageBus(
        this ClusteringBuilder builder,
        TableServiceClient tableServiceClient,
        string eventTable = "clusterevents",
        TimeSpan? pollInterval = null)
    {
        AddTableBus(builder, tableServiceClient, eventTable, pollInterval);
        return builder;
    }

    private static void AddTableBus(
        ClusteringBuilder builder, TableServiceClient tableServiceClient, string eventTable, TimeSpan? pollInterval)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(3);

        builder.Services.RemoveAll<TableClusterEventBus>();
        builder.Services.AddSingleton(sp => new TableClusterEventBus(
            tableServiceClient.GetTableClient(eventTable),
            interval,
            sp.GetRequiredService<ILogger<TableClusterEventBus>>()));

        // Same instance serves the bus contract and runs the polling loop.
        builder.Services.Replace(ServiceDescriptor.Singleton<IClusterEventBus>(
            sp => sp.GetRequiredService<TableClusterEventBus>()));
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TableClusterEventBus>());
    }
}

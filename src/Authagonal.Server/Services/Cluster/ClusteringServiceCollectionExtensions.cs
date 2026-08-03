using Authagonal.Core.Clustering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography;

namespace Authagonal.Server;

/// <summary>
/// Registration for the pluggable clustering layer — leader election + a cross-node event bus.
/// Defaults to single-node in-process behaviour (always leader, local-only bus). A backend such as
/// <c>UseAzureStorage</c> / <c>UseAzureStorageBus</c> (from <c>Authagonal.AzureProvider</c>) swaps in real
/// implementations.
/// </summary>
public static class ClusteringServiceCollectionExtensions
{
    /// <summary>
    /// Registers cluster node identity, leader election (read via <see cref="Core.Clustering.ILeaderElection"/>
    /// or the <c>ClusterLeaderService</c> façade), and a <see cref="Core.Clustering.IClusterEventBus"/>.
    /// The <paramref name="configure"/> callback lets a backend replace the in-process defaults.
    /// </summary>
    /// <param name="runLeaderElection">
    /// When true (default) this node runs the lease-renewal loop and can become leader. Set false on
    /// nodes that must receive cluster events but must never hold leadership (e.g. Portal/Admin), so
    /// they can't win the lease away from the node that runs the leader-gated jobs.
    /// </param>
    public static ClusteringBuilder AddAuthagonalClustering(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<ClusteringBuilder>? configure = null,
        bool runLeaderElection = true)
    {
        var section = configuration.GetSection("Cluster");
        services.Configure<Services.Cluster.ClusterOptions>(section);

        var options = new Services.Cluster.ClusterOptions();
        section.Bind(options);

        var node = new Services.Cluster.ClusterNode(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
        services.AddSingleton(node);

        var election = new LeaderElection(node.NodeId);
        services.AddSingleton(election);
        services.AddSingleton<ILeaderElection>(election);
        services.AddSingleton<Services.Cluster.ClusterLeaderService>();

        // In-process defaults — a backend (configure) may replace these.
        services.TryAddSingleton<ILeaseProvider, InProcessLeaseProvider>();
        services.TryAddSingleton<IClusterEventBus, InProcessClusterEventBus>();

        // Renews the lease and publishes leadership to LeaderElection. With the in-process lease this
        // node is always leader; with a real lease backend, exactly one node is. Skipped on nodes that
        // must never hold leadership (they keep the default IsLeader = false).
        // Both gates, because they come from different places: the parameter is for a host that composes
        // clustering itself, and Cluster:RunLeaderElection is for one that goes through AddAuthagonal — which
        // never exposed the parameter, so configuration was the only reachable switch and it did not exist.
        if (runLeaderElection && options.RunLeaderElection)
            services.AddHostedService<Services.Cluster.LeaderElectionService>();
        else
            // Otherwise the always-granting in-process default would still be resolvable by anything that
            // asks for a lease directly, and the intent here is "this node never leads".
            services.Replace(ServiceDescriptor.Singleton<ILeaseProvider, NeverLeaseProvider>());

        var builder = new ClusteringBuilder(
            services,
            options.PollIntervalSeconds > 0 ? TimeSpan.FromSeconds(options.PollIntervalSeconds) : null);

        // Cluster:Enabled=false means "run standalone", and only half of that was honoured: the
        // leader-election loop short-circuited to permanent leader, but the backend callback still ran
        // and REPLACED the in-process bus with the distributed one. A node an operator had deliberately
        // taken out of the cluster therefore kept polling the shared event log and acting on other
        // nodes' invalidations, while believing itself standalone — the two halves of the switch
        // disagreeing is worse than either answer.
        if (!options.Enabled)
            return builder;

        configure?.Invoke(builder);
        return builder;
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.Core.Clustering;

/// <summary>
/// Fluent surface returned by <c>AddAuthagonalClustering</c>. Backends (e.g. the Azure-storage
/// package) provide extension methods on this type to swap the in-process defaults for real
/// implementations of <see cref="ILeaseProvider"/> and <see cref="IClusterEventBus"/>.
/// </summary>
public sealed class ClusteringBuilder
{
    public ClusteringBuilder(IServiceCollection services)
        : this(services, null)
    {
    }

    public ClusteringBuilder(IServiceCollection services, TimeSpan? pollInterval)
    {
        Services = services;
        PollInterval = pollInterval;
    }

    /// <summary>The underlying service collection, for backends to register their implementations.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// The operator's configured <c>Cluster:PollIntervalSeconds</c>, for a backend to use when its own
    /// <c>pollInterval</c> argument is not supplied. Null when the setting was left at its default.
    /// </summary>
    /// <remarks>
    /// The setting was documented and bound and then read by nobody: every backend fell straight to its
    /// own hard-coded three seconds, so an operator who slowed the poll down to cut storage
    /// transactions — or sped it up to shorten cache-invalidation lag — changed nothing at all and had
    /// no way to tell.
    /// </remarks>
    public TimeSpan? PollInterval { get; }
}

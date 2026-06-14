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
    {
        Services = services;
    }

    /// <summary>The underlying service collection, for backends to register their implementations.</summary>
    public IServiceCollection Services { get; }
}

namespace Authagonal.Server.Services.Cluster;

/// <summary>
/// Identity of this node in the cluster.
/// Generated once at startup and used by all cluster services.
/// </summary>
public sealed class ClusterNode
{
    public ClusterNode(string nodeId)
    {
        NodeId = nodeId;
    }

    /// <summary>Unique identifier for this node (random hex, generated at startup).</summary>
    public string NodeId { get; }
}

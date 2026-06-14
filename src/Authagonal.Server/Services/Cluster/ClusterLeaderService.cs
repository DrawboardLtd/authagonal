using Authagonal.Core.Clustering;

namespace Authagonal.Server.Services.Cluster;

/// <summary>
/// Compatibility façade over <see cref="ILeaderElection"/>. Leader-gated background services
/// keep depending on this type and calling <see cref="IsLeader"/>; the actual election now runs
/// through the pluggable lease backend (in-process or Azure blob lease).
/// </summary>
public sealed class ClusterLeaderService(ILeaderElection election)
{
    /// <summary>This node's identifier.</summary>
    public string NodeId => election.NodeId;

    /// <summary>True if this node currently holds cluster leadership.</summary>
    public bool IsLeader() => election.IsLeader;

    /// <summary>The current leader's id (this node when it holds the lease; otherwise its own id as a fallback).</summary>
    public string GetLeaderId() => election.LeaderId ?? election.NodeId;
}

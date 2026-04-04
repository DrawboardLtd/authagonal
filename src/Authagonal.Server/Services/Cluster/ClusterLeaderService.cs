namespace Authagonal.Server.Services.Cluster;

/// <summary>
/// Simple leader election built on top of the existing gossip protocol.
/// The node with the lexicographically lowest NodeId among all live peers is the leader.
/// Since gossip prunes stale peers, leadership transfers automatically when the leader dies.
/// On a single-node cluster, this node is always the leader.
/// </summary>
public sealed class ClusterLeaderService
{
    private readonly ClusterNode _node;
    private readonly PeerRegistry _peerRegistry;

    public ClusterLeaderService(ClusterNode node, PeerRegistry peerRegistry)
    {
        _node = node;
        _peerRegistry = peerRegistry;
    }

    /// <summary>The local node's ID.</summary>
    public string NodeId => _node.NodeId;

    /// <summary>
    /// Returns true if this node is the current cluster leader.
    /// Leader = lowest NodeId among all known live nodes (self + peers).
    /// </summary>
    public bool IsLeader()
    {
        var myId = _node.NodeId;
        var peers = _peerRegistry.GetPeers();

        foreach (var peer in peers)
        {
            if (string.Compare(peer.NodeId, myId, StringComparison.Ordinal) < 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the NodeId of the current leader.
    /// </summary>
    public string GetLeaderId()
    {
        var leaderId = _node.NodeId;
        var peers = _peerRegistry.GetPeers();

        foreach (var peer in peers)
        {
            if (string.Compare(peer.NodeId, leaderId, StringComparison.Ordinal) < 0)
                leaderId = peer.NodeId;
        }

        return leaderId;
    }
}

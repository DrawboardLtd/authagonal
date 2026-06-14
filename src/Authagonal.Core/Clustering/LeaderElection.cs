namespace Authagonal.Core.Clustering;

/// <summary>
/// Mutable leadership state: written by the leader-election loop after each lease attempt and
/// read by callers via <see cref="ILeaderElection"/>. Registered as a singleton.
/// </summary>
public sealed class LeaderElection : ILeaderElection
{
    private volatile bool _isLeader;

    public LeaderElection(string nodeId)
    {
        NodeId = nodeId;
    }

    public bool IsLeader => _isLeader;
    public string NodeId { get; }
    public string? LeaderId => _isLeader ? NodeId : null;

    /// <summary>Updated by the election loop after each lease acquire/renew attempt.</summary>
    public void Update(bool isLeader) => _isLeader = isLeader;
}

namespace Authagonal.Core.Clustering;

/// <summary>
/// Read-only view of cluster leadership. Background work that must run on exactly one node
/// guards on <see cref="IsLeader"/>. Backed by a renewable lease that the leader-election
/// loop maintains; leadership transfers automatically when the holder stops renewing.
/// </summary>
public interface ILeaderElection
{
    /// <summary>True if this node currently holds the cluster leadership lease.</summary>
    bool IsLeader { get; }

    /// <summary>This node's stable identifier for the lifetime of the process.</summary>
    string NodeId { get; }

    /// <summary>This node's id when it holds leadership; otherwise null (the holder isn't directly observable).</summary>
    string? LeaderId { get; }
}

namespace Authagonal.Core.Clustering;

/// <summary>
/// Mutable leadership state: written by the leader-election loop after each lease attempt and
/// read by callers via <see cref="ILeaderElection"/>. Registered as a singleton.
/// </summary>
public sealed class LeaderElection : ILeaderElection
{
    private volatile bool _isLeader;
    private long _leaderUntilTicks;

    public LeaderElection(string nodeId)
    {
        NodeId = nodeId;
    }

    /// <summary>
    /// True only while this node holds a lease that has not yet expired by its own clock.
    /// </summary>
    /// <remarks>
    /// This returned the last value the election loop wrote, with no expiry of its own — so a node
    /// whose loop stalled (a GC pause, a blocked thread, a hung lease-store call) kept answering true
    /// long after the lease had actually lapsed and another node had taken it. Both then believed
    /// they were leader, which is precisely what leader election exists to prevent: the guarded work
    /// here is signing-key generation and the expiry reaper, so dual leadership means two nodes
    /// minting keys or sweeping the same rows.
    /// <para>
    /// The local deadline is the safe direction — it can only ever make a node stop believing it is
    /// leader, never start.
    /// </para>
    /// </remarks>
    public bool IsLeader =>
        _isLeader && DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _leaderUntilTicks);

    public string NodeId { get; }
    public string? LeaderId => IsLeader ? NodeId : null;

    /// <summary>Updated by the election loop after each lease acquire/renew attempt.</summary>
    /// <param name="leaseTtl">
    /// How long the lease just acquired or renewed remains valid. The deadline is taken from the
    /// moment of the successful call, so a slow renewal shortens the window rather than extending it.
    /// </param>
    public void Update(bool isLeader, TimeSpan leaseTtl)
    {
        Interlocked.Exchange(
            ref _leaderUntilTicks,
            isLeader ? DateTimeOffset.UtcNow.Add(leaseTtl).UtcTicks : 0);
        _isLeader = isLeader;
    }
}

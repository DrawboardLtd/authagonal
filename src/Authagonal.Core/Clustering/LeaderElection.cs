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
    /// <param name="leaseTtl">How long the lease just acquired or renewed remains valid.</param>
    /// <remarks>
    /// Prefer the overload taking <c>requestedAt</c>. This one dates the lease from NOW, which is after the
    /// backend granted it — see the other overload for why that is the wrong direction.
    /// </remarks>
    public void Update(bool isLeader, TimeSpan leaseTtl)
        => Update(isLeader, DateTimeOffset.UtcNow, leaseTtl);

    /// <summary>Updated by the election loop, dating the lease from before the call that granted it.</summary>
    /// <param name="requestedAt">
    /// When the acquire/renew was ISSUED, captured before awaiting it.
    /// </param>
    /// <param name="leaseTtl">How long the lease remains valid, measured by the backend from when it granted it.</param>
    /// <remarks>
    /// The deadline has to be derived from before the round trip, not after it. The backend's lease expires
    /// <paramref name="leaseTtl"/> after the BACKEND processed the call, which is earlier than the moment the
    /// response arrives by the whole latency of the call — so computing <c>UtcNow.Add(ttl)</c> on return
    /// overshoots the real expiry by exactly that latency. The parameter documentation used to assert the
    /// opposite ("a slow renewal shortens the window rather than extending it") while the code did the
    /// extending, and a stalled lease-store call is precisely the case the local deadline exists to contain:
    /// the longer the call takes, the further past the real expiry this node believed it was still leader.
    /// <para>
    /// A round trip longer than the TTL therefore yields a deadline already in the past, and
    /// <see cref="IsLeader"/> reports false with no special case — which is the correct answer.
    /// </para>
    /// </remarks>
    public void Update(bool isLeader, DateTimeOffset requestedAt, TimeSpan leaseTtl)
    {
        Interlocked.Exchange(
            ref _leaderUntilTicks,
            isLeader ? requestedAt.Add(leaseTtl).UtcTicks : 0);
        _isLeader = isLeader;
    }

    /// <summary>
    /// Leader for the life of the process, for a node deliberately running outside a cluster.
    /// </summary>
    /// <remarks>
    /// Standalone mode used to be expressed as <c>Update(true, Timeout.InfiniteTimeSpan)</c>, and
    /// <see cref="Timeout.InfiniteTimeSpan"/> is <c>new TimeSpan(0, 0, 0, 0, -1)</c> — a NEGATIVE duration.
    /// So the deadline landed one millisecond in the PAST and <see cref="IsLeader"/> was false forever,
    /// while the log line said "running standalone as permanent leader". Every leader-gated job silently
    /// never ran on a node with <c>Cluster:Enabled=false</c>: signing-key generation, the expiry reaper,
    /// grant reconciliation. An explicit mode rather than a very large TTL, so the intent is stated rather
    /// than encoded in a magic number that date arithmetic can misread again.
    /// </remarks>
    public void MarkPermanentLeader()
    {
        Interlocked.Exchange(ref _leaderUntilTicks, long.MaxValue);
        _isLeader = true;
    }
}

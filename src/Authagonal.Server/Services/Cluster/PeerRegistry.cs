using System.Collections.Concurrent;

namespace Authagonal.Server.Services.Cluster;

public sealed class PeerRegistry
{
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new(StringComparer.OrdinalIgnoreCase);

    public void AddOrRefresh(string nodeId, string httpAddress)
    {
        _peers.AddOrUpdate(
            nodeId,
            new PeerInfo(nodeId, httpAddress, DateTimeOffset.UtcNow),
            (_, existing) => existing with { HttpAddress = httpAddress, LastSeen = DateTimeOffset.UtcNow });
    }

    public void Remove(string nodeId) => _peers.TryRemove(nodeId, out _);

    public IReadOnlyList<PeerInfo> GetPeers() => _peers.Values.ToList();

    public void Prune(TimeSpan staleAfter)
    {
        var cutoff = DateTimeOffset.UtcNow - staleAfter;
        foreach (var kvp in _peers)
        {
            if (kvp.Value.LastSeen < cutoff)
                _peers.TryRemove(kvp.Key, out _);
        }
    }
}

public sealed record PeerInfo(string NodeId, string HttpAddress, DateTimeOffset LastSeen);

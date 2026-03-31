namespace Authagonal.Server.Services.Cluster;

public sealed class GossipMessage
{
    public required string NodeId { get; set; }
    public required Dictionary<string, WindowCounter> Counters { get; set; }
}

public sealed class GossipResponse
{
    public required string NodeId { get; set; }
    public bool Self { get; set; }
    public Dictionary<string, WindowCounter>? Counters { get; set; }
}

public sealed class WindowCounter
{
    public int Count { get; set; }
    public DateTimeOffset WindowStart { get; set; }
}
